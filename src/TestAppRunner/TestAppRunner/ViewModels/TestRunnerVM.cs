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

using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using MSTestX;


#if MAUI
using Microsoft.Maui.Controls;
#else
using Xamarin.Forms;
using static SQLite.SQLite3;
#endif

using MessageType = Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel.MessageType;

namespace TestAppRunner.ViewModels
{
    internal class TestRunnerVM : VMBase, ITestExecutionRecorder
    {
        private static TestRunner testRunner;
        private static Dictionary<Guid, TestResultVM> alltests;
        private Dictionary<Guid, TestResultVM> tests;
        private System.IO.StreamWriter logOutput;
        private TrxWriter trxWriter;
        private TestAdapterConnection connection;
        private CancellationTokenSource loopTcs;
        private bool isLoopingUntilFailure;
        private bool showStopAction;
        private bool isStopRequested;
        private int currentIteration;

        private static TestRunnerVM _Instance;

        public static TestRunnerVM Instance => _Instance ?? (_Instance = new TestRunnerVM());

        internal MSTestX.RunnerApp HostApp { get; set; }

        private TestRunnerVM()
        {   
        }

        public async void Initialize()
        {
            await LoadTests();
            int port = Settings.TestAdapterPort;
            if (port > 0)
            {
                InitializeTestAdapterConnection(port);
            }
        }

        internal static void ShowToast(string text)
        {
            Application.Current.Dispatcher.Dispatch(() =>
            {
                var duration = CommunityToolkit.Maui.Core.ToastDuration.Long;
                double fontSize = 14;
                var toast = CommunityToolkit.Maui.Alerts.Toast.Make(text, duration, fontSize);
                _ = toast.Show();
            });
        }

        private static async void PingHost(string host)
        {
            await Task.Delay(2000);
            TestAppRunner.ViewModels.TestRunnerVM.ShowToast("Pinging host: " + host);
            for (int i = 0; i < 100; i++)
            {

                using (System.Net.Sockets.TcpClient tcpScan = new System.Net.Sockets.TcpClient())
                {
                    try
                    {
                        await tcpScan.ConnectAsync(host, 38302);
                        return;
                    }
                    catch (System.Exception)
                    {
                    }
                }
            }
        }

        public async void InitializeTestAdapterConnection(int port)
        {
            OnPropertyChanged(nameof(Status));
            //TODO: Get iOS mlaunch port number
            connection = new TestAdapterConnection(port);
            if (!string.IsNullOrEmpty(Settings.RemoteHost))
                PingHost(Settings.RemoteHost);

            try
            {
                await connection.StartAsync();
                ShowToast("Remote test adapter connected!");
            }
            catch (System.Exception ex)
            {
                ShowToast("Failed to open test adapter socket: " + ex.Message);
            }
        }

        private async Task LoadTests()
        {
            Status = "Loading tests...";
            OnPropertyChanged(nameof(Status));
            if (testRunner == null)
            {
                await Task.Run(() =>
                {
                    var tests = new Dictionary<Guid, TestResultVM>();
                    var asm = this.Settings.TestAssemblies ?? AppDomain.CurrentDomain.GetAssemblies().Where(c => !c.IsDynamic).ToArray();
                    var references = asm.Select(c => string.IsNullOrEmpty(c.Location)
                        ? $"{c.GetName().Name}.dll"
                        : System.IO.Path.GetFileName(c.Location)).ToArray();
                    testRunner = new TestRunner(references, this);
                    foreach (var item in testRunner.Tests)
                    {
                        tests[item.Id] = new TestResultVM(item);
                    }
                    alltests = this.tests = tests;

                    if (!this.Settings.AutoRun || this.Settings.AutoResume)
                    {
                    var previousResults = LoadProgress().ToArray();
                    try
                    {
                        if (previousResults != null)
                        {
                            Parallel.ForEach(previousResults, (previousResult) =>
                            {
                                var candidates = tests.Where(tt => tt.Value.Test.FullyQualifiedName == previousResult.FullyQualifiedName).Select(tt => tt.Value);
                                TestResultVM t = null;
                                if (candidates.Count() == 1)
                                    t = candidates.Single();
                                else
                                    t = candidates.Where(c => previousResult.DisplayName == c.DisplayName).FirstOrDefault();
                                if (t != null)
                                {
                                    try
                                    {
                                        t.Result = MSTestX.UnitTestRunner.TestResultSerializer.Deserialize(previousResult.Result, t.Test);
                                    }
                                    catch(System.Exception ex)
                                    {
                                        Debug.WriteLine($"Failed to deserialize {t.Test.FullyQualifiedName}: " + ex.Message);
                                        Debug.WriteLine(ex.StackTrace);
                                        return; 
                                    }
                                }
                                else
                                {
                                    // TODO: Remove from sql database
                                }
                            });
                            OnPropertyChanged(nameof(PassedTests));
                            OnPropertyChanged(nameof(FailedTests));
                            OnPropertyChanged(nameof(SkippedTests));
                        }
                    }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to restore previous test progress: {ex}");
                        }
                    }


                    this.HostApp.RaiseTestsDiscovered(testRunner.Tests);
                });
                if (Settings.AutoResume)
                {
                    var _ = RunRemainingTests(Settings);
                }
                else if (Settings.AutoRun)
                {
                    var _ = Run(Settings);
                }
            }
            OnPropertyChanged(nameof(Tests));
            OnPropertyChanged(nameof(GroupedTests));
            OnPropertyChanged(nameof(TestStatus));
            OnPropertyChanged(nameof(NotRunTests));
            Status = $"{tests.Count} tests found.";
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsInitialized));
            OnRunActionStateChanged();
        }

        private readonly string progressPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unittest_progress.bin");
        private readonly string progressSqlPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "unittest_progress.sql");

        public IEnumerable<TestProgress> LoadProgress()
        {
            if (System.IO.File.Exists(progressSqlPath))
            {
                using (var conn = new SQLiteConnection(progressSqlPath))
                {
                    conn.CreateTable<TestProgress>();
                    foreach(var row in conn.Table<TestProgress>())
                    {
                        yield return row;
                    }
                }
            }
        }
        [Table("TestProgress")]
        public class TestProgress
        {
            [PrimaryKey, AutoIncrement, Column("_id")]
            public int Id { get; set; }

            [MaxLength(250)]
            public string FullyQualifiedName { get; set; }
            [MaxLength(250)]
            public string DisplayName { get; set; }

            [MaxLength(2500), Unique]
            public byte[] Result { get; set; }
        }

        public void DeleteProgress()
        {
            try
            {
                using (var conn = new SQLiteConnection(progressSqlPath))
                {
                    conn.DropTable<TestProgress>();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to clear saved test progress: {ex}");
            }
        }

        public void DeleteProgress(IEnumerable<TestCase> tests)
        {
            try
            {
                //var s = Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.JsonDataSerializer.Instance;
                using (var conn = new SQLiteConnection(progressSqlPath))
                {
                    conn.CreateTable<TestProgress>();

                    foreach (var test in tests)
                    {
                        //var str = s.Serialize<TestResult>(test.Result);
                        int count = conn.Table<TestProgress>().Delete(p => p.FullyQualifiedName == test.FullyQualifiedName && test.DisplayName == test.DisplayName);
                    }
                }
            }
            catch (System.Exception ex) { }
        }
        public void SaveProgress(IEnumerable<TestResultVM> tests, bool append = true)
        {
            try
            {
                tests = tests.Where(t => t.Result != null);
                using (var conn = new SQLiteConnection(progressSqlPath))
                {
                    conn.CreateTable<TestProgress>();

                    foreach (var test in tests)
                    {
                        conn.Table<TestProgress>().Delete(p => p.FullyQualifiedName == test.Test.FullyQualifiedName && p.DisplayName == test.Test.DisplayName);
                        using var s = new MemoryStream();
                        MSTestX.UnitTestRunner.TestResultSerializer.Serialize(s, test.Result);
                        conn.Insert(new TestProgress() { FullyQualifiedName = test.Test.FullyQualifiedName, DisplayName = test.DisplayName, Result = s.ToArray() });
                    }
                }
            }
            catch(System.Exception ex) { }
        }

        private string _grouping;
        internal void UpdateGroup(string grouping)
        {
            _grouping = grouping;
            if (tests != null)
            {
                if (grouping == "Category")
                    _GroupedTests = new List<TestResultGroup>(tests.Values.GroupBy(t => t.Category).Select((g, t) => new TestResultGroup(g.Key, g)).OrderBy(g => g.Group));
                else if (grouping == "Outcome")
                    _GroupedTests = new List<TestResultGroup>(tests.Values.GroupBy(t => t.Outcome).Select((g, t) => new TestResultGroup(g.Key.ToString(), g)).OrderBy(g => g.Group));
                else if (grouping == "Namespace")
                    _GroupedTests = new List<TestResultGroup>(tests.Values.GroupBy(t => t.Namespace).Select((g, t) => new TestResultGroup(g.Key, g)).OrderBy(g => g.Group));
                OnPropertyChanged(nameof(GroupedTests));
            }
        }

        public string Status { get; private set; }

        public string DiagnosticsInfo { get; private set; }

        public string TestStatus => $"{PassedTests} passed. {FailedTests} failed. {SkippedTests} skipped. {NotRunTests} not run. {Percentage.ToString("0")}%";

        private CancellationTokenSource tcs;

        public bool IsLoopingUntilFailure => isLoopingUntilFailure;

        public int CurrentIteration => currentIteration;

        public bool IsBusy => tcs != null || IsLoopingUntilFailure;

        public string RunUntilFailureActionText => showStopAction ? "Stop" : "Run until failure";

        public string LoopIterationText => $"Running until failure · iteration {CurrentIteration}";

        public bool CanRunTests => IsInitialized && !IsBusy && !isStopRequested;

        public bool CanRunUntilFailure =>
            IsInitialized &&
            !isStopRequested &&
            (!IsBusy || (IsLoopingUntilFailure && showStopAction));

        public event EventHandler<Exception> OnTestRunException;

        public void Cancel()
        {
            isStopRequested = true;
            showStopAction = false;
            OnRunActionStateChanged();
            CancelTokenSource(loopTcs);
            CancelTokenSource(tcs);
        }

        private void OnRunActionStateChanged()
        {
            OnPropertiesChanged(
                nameof(IsBusy),
                nameof(CanRunTests),
                nameof(CanRunUntilFailure),
                nameof(RunUntilFailureActionText));
        }

        private static void CancelTokenSource(CancellationTokenSource cancellationTokenSource)
        {
            if (cancellationTokenSource is null)
                return;

            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run finished and disposed the token source while the UI was handling a stop click.
            }
        }

        public Task<IEnumerable<TestResult>> Run()
        {
            if(testRunner == null || !testRunner.Tests.Any())
                return Task.FromResult(Enumerable.Empty<TestResult>());
            return Run(testRunner.Tests.OrderBy(tst => tst.FullyQualifiedName), Settings);
        }
        public Task<IEnumerable<TestResult>> Run(IRunSettings runSettings)
        {
            return Run(testRunner.Tests.OrderBy(tst => tst.FullyQualifiedName), runSettings);
        }

        private List<TestResult> results;

        public Task<IEnumerable<TestResult>> Run(IEnumerable<TestCase> testCollection)
        {
            return Run(testCollection, Settings);
        }

        public Task<IEnumerable<TestResult>> RunRemainingTests(IRunSettings runSettings = null)
        {
            return Run(Tests.Where(t => t.Result is null).Select(t => t.Test).ToArray(), runSettings ?? Settings);
        }

        public Task<IEnumerable<TestResult>> RunFailedTests(IRunSettings runSettings = null)
        {
            return Run(Tests?.Where(t => t.Result?.Outcome == TestOutcome.Failed).Select(t => t.Test).ToArray(), runSettings ?? Settings);
        }

        public async Task<IEnumerable<TestResult>> Run(IEnumerable<TestCase> testCollection, IRunSettings runSettings)
        {
            if (IsBusy)
                throw new InvalidOperationException("Can't begin a test run while another is in progress");
            try
            {
                var runOutcome = await Run_Internal(testCollection, runSettings, CancellationToken.None);
                return runOutcome.Results;
            }
            catch(System.Exception ex)
            {
                Logger.Log($"Test run failed: {ex.Message}\nStack trace: {ex.StackTrace}");
                OnTestRunException?.Invoke(this, ex);
                throw;
            }
        }

        internal async Task<IEnumerable<TestResult>> RunFromRemoteAdapter(IEnumerable<TestCase> testCollection, IRunSettings runSettings)
        {
            if (IsBusy)
                throw new InvalidOperationException("Can't begin a test run while another is in progress");
            try
            {
                var runOutcome = await Run_Internal(testCollection, runSettings, CancellationToken.None, allowTerminate: false);
                if (runOutcome.ExecutionFailed)
                    throw new InvalidOperationException("Remote adapter test run failed before completion.");

                return runOutcome.Results;
            }
            catch (System.Exception ex)
            {
                Logger.Log($"Remote adapter test run failed: {ex.Message}\nStack trace: {ex.StackTrace}");
                OnTestRunException?.Invoke(this, ex);
                throw;
            }
        }

        internal void TerminateAfterRemoteAdapterCompletion()
        {
            if (Settings.TerminateAfterExecution)
            {
                LogLifecycle("terminating after remote adapter completion");
                Terminate();
            }
        }

        internal static void LogLifecycle(string message)
        {
            var line = "MSTestX app: " + message;
            Console.WriteLine(line);
            Trace.TraceInformation(line);
        }

        public async Task<IEnumerable<TestResult>> RunUntilFailure(IEnumerable<TestCase> testCollection, IRunSettings runSettings, CancellationToken cancellationToken)
        {
            if (IsBusy)
                throw new InvalidOperationException("Can't begin a test run while another is in progress");

            var selectedTests = testCollection?.OrderBy(t => t.FullyQualifiedName).ToArray() ?? Array.Empty<TestCase>();
            if (!selectedTests.Any())
                return Enumerable.Empty<TestResult>();

            try
            {
                isLoopingUntilFailure = true;
                currentIteration = 0;
                loopTcs = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                showStopAction = true;
                OnPropertyChanged(nameof(IsLoopingUntilFailure));
                OnPropertyChanged(nameof(CurrentIteration));
                OnPropertyChanged(nameof(LoopIterationText));
                OnRunActionStateChanged();

                List<TestResult> lastResults = new();
                while (!loopTcs.IsCancellationRequested)
                {
                    currentIteration++;
                    Status = $"Running iteration {currentIteration} until failure...";
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(CurrentIteration));
                    OnPropertyChanged(nameof(LoopIterationText));

                    var iterationOutcome = await Run_Internal(selectedTests, runSettings ?? Settings, loopTcs.Token, allowTerminate: false);
                    var iterationResults = iterationOutcome.Results.ToList();
                    lastResults = iterationResults;

                    if (loopTcs.IsCancellationRequested)
                    {
                        Status = $"Run until failure canceled during iteration {currentIteration}.";
                        OnPropertyChanged(nameof(Status));
                        break;
                    }

                    if (iterationOutcome.ExecutionFailed)
                    {
                        Status = $"Run until failure stopped after iteration {currentIteration} due to an execution error.";
                        OnPropertyChanged(nameof(Status));
                        break;
                    }

                    var childResults = iterationResults.Where(tt => GetProperty<int>("InnerResultsCount", tt, 0) == 0);
                    if (childResults.Any(result => result.Outcome == TestOutcome.Failed))
                    {
                        Status = $"Run until failure stopped after iteration {currentIteration}.";
                        OnPropertyChanged(nameof(Status));
                        break;
                    }
                }

                if (Settings.TerminateAfterExecution)
                {
                    Terminate();
                }

                return lastResults;
            }
            catch (System.Exception ex)
            {
                Logger.Log($"Run until failure failed: {ex.Message}\nStack trace: {ex.StackTrace}");
                OnTestRunException?.Invoke(this, ex);
                throw;
            }
            finally
            {
                loopTcs?.Dispose();
                loopTcs = null;
                isLoopingUntilFailure = false;
                isStopRequested = false;
                showStopAction = false;
                OnPropertyChanged(nameof(IsLoopingUntilFailure));
                OnPropertyChanged(nameof(LoopIterationText));
                OnRunActionStateChanged();
            }
        }

        public Task<IEnumerable<TestResult>> RunUntilFailure(IEnumerable<TestCase> testCollection, IRunSettings runSettings = null)
            => RunUntilFailure(testCollection, runSettings ?? Settings, CancellationToken.None);

        private async Task<(IEnumerable<TestResult> Results, bool ExecutionFailed)> Run_Internal(IEnumerable<TestCase> testCollection, IRunSettings runSettings, CancellationToken cancellationToken, bool allowTerminate = true)
        {
            var selectedTests = testCollection.OrderBy(tst => tst.FullyQualifiedName).ToArray();
            LogLifecycle($"run started: selectedTests={selectedTests.Length}");
            HostApp?.RaiseTestRunStarted(testCollection);
            var t = tcs = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            isStopRequested = false;
            var executionFailed = false;
            Status = $"Running tests...";
            OnPropertyChanged(nameof(Status));
            DiagnosticsInfo = "";
            OnPropertyChanged(nameof(DiagnosticsInfo));
            foreach (var item in selectedTests)
            {
                tests[item.Id].Result = null;
            }
            OnPropertyChanged(nameof(TestStatus));
            OnPropertyChanged(nameof(NotRunTests));
            OnPropertyChanged(nameof(PassedTests));
            OnPropertyChanged(nameof(FailedTests));
            OnPropertyChanged(nameof(SkippedTests));
            OnPropertyChanged(nameof(Percentage));

            results = new List<TestResult>();
            if (!string.IsNullOrEmpty(Settings.ProgressLogPath))
            {
                var s = System.IO.File.OpenWrite(Settings.ProgressLogPath);
                logOutput = new System.IO.StreamWriter(s);
                logOutput.WriteLine("*************************************************");
                logOutput.WriteLine($"* Starting Test Run @ {DateTime.Now}");
                logOutput.WriteLine("*************************************************");
                Logger.Log($"LOGREPORT LOCATION: {Settings.ProgressLogPath}");
            }
            if (!string.IsNullOrEmpty(Settings.TrxOutputPath))
            {
                trxWriter = new TrxWriter(Settings.TrxOutputPath);
            }
            
            if (selectedTests.Length == Tests.Count())
                DeleteProgress();
            else
                DeleteProgress(selectedTests);
            DateTime start = DateTime.Now;
            Logger.Log($"STARTING TESTRUN {selectedTests.Length} Tests");
            var task = testRunner.Run(selectedTests, runSettings, t.Token);
            OnPropertyChanged(nameof(IsRunning));
            OnRunActionStateChanged();
            try
            {
                await task;
                if (t.IsCancellationRequested)
                {
                    Status = $"Test run canceled.";
                    
                }
                else
                {
                    Status = $"Test run completed.";
                }
                LogLifecycle($"run completed: status={Status}");
            }
            catch (System.Exception ex)
            {
                executionFailed = true;
                Status = $"Test run failed to run: {ex.Message}";
                LogLifecycle($"run failed: {ex.Message}");
            }
            DateTime end = DateTime.Now;
            foreach (var item in selectedTests)
            {
                var vmTest = tests[item.Id];
                if (vmTest.Result is null)
                {
                    vmTest.ClearInProgress();
                }
            }
            CurrentTestRunning = null;
            OnPropertyChanged(nameof(CurrentTestRunning));
            if (logOutput != null)
            {
                Log("*************************************************");
                Log(Status);
                Log(TestStatus);
                Log("*************************************************\n\n");
                logOutput.Dispose();
                logOutput = null;
            }
            if (trxWriter != null)
            {
                try
                {
#if DEBUG
                    File.Delete(Settings.TrxOutputPath);
#endif
                    trxWriter?.OnTestRunComplete(new Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.TestRunCompleteEventArgs(null, false, false, null, null, TimeSpan.Zero));
#if DEBUG
                    var trx = File.ReadAllText(Settings.TrxOutputPath);
                    System.Diagnostics.Debug.WriteLine(trx);
#endif
                }
                catch (PlatformNotSupportedException) // Throws due to https://github.com/microsoft/vstest/issues/4736. However it's thrown after TRX is written
                {
                    Debug.Assert(File.Exists(Settings.TrxOutputPath));
#if DEBUG
                    var trx = File.ReadAllText(Settings.TrxOutputPath);
                    System.Diagnostics.Debug.WriteLine(trx);
#endif
                }
                trxWriter = null;
                Logger.Log($"TRXREPORT LOCATION: {Settings.TrxOutputPath}");
            }
            var childResults = results.Where(tt => GetProperty<int>("InnerResultsCount", tt, 0) == 0); // Avoid counting parent tests
            DiagnosticsInfo += $"\nLast run duration: {(end - start).ToString("c")}";
            DiagnosticsInfo += $"\n{childResults.Where(a => a.Outcome == TestOutcome.Passed).Count()} passed - {childResults.Where(a => a.Outcome == TestOutcome.Failed).Count()} failed";
            if (Settings.ProgressLogPath != null) DiagnosticsInfo += $"\nLog: {Settings.ProgressLogPath}";
            if (Settings.TrxOutputPath != null) DiagnosticsInfo += $"\nTRX Report: {Settings.TrxOutputPath}";
            OnPropertyChanged(nameof(DiagnosticsInfo));
            isStopRequested = false;
            OnPropertyChanged(nameof(IsRunning));
            OnRunActionStateChanged();
            OnPropertyChanged(nameof(Status));
            HostApp?.RaiseTestRunCompleted(results);

            Logger.Log($"COMPLETED TESTRUN Total:{childResults.Count()} Failed:{childResults.Where(a => a.Outcome == TestOutcome.Failed).Count()} Passed:{childResults.Where(a => a.Outcome == TestOutcome.Passed).Count()}  Skipped:{childResults.Where(a => a.Outcome == TestOutcome.Skipped).Count()}");
            if (allowTerminate && Settings.TerminateAfterExecution)
            {
                LogLifecycle("terminating after direct run completion");
                Terminate();
            }
            tcs.Dispose();
            tcs = null;
            return (results, executionFailed);
        }

        private void Terminate()
        {
#if __IOS__
            var selector = new ObjCRuntime.Selector("terminateWithSuccess");
            UIKit.UIApplication.SharedApplication.PerformSelector(selector, UIKit.UIApplication.SharedApplication, 0);
#elif __ANDROID__
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
            Environment.Exit(0);
#endif
        }

        public bool IsRunning => testRunner?.IsRunning ?? false;
        public bool IsInitialized => Tests?.Any() == true;
        public int PassedTests => Tests?.SelectMany(t=>t.Results).Where(t => t.Outcome == TestOutcome.Passed).Count() ?? 0;
        public int PassedTestsWithoutChildren => Tests?.Where(t => t.Result?.Outcome == TestOutcome.Passed).Count() ?? 0;
        public int FailedTests => Tests?.SelectMany(t => t.Results)?.Where(t => t?.Outcome == TestOutcome.Failed).Count() ?? 0;
        public int SkippedTests => Tests?.SelectMany(t => t.Results)?.Where(t => t?.Outcome == TestOutcome.Skipped).Count() ?? 0;
        public int NotRunTests => Tests?.Where(t => t.Result == null).Count() ?? 0;
        public double Percentage => Tests?.Any(t => t.Result?.Outcome == TestOutcome.Passed) == true ? (int)(PassedTestsWithoutChildren * 100d / (FailedTests + PassedTestsWithoutChildren)) : 0;
        public double Progress => Tests == null || Tests.Count() == 0 ? 0 : 1 - (NotRunTests / (double)Tests.Count());

        public IEnumerable<TestResultVM> Tests => tests?.Values;
        private List<TestResultGroup> _GroupedTests;

        public List<TestResultGroup> GroupedTests
        {
            get
            {
                if(_GroupedTests == null && tests != null)
                {
                    UpdateGroup(_grouping);
                }
                return _GroupedTests;
            }
        }

        public MSTestX.TestOptions Settings { get; internal set; }

        private static T GetProperty<T>(string id, TestObject test, T defaultValue)
        {
            var prop = test.Properties.Where(p => p.Id == id).FirstOrDefault();
            if (prop != null)
                return test.GetPropertyValue<T>(prop, defaultValue);
            return defaultValue;
        }

        private static string s_computerName;
        private static string ComputerName
        {
            get
            {
                if(s_computerName is null)
                {
#if __ANDROID__
                    s_computerName = $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model} v{Environment.OSVersion.Version.Major}";
#elif __IOS__
                    var pLen = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
                    sysctlbyname("hw.machine", IntPtr.Zero, pLen, IntPtr.Zero, 0);
                    var length = System.Runtime.InteropServices.Marshal.ReadInt32(pLen);
                    string deviceName = null;
                    if (length > 0)
                    {
                        var pStr = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
                        sysctlbyname("hw.machine", pStr, pLen, IntPtr.Zero, 0);
                        deviceName = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(pStr);
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(pStr);
                    }
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(pLen);
                    if (!string.IsNullOrEmpty(deviceName))
                        s_computerName = $"{deviceName} - {UIKit.UIDevice.CurrentDevice.SystemName} {UIKit.UIDevice.CurrentDevice.SystemVersion}";
                    else
                        s_computerName = $"{UIKit.UIDevice.CurrentDevice.SystemName} {UIKit.UIDevice.CurrentDevice.SystemVersion}";
#else
                   s_computerName = Environment.MachineName;
#endif
                }
                return s_computerName;
            }
        }
#if __IOS__
        [System.Runtime.InteropServices.DllImport("libc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int sysctlbyname([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string property, IntPtr output, IntPtr length, IntPtr newp, uint newlen);
#endif
        void ITestExecutionRecorder.RecordResult(TestResult testResult)
        {
            var execId = GetProperty<Guid>("ExecutionId", testResult, Guid.Empty);
            if (execId == Guid.Empty)
            {
                var prop = testResult.Properties.Where(p => p.Id == "ExecutionId").FirstOrDefault();
                if (prop != null)
                    testResult.SetPropertyValue(prop, Guid.NewGuid());
            }

            results?.Add(testResult);
            var innerResultsCount = GetProperty<int>("InnerResultsCount", testResult, 0);
            var parentExecId = GetProperty<Guid>("ParentExecId", testResult, Guid.Empty);
            var test = tests[testResult.TestCase.Id];
            if (parentExecId == Guid.Empty) // We don't report child result in the UI
            {
                testResult.ComputerName = ComputerName;
                test.Result = testResult;

                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(Percentage));
                OnPropertyChanged(nameof(TestStatus));
                OnPropertyChanged(nameof(NotRunTests));
                OnPropertyChanged(nameof(PassedTests));
                OnPropertyChanged(nameof(FailedTests));
                OnPropertyChanged(nameof(SkippedTests));
                if (innerResultsCount > 0) // Prep the child results getting reported immediately after this one
                    test.ChildResults = new List<TestResult>(innerResultsCount);
            }
            else
            {
                test.ChildResults.Add(testResult);
                test.OnPropertyChanged(nameof(TestResultVM.ChildResults));
            }
            Log($"Completed test '{testResult.TestCase.FullyQualifiedName}': {testResult.Outcome} {testResult.ErrorMessage}");
            if (testResult.Attachments.Count > 0)
            {
                connection?.SendAttachments(testResult.Attachments, Settings.TestRunDirectory);
                Settings.TestRecorder?.RecordAttachments(testResult.Attachments);
            }
            trxWriter?.OnTestResult(new TestResultEventArgs(testResult));
            Logger.LogResult(testResult);
            connection?.SendTestEndResult(testResult);
            Settings.TestRecorder?.RecordResult(testResult);
            
            if (test.ChildResults == null || test.ChildResults.Count == test.ChildResultCount)
            {
                // Single test or complete set of datarows: Save
                SaveProgress(new TestResultVM[] { test }, true);
            }
        }

        private void Log(string message)
        {
            if (logOutput != null)
            {
                logOutput.WriteLine(message);
                logOutput.Flush();
            }
        }

        void ITestExecutionRecorder.RecordStart(TestCase testCase)
        {
            var vmtest = tests[testCase.Id];
            vmtest.SetInProgress();
            CurrentTestRunning = vmtest;
            OnPropertyChanged(nameof(CurrentTestRunning));
            Log($"Starting test '{testCase.FullyQualifiedName}'");
            Logger.LogTestStart(testCase);
            connection?.SendTestStart(testCase);
            Settings.TestRecorder?.RecordStart(testCase);
        }

        public TestResultVM CurrentTestRunning { get; private set; }

        void ITestExecutionRecorder.RecordEnd(TestCase testCase, TestOutcome outcome)
        {
            connection?.SendTestEnd(testCase, outcome);
            Settings.TestRecorder?.RecordEnd(testCase, outcome);
        }

        void ITestExecutionRecorder.RecordAttachments(IList<AttachmentSet> attachmentSets)
        {
            connection?.SendAttachments(attachmentSets, Settings.TestRunDirectory);
            Settings.TestRecorder?.RecordAttachments(attachmentSets);
        }

        void IMessageLogger.SendMessage(TestMessageLevel testMessageLevel, string message)
        {
            Log($"MESSAGE: {testMessageLevel}: {message}");
            connection?.SendMessage(testMessageLevel, message);
            //Logger.LogMessage(Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel.MessageType.TestMessage)
            Settings.TestRecorder?.SendMessage(testMessageLevel, message);
        }
    }
}
