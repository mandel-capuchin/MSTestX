// Copyright 2026 Esri
// 
// Licensed under the Apache License Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MSTestX.Console
{
    internal sealed class TestRunDiagnostics
    {
        public TestRunDiagnostics(string appLogPath)
        {
            AppLogPath = string.IsNullOrWhiteSpace(appLogPath) ? "(none)" : appLogPath;
        }

        public string Phase { get; private set; } = "starting";
        public int SelectedTestCount { get; private set; }
        public int ResultCount { get; private set; }
        public string LastStartedTest { get; private set; } = "(none)";
        public string LastTestResult { get; private set; } = "(none)";
        public string LastReceivedMessage { get; private set; } = "(none)";
        public string LastException { get; private set; } = "(none)";
        public bool ExecutionCompleteReceived { get; private set; }
        public string AppLogPath { get; }

        public void SetPhase(string phase)
        {
            Phase = string.IsNullOrWhiteSpace(phase) ? "(unknown)" : phase;
        }

        public void RecordSelectedTests(int count)
        {
            SelectedTestCount = count;
        }

        public void RecordMessage(string messageType)
        {
            LastReceivedMessage = string.IsNullOrWhiteSpace(messageType)
                ? "(none)"
                : messageType;
            if (messageType == MessageType.ExecutionComplete)
                ExecutionCompleteReceived = true;
        }

        public void RecordResult(string testName, string outcome, TimeSpan duration)
        {
            ResultCount++;
            LastTestResult = string.Format(
                "{0} {1} {2}",
                string.IsNullOrWhiteSpace(outcome) ? "(unknown)" : outcome,
                string.IsNullOrWhiteSpace(testName) ? "(unnamed test)" : testName,
                duration.FormatDuration());
        }

        public void RecordTestStart(string testName)
        {
            LastStartedTest = string.IsNullOrWhiteSpace(testName)
                ? "(unnamed test)"
                : testName;
        }

        public void RecordException(System.Exception exception)
        {
            LastException = exception?.GetType().Name + ": " + exception?.Message;
        }

        public string FormatAbortSummary()
        {
            return string.Join("; ", new[]
            {
                "MSTestX abort: adapter disconnected before ExecutionComplete",
                $"phase={Phase}",
                $"selectedTests={SelectedTestCount}",
                $"results={ResultCount}",
                $"lastStarted={LastStartedTest}",
                $"lastResult={LastTestResult}",
                $"lastMessage={LastReceivedMessage}",
                $"executionCompleteReceived={ExecutionCompleteReceived}",
                $"appLog={AppLogPath}",
                $"lastException={LastException}"
            });
        }
    }

    internal sealed class TestRunAbortedException : Exception
    {
        public TestRunAbortedException(
            TestRunDiagnostics diagnostics,
            Exception innerException)
            : base(diagnostics.FormatAbortSummary(), innerException)
        {
            Diagnostics = diagnostics;
        }

        public TestRunDiagnostics Diagnostics { get; }
    }

    internal sealed class TestRunCompletedWithErrorException : Exception
    {
        public TestRunCompletedWithErrorException(TestRunCompleteEventArgs completion)
            : base(TestRunner.FormatCompletionFailureMessage(completion))
        {
        }
    }

    public class TestRunner : IDisposable
    {
        private SocketCommunicationManager socket;
        private readonly System.Net.IPEndPoint _endpoint;
        private readonly TimeSpan _adapterConnectionTimeout;
        private TestRunDiagnostics diagnostics = new TestRunDiagnostics(null);

        internal static string GetRedirectedOutcomeLabel(Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome outcome)
        {
            return outcome switch
            {
                Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Passed => "PASS",
                Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Skipped => "SKIP",
                Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Failed => "FAIL",
                _ => outcome.ToString().ToUpperInvariant(),
            };
        }

        internal static string FormatRedirectedRunningTestLine(string testName)
        {
            return $"RUN  {testName}";
        }

        internal static string FormatRedirectedResultLine(Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome outcome, string testName, TimeSpan duration)
        {
            return $"{GetRedirectedOutcomeLabel(outcome)} {testName} {duration.FormatDuration()}";
        }

        internal static bool ShouldWriteTestResult(Guid parentExecId, bool isOutputRedirected)
        {
            return !isOutputRedirected || parentExecId == Guid.Empty;
        }

        internal static bool ShouldWriteRunningTest(Guid parentExecId, bool isOutputRedirected)
        {
            // Start events do not reliably carry ParentExecId for child/data-driven executions,
            // so redirected RUN lines cannot be matched to top-level tests without producing orphans.
            return !isOutputRedirected;
        }

        internal static bool ShouldWriteFailureDetails(Guid parentExecId, bool isOutputRedirected, string testMessage)
        {
            return ShouldWriteTestResult(parentExecId, isOutputRedirected) && !string.IsNullOrEmpty(testMessage);
        }

        internal static bool IsDisconnectBeforeExecutionComplete(TestRunDiagnostics diagnostics)
        {
            return diagnostics != null && !diagnostics.ExecutionCompleteReceived;
        }

        internal static long GetOutcomeCount(
            ITestRunStatistics statistics,
            Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome outcome)
        {
            if (statistics?.Stats == null)
                return 0;

            return statistics.Stats.TryGetValue(outcome, out var count) ? count : 0;
        }

        internal static bool IsUnsuccessfulCompletion(TestRunCompleteEventArgs completion)
        {
            return completion?.Error != null
                || completion?.IsAborted == true
                || completion?.IsCanceled == true;
        }

        internal static bool HasFailedTests(ITestRunStatistics statistics)
        {
            return GetOutcomeCount(
                statistics,
                Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Failed) > 0;
        }

        internal static string FormatCompletionFailureMessage(TestRunCompleteEventArgs completion)
        {
            if (completion?.Error != null)
                return "Test run completed with error: " + completion.Error.Message;
            if (completion?.IsAborted == true)
                return "Test run completed as aborted.";
            if (completion?.IsCanceled == true)
                return "Test run completed as canceled.";

            return "Test run completed successfully.";
        }

        public TestRunner(
            System.Net.IPEndPoint endpoint = null,
            TimeSpan? adapterConnectionTimeout = null)
        {
            _endpoint = endpoint;
            _adapterConnectionTimeout = adapterConnectionTimeout ?? TimeSpan.FromSeconds(60);
            if (_adapterConnectionTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(adapterConnectionTimeout));
        }

        public async Task RunTests(
            string outputFilename,
            string settingsXml,
            CancellationToken cancellationToken,
            string appLogFilename = null)
        {
            diagnostics = new TestRunDiagnostics(appLogFilename);
            var loggerEvents = new TestLoggerEventsImpl();
            var logger = new Microsoft.VisualStudio.TestPlatform.Extensions.TrxLogger.TrxLogger();
            var parameters = new Dictionary<string, string>() { { "TestRunDirectory", "." } };
            if (!string.IsNullOrEmpty(outputFilename))
                parameters.Add("LogFileName", outputFilename);
            logger.Initialize(loggerEvents, parameters);
            try
            {
                await RunTestsInternal(outputFilename, settingsXml, loggerEvents, cancellationToken);
            }
            catch (TestRunCompletedWithErrorException)
            {
                throw;
            }
            catch (TestRunAbortedException)
            {
                if (loggerEvents != null)
                {
                    var result = new TestRunCompleteEventArgs(null, false, true, null, null, TimeSpan.Zero); //TRXLogger doesn't use these values anyway
                    loggerEvents?.OnTestRunComplete(result);
                }
                throw;
            }
            catch (System.Exception ex)
            {
                if (loggerEvents != null)
                {
                    var result = new TestRunCompleteEventArgs(null, false, true, null, null, TimeSpan.Zero); //TRXLogger doesn't use these values anyway
                    loggerEvents?.OnTestRunComplete(result);
                }
                diagnostics.RecordException(ex);
                throw;
            }
            finally
            {
                socket?.StopClient();
            }
        }

        private async Task RunTestsInternal(string outputFilename, string settingsXml, TestLoggerEventsImpl loggerEvents, CancellationToken cancellationToken)
        {
            diagnostics.SetPhase("connecting to test adapter");
            System.Console.WriteLine("Waiting for connection to test adapter...");
            using var connectionTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionTimeoutSource.CancelAfter(_adapterConnectionTimeout);
            try
            {
                socket = new SocketCommunicationManager();
                await socket.SetupClientAsync(
                        _endpoint ?? new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 38300))
                    .WaitAsync(connectionTimeoutSource.Token)
                    .ConfigureAwait(false);
                if (!socket.WaitForServerConnection(0))
                    throw new IOException("The test adapter socket did not report a completed connection.");

                socket.SendMessage(MessageType.SessionConnected); //Start session

                //Perform version handshake
                diagnostics.SetPhase("waiting for version check");
                Message handshakeMessage = await ReceiveMessageAsync(connectionTimeoutSource.Token).ConfigureAwait(false);
                if (handshakeMessage?.MessageType != MessageType.VersionCheck)
                    throw new InvalidOperationException("Handshake failed: the adapter did not send VersionCheck.");

                _ = JsonDataSerializer.Instance.DeserializePayload<int>(handshakeMessage);
                System.Console.WriteLine("Connected to test adapter");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket?.StopClient();
                throw new TimeoutException(
                    $"Timed out after {_adapterConnectionTimeout.TotalSeconds:0} seconds waiting for the test adapter connection and VersionCheck handshake. " +
                    "Verify that the app is running, tcp:38300 is forwarded through the selected ADB server, and the device is online.");
            }

            Message msg;

            // Get tests
            diagnostics.SetPhase("requesting discovery");
            socket.SendMessage(MessageType.StartDiscovery,
                new DiscoveryRequestPayload()
                {
                    Sources = new string[] { },
                    RunSettings = settingsXml ?? @"<?xml version=""1.0"" encoding=""utf-8""?><RunSettings><RunConfiguration /></RunSettings>",
                    TestPlatformOptions = null
                });

            int pid = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                msg = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (msg == null)
                {
                    continue;
                }

                if (msg.MessageType == MessageType.TestHostLaunched)
                {
                    var thl = JsonDataSerializer.Instance.DeserializePayload<TestHostLaunchedPayload>(msg);
                    pid = thl.ProcessId;
                    System.Console.WriteLine($"Test Host Launched. Process ID '{pid}'");
                }
                else if (msg.MessageType == MessageType.DiscoveryInitialize)
                {
                    diagnostics.SetPhase("discovering tests");
                    System.Console.Write("Discovering tests...");
                    loggerEvents?.OnDiscoveryStart(new DiscoveryStartEventArgs(new DiscoveryCriteria()));
                }
                else if (msg.MessageType == MessageType.DiscoveryComplete)
                {
                    var dcp = JsonDataSerializer.Instance.DeserializePayload<DiscoveryCompletePayload>(msg);
                    var selectedTests = TestCaseSelector.SelectTests(dcp.LastDiscoveredTests, settingsXml);
                    diagnostics.RecordSelectedTests(selectedTests.Count);
                    diagnostics.SetPhase("running tests");
                    System.Console.WriteLine($"Discovered {dcp.TotalTests} tests");

                    loggerEvents?.OnDiscoveryComplete(new DiscoveryCompleteEventArgs(dcp.TotalTests, false));
                    loggerEvents?.OnDiscoveredTests(new DiscoveredTestsEventArgs(selectedTests));
                    //Start testrun
                    socket.SendMessage(MessageType.TestRunSelectedTestCasesDefaultHost,
                        new TestRunRequestPayload() { TestCases = selectedTests, RunSettings = settingsXml });
                    loggerEvents?.OnTestRunStart(new TestRunStartEventArgs(new TestRunCriteria(selectedTests, 1)));
                }
                else if (msg.MessageType == MessageType.DataCollectionTestStart)
                {
                    var tcs = JsonDataSerializer.Instance.DeserializePayload<TestCaseStartEventArgs>(msg);
                    var testName = tcs.GetDisplayName();
                    diagnostics.RecordTestStart(testName);
                    var parentExecId = tcs.GetParentExecId();
                    if (System.Console.IsOutputRedirected)
                    {
                        if (ShouldWriteRunningTest(parentExecId, isOutputRedirected: true))
                        {
                            System.Console.WriteLine(FormatRedirectedRunningTestLine(testName));
                        }
                    }
                    else
                    {
                        System.Console.Write($"    {testName}");
                    }
                }
                else if (msg.MessageType == MessageType.DataCollectionTestEnd)
                {
                    //Skip
                }
                else if (msg.MessageType == MessageType.DataCollectionTestEndResult)
                {
                    var tr = JsonDataSerializer.Instance.DeserializePayload<Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection.TestResultEventArgs>(msg);
                    var testName = tr.GetDisplayName();

                    var outcome = tr.TestResult.Outcome;
                    diagnostics.RecordResult(testName, outcome.ToString(), tr.TestResult.Duration);

                    var parentExecId = tr.GetParentExecId();
                    if (outcome == Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Failed)
                    {
                    }
                    else if (outcome == Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Skipped)
                    {
                        System.Console.ForegroundColor = ConsoleColor.Yellow;
                    }
                    else if (outcome == Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Passed)
                    {
                        System.Console.ForegroundColor = ConsoleColor.Green;
                    }
                    if (!System.Console.IsOutputRedirected)
                    {
                        System.Console.SetCursorPosition(0, System.Console.CursorTop);
                    }
                    var isOutputRedirected = System.Console.IsOutputRedirected;
                    string testMessage = tr.TestResult?.ErrorMessage;
                    if (ShouldWriteTestResult(parentExecId, isOutputRedirected))
                    {
                        if (isOutputRedirected)
                        {
                            System.Console.WriteLine(FormatRedirectedResultLine(outcome, testName, tr.TestResult.Duration));
                        }
                        else
                        {
                            switch(outcome)
                            {
                                case Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Passed:
                                    System.Console.ForegroundColor = ConsoleColor.Green;
                                    System.Console.Write("  √ ");
                                    break;
                                case Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Skipped:
                                    System.Console.ForegroundColor = ConsoleColor.Yellow;
                                    System.Console.Write("  ! ");
                                    break;
                                case Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Failed:
                                    System.Console.ForegroundColor = ConsoleColor.Red;
                                    System.Console.Write("  X ");
                                    break;
                                default:
                                    System.Console.Write("    "); break;
                            }
                            System.Console.ResetColor();
                            System.Console.Write(testName);
                            System.Console.WriteLine($" {tr.TestResult.Duration.FormatDuration()}");
                        }

                        if (ShouldWriteFailureDetails(parentExecId, isOutputRedirected, testMessage))
                        {
                            System.Console.ForegroundColor = ConsoleColor.Red;
                            System.Console.WriteLine("  Error Message:");
                            System.Console.WriteLine("   " + testMessage);
                            if (!string.IsNullOrEmpty(tr.TestResult.ErrorStackTrace))
                            {
                                System.Console.WriteLine("  Stack Trace:");
                                System.Console.WriteLine("   " + tr.TestResult.ErrorStackTrace);
                            }
                            System.Console.ResetColor();
                            System.Console.WriteLine();
                            // If test failed, also output messages, if any
                            if (tr.TestResult.Messages?.Any() == true)
                            {
                                System.Console.WriteLine("  Standard Output Messages:");
                                foreach (var message in tr.TestResult.Messages)
                                {
                                    System.Console.WriteLine(message.Text);
                                }
                                System.Console.WriteLine();
                            }
                        }                        
                    }


                    // Make attachment paths absolute
                    foreach (var set in tr.TestResult.Attachments)
                    {
                        for (int i = 0; i < set.Attachments.Count; i++)
                        {
                            var uri = set.Attachments[i].Uri.OriginalString;

                            if (!set.Attachments[i].Uri.IsAbsoluteUri)
                            {
                                DirectoryInfo d = new DirectoryInfo(".");
                                var newPath = Path.Combine(d.FullName, uri);
                                newPath = newPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
                                set.Attachments[i] = new Microsoft.VisualStudio.TestPlatform.ObjectModel.UriDataAttachment(
                                    new Uri(newPath, UriKind.Relative), set.Attachments[i].Description);
                            }
                        }
                    }
                    loggerEvents?.OnTestResult(new Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging.TestResultEventArgs(tr.TestResult));
                }
                else if (msg.MessageType == MessageType.ExecutionComplete)
                {
                    diagnostics.SetPhase("processing execution complete");
                    var trc = JsonDataSerializer.Instance.DeserializePayload<TestRunCompletePayload>(msg);
                    loggerEvents?.OnTestRunComplete(trc.TestRunCompleteArgs);
                    var statistics = trc.LastRunTests?.TestRunStatistics ?? trc.TestRunCompleteArgs?.TestRunStatistics;
                    var elapsedTime = trc.TestRunCompleteArgs?.ElapsedTimeInRunningTests ?? TimeSpan.Zero;
                    var unsuccessfulCompletion = IsUnsuccessfulCompletion(trc.TestRunCompleteArgs);
                    if (trc.TestRunCompleteArgs?.Error != null)
                    {
                        diagnostics.RecordException(trc.TestRunCompleteArgs.Error);
                        System.Console.ForegroundColor = ConsoleColor.Red;
                        System.Console.WriteLine($"Run error: {trc.TestRunCompleteArgs.Error.Message}");
                        System.Console.ResetColor();
                    }
                    System.Console.WriteLine();
                    System.Console.WriteLine("Test Run Complete");
                    System.Console.WriteLine($"Total tests: {statistics?.ExecutedTests ?? 0} tests");
                    System.Console.ForegroundColor = ConsoleColor.Green;
                    System.Console.WriteLine($"     Passed : {GetOutcomeCount(statistics, Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Passed)} ");
                    System.Console.ForegroundColor = ConsoleColor.Red;
                    System.Console.WriteLine($"     Failed : {GetOutcomeCount(statistics, Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Failed)} ");
                    System.Console.ForegroundColor = ConsoleColor.Yellow;
                    System.Console.WriteLine($"    Skipped : {GetOutcomeCount(statistics, Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Skipped)} ");
                    System.Console.ResetColor(); 
                    System.Console.WriteLine($" Total time: {elapsedTime.TotalSeconds} Seconds");
                    diagnostics.SetPhase(unsuccessfulCompletion ? "complete with error" : "complete");
                    if (unsuccessfulCompletion)
                        throw new TestRunCompletedWithErrorException(trc.TestRunCompleteArgs);
                    return; //Test run is complete -> Exit message loop
                }
                else if (msg.MessageType == MessageType.AbortTestRun)
                {
                    throw new TaskCanceledException("Test Run Aborted!");
                }
                else if (msg.MessageType == MessageType.CancelTestRun)
                {
                    throw new TaskCanceledException("Test Run Cancelled!");
                }
                else if (msg.MessageType == MessageType.TestMessage)
                {
                    var tm = JsonDataSerializer.Instance.DeserializePayload<TestMessagePayload>(msg);
                    System.Console.WriteLine($"{tm.MessageLevel}: {tm.Message}");
                }
                else if (msg.MessageType == "AttachmentSet")
                {
                    var set = JsonDataSerializer.Instance.DeserializePayload<FileAttachmentSet>(msg);
                    foreach(var attachment in set.Attachments)
                    {
                        var path = attachment.Uri.OriginalString;
                        try
                        {
                            var dir = Path.GetDirectoryName(path);
                            if (!Directory.Exists(dir))
                                Directory.CreateDirectory(dir);
                            File.WriteAllBytes(path, attachment.Data);
                        }
                        catch { }
                    }
                }
                else
                {
                    System.Console.WriteLine($"Received: {msg.MessageType} -> {msg.Payload}");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task<Message> ReceiveMessageAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var msg = await Task.Run(socket.ReceiveMessage, CancellationToken.None)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (msg is null)
                {
                    throw new EndOfStreamException(
                        "The test adapter closed the connection without sending a message.");
                }

                diagnostics.RecordMessage(msg.MessageType);
                return msg;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (EndOfStreamException endOfStreamException)
            {
                diagnostics.SetPhase("adapter disconnected");
                throw new TestRunAbortedException(diagnostics, endOfStreamException);
            }
            catch (IOException ioException)
            {
                if (ioException.InnerException is SocketException socketException &&
                    socketException.SocketErrorCode == SocketError.TimedOut)
                {
                    diagnostics.SetPhase("adapter receive timeout");
                }
                else
                {
                    diagnostics.SetPhase("adapter I/O failure");
                }
                throw new TestRunAbortedException(diagnostics, ioException);
            }
            catch (Exception exception)
            {
                diagnostics.SetPhase("adapter protocol failure");
                throw new TestRunAbortedException(diagnostics, exception);
            }
        }

        public void Dispose()
        {
            socket?.StopClient();
        }

        private class TestLoggerEventsImpl : TestLoggerEvents
        {
            public void OnTestRunMessage(TestRunMessageEventArgs e) => TestRunMessage?.Invoke(this, e);
            public override event EventHandler<TestRunMessageEventArgs> TestRunMessage;

            public void OnTestRunStart(TestRunStartEventArgs e) => TestRunStart?.Invoke(this, e);
            public override event EventHandler<TestRunStartEventArgs> TestRunStart;

            public void OnTestResult(Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging.TestResultEventArgs e) => TestResult?.Invoke(this, e);
            public override event EventHandler<Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging.TestResultEventArgs> TestResult;

            public void OnTestRunComplete(TestRunCompleteEventArgs e) => TestRunComplete?.Invoke(this, e);
            public override event EventHandler<TestRunCompleteEventArgs> TestRunComplete;

            public void OnDiscoveryStart(DiscoveryStartEventArgs e) => DiscoveryStart?.Invoke(this, e);
            public override event EventHandler<DiscoveryStartEventArgs> DiscoveryStart;

            public void OnDiscoveryMessage(TestRunMessageEventArgs e) => DiscoveryMessage?.Invoke(this, e);
            public override event EventHandler<TestRunMessageEventArgs> DiscoveryMessage;

            public void OnDiscoveredTests(DiscoveredTestsEventArgs e) => DiscoveredTests?.Invoke(this, e);
            public override event EventHandler<DiscoveredTestsEventArgs> DiscoveredTests;

            public void OnDiscoveryComplete(DiscoveryCompleteEventArgs e) => DiscoveryComplete?.Invoke(this, e);
            public override event EventHandler<DiscoveryCompleteEventArgs> DiscoveryComplete;
        }


        [DataContract]
        private class FileAttachmentSet
        {
            [DataMember]
            public string Uri { get; set; }

            [DataMember]
            public string DisplayName { get; set; }

            [DataMember]
            public IList<FileDataAttachment> Attachments { get; set; }
        }
        [DataContract]
        private class FileDataAttachment
        {
            [DataMember]
            public string Description { get; set; }
            [DataMember]
            public Uri Uri { get; set; }
            private byte[] data;
            [DataMember]
            public byte[] Data { get; set; }
        }
    }
}
