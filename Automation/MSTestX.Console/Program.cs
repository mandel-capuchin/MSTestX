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

#nullable enable
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using MSTestX.Console.Adb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MSTestX.Console
{
    internal sealed class ParsedArguments
    {
        public ParsedArguments(
            IReadOnlyDictionary<string, string?> consoleOptions,
            IReadOnlyList<string> applicationArguments)
        {
            ConsoleOptions = consoleOptions;
            ApplicationArguments = applicationArguments;
        }

        public IReadOnlyDictionary<string, string?> ConsoleOptions { get; }

        public IReadOnlyList<string> ApplicationArguments { get; }
    }

    class Program 
    {
        private static Device? device;
        private static string? apk_id = null;
        private static string? activityName = null;
        private static AdbClient? client;
        private static string? outputFilename;
        private static string? appLogFilename;
        private static string? settingsXml = null;
        private static LogCatMonitor? monitor;
        private static CancellationTokenSource? processExitCancellationTokenSource;
        private static TaskCompletionSource<int> testRunCompleted = new TaskCompletionSource<int>();
        private static int adbServerPort = 5037;
        private static int adapterConnectionTimeoutSeconds = 60;
        private static bool androidForwardCreated;
        private const int TestAdapterPort = 38300;

        internal static LaunchMode GetLaunchMode(IReadOnlyDictionary<string, string?> arguments)
        {
            if (arguments.ContainsKey("remoteIp") || arguments.ContainsKey("waitForRemote"))
                return LaunchMode.RemoteAdapter;

            if (arguments.ContainsKey("apppath"))
                return LaunchMode.AppleApp;

            return LaunchMode.AndroidAdb;
        }

        internal static string? GetAppLogFilename(string? trxFilename)
        {
            return string.IsNullOrWhiteSpace(trxFilename)
                ? null
                : Path.ChangeExtension(trxFilename, ".log");
        }

        static async Task Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "/?" || args[0] == "-?" || args[0] == "?")
            {
                PrintUsage();
                return;
            }
            ParsedArguments arguments;
            try
            {
                arguments = ParseArguments(args);
            }
            catch (ArgumentException ex)
            {
                System.Console.Error.WriteLine("ERROR: " + ex.Message);
                Environment.ExitCode = 1;
                return;
            }

            using var processCancellationTokenSource = new CancellationTokenSource();
            processExitCancellationTokenSource = processCancellationTokenSource;
            System.ConsoleCancelEventHandler cancelKeyPressHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                processCancellationTokenSource.Cancel();
                testRunCompleted.TrySetResult(1);
            };
            System.Console.CancelKeyPress += cancelKeyPressHandler;
            try
            {
                await RunTest(
                    arguments.ConsoleOptions,
                    arguments.ApplicationArguments,
                    processCancellationTokenSource.Token);
                Environment.ExitCode = await testRunCompleted.Task;
            }
            catch (OperationCanceledException) when (processCancellationTokenSource.IsCancellationRequested)
            {
                System.Console.Error.WriteLine("Test run canceled.");
                Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine("ERROR: " + ex.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                System.Console.CancelKeyPress -= cancelKeyPressHandler;
                await KillProcess();
                processExitCancellationTokenSource = null;
            }
        }

        private static void PrintUsage()
        {
            System.Console.WriteLine(@"Test Runner commandline arguments:
    -remoteIp <ip:port>                 The IP address of a device already running the unit test app
    -waitForRemote                      If IP of device isn't known, use waitForRemote to wait for app to ping back on port 38302.
    -logFileName <path>                 TRX Output Filename
    -settings <path>                    MSTest RunSettings xml file
    -filter, --filter <expression>      MSTest test case filter expression

Android specific (ignored if using remoteIp):
    -deviceid <Device Serial Number>    If more than one device is connected, specifies which device to use
    -apkpath <file path>                Path to an APK to install.
    -apkid <id>                         Package ID of the test app (if not provided, auto-discovered from manifest)
    -activity <activity id>             Activity to launch (if not provided, auto-discovered from manifest)
    -pin <pin code>                     Pin to use to unlock your phone (or empty to just unlock phone with no pin)
    -adbServerPort <port>               ADB smart-socket server port (default 5037)
    -adapterConnectionTimeoutSeconds <seconds> Maximum adapter connection and handshake time (default 60)

iOS physical-device specific (macOS only):
    -apppath <file path>                Path to app to install and launch
    -device <uuid|ecid|serial_number|udid|name|dns_name> The identifier, ECID, serial number, UDID, user-provided name, or DNS name of the device.

Application arguments for physical iOS launches:
    Syntax: MSTestX.Console <console-options> -- <app-arg-0> <app-arg-1> ...
    MSTestX.Console -apppath MyTests.app -- --suite Smoke --display-name ""Login flow""
    MSTestX.Console -apppath MyTests.app -- --quoted '""literal quotes""' --empty """"

    The first standalone -- ends Console option parsing. Every following shell-tokenized
    argument is forwarded verbatim and in order. Quote or escape values according to your
    shell to preserve spaces, literal quotes, or empty values.

    With 'dotnet tool run', add its outer separator before Console options:
    dotnet tool run MSTestX.Console -- <console-options> -- <app-args>
    MSTestX.Console adds the separate CoreDevice child-argument separator internally.

    --TestAdapterPort and --AutoExit are reserved. MSTestX.Console supplies them first to
    manage the test connection and application lifetime and rejects caller overrides.

    Application arguments are unsupported for Android, -remoteIp, and -waitForRemote.
    Mac Catalyst remains caller-launched and must use -remoteIp without passthrough.
");
        }

        static async Task RunTest(
            IReadOnlyDictionary<string, string?> arguments,
            IReadOnlyList<string> applicationArguments,
            CancellationToken cancellationToken)
        {
            adbServerPort = GetValidatedIntegerOption(arguments, "adbServerPort", 5037);
            adapterConnectionTimeoutSeconds = GetValidatedIntegerOption(
                arguments,
                "adapterConnectionTimeoutSeconds",
                60);

            if (arguments.ContainsKey("settings") && File.Exists(arguments["settings"]))
            {
                settingsXml = File.ReadAllText(arguments["settings"]!);
            }

            if (arguments.TryGetValue("filter", out var filterExpression) && !string.IsNullOrWhiteSpace(filterExpression))
            {
                settingsXml = MergeTestCaseFilter(settingsXml, filterExpression);
            }


            System.Net.IPEndPoint? testAdapterEndpoint = null;
            if (arguments.ContainsKey("waitForRemote"))
            {
                var pingListener = new TcpListener(System.Net.IPAddress.Any, 38302);
                System.Console.WriteLine("Waiting for remote ping...");
                TimeSpan timeout;
                if (arguments.ContainsKey("waitForRemoteTimeout"))
                {
                    if(int.TryParse(arguments["waitForRemoteTimeout"], out int timeoutSeconds))
                        timeout = TimeSpan.FromSeconds(timeoutSeconds);
                    else
                    {
                        System.Console.WriteLine($"Invalid timeout value '{arguments["waitForRemoteTimeout"]}'.");
                        testRunCompleted.TrySetResult(1);
                        return;
                    }
                }
                else
                    timeout = TimeSpan.FromMinutes(5);
                try
                {
                    testAdapterEndpoint = await WaitForRemoteEndpointAsync(
                        pingListener,
                        timeout,
                        cancellationToken);
                    System.Console.WriteLine($"Remote device connected from {testAdapterEndpoint}. Starting test run...");
                }
                catch (TimeoutException)
                {
                    System.Console.WriteLine("Timeout waiting for remote device to connect");
                    testRunCompleted.TrySetResult(1);
                    return;
                }
            }

            if (arguments.ContainsKey("remoteIp"))
            {
                var val = arguments["remoteIp"];
                if (val is not null && val.Contains(":") && System.Net.IPAddress.TryParse(val.Split(':')[0], out System.Net.IPAddress? ip) && int.TryParse(val.Split(':')[1], out int port))
                {
                    testAdapterEndpoint = new System.Net.IPEndPoint(ip, port);
                }
                else
                {
                    System.Console.WriteLine("Invalid remote ip and/or port");
                    testRunCompleted.TrySetResult(1);
                    return;
                }
            }
            if (arguments.ContainsKey("logFileName"))
                outputFilename = arguments["logFileName"];

            switch (GetLaunchMode(arguments))
            {
                case LaunchMode.RemoteAdapter:
                {
                    if (testAdapterEndpoint == null)
                    {
                        testRunCompleted.TrySetResult(1);
                        return;
                    }

                    if (string.IsNullOrEmpty(outputFilename))
                    {
                        string defaultFilename = DateTime.Now.ToString("yyyy-MM-dd_HH_mm_ss");
                        outputFilename = Path.Combine(System.Environment.CurrentDirectory, defaultFilename + ".trx");
                    }
                    await OnApplicationLaunched(testAdapterEndpoint, cancellationToken: cancellationToken);
                    return;
                }

                case LaunchMode.AppleApp:
                {
                    if (!OperatingSystem.IsMacOS())
                    {
                        System.Console.WriteLine("iOS apps much be launch from a Mac");
                        testRunCompleted.TrySetResult(1);
                        return;
                    }
                    var devices = await devicectl.GetConnectedAppleDevicesAsync(cancellationToken);
#if DEBUG
                    System.Console.WriteLine("Connected devices: ");
                    foreach (var d in devices.Result.Devices)
                    {
                        System.Console.WriteLine($"{d.DeviceProperties.Name} ({d.Identifier}) : {d.HardwareProperties.DeviceType} {d.HardwareProperties.CpuType.Name} {d.DeviceProperties.OsVersionNumber}");
                    }
#endif
                    string? device = arguments.ContainsKey("device") ? arguments["device"] : null;
                    if (device is null && devices.Result.Devices.Length == 1)
                    {
                        device = devices.Result.Devices[0].Identifier;
                    }
                    if (string.IsNullOrEmpty(device))
                    {
                        if (devices.Result.Devices.Length == 0)
                            System.Console.WriteLine("No devices found");
                        else
                        {
                            System.Console.WriteLine("Device parameter '-device <uuid>' missing and multiple devices connected:");
                            foreach (var d in devices.Result.Devices)
                            {
                                System.Console.WriteLine($"    - {d.Identifier} ({d.DeviceProperties.Name} - {d.HardwareProperties.MarketingName}. OS version: {d.DeviceProperties.OsVersionNumber})");
                            }
                        }

                        testRunCompleted.TrySetResult(1);
                        return;
                    }
                    var details = await devicectl.GetDeviceDetails(device, cancellationToken);
                    var apppath = arguments["apppath"];
                    if (!Directory.Exists(apppath) && !File.Exists(apppath))
                    {
                        System.Console.WriteLine("File not found: " + apppath);
                        testRunCompleted.TrySetResult(1);
                        return;
                    }
                    System.Console.WriteLine("Installing app...");
                    var bundleId = await devicectl.InstallApp(device, apppath, cancellationToken);
                    System.Console.WriteLine($"App {bundleId} installed");

                    if (string.IsNullOrEmpty(outputFilename))
                    {
                        string defaultFilename = DateTime.Now.ToString("yyyy-MM-dd_HH_mm_ss");
                        outputFilename = Path.Combine(System.Environment.CurrentDirectory, defaultFilename + ".trx");
                    }
                    appLogFilename = GetAppLogFilename(outputFilename);
                    System.Console.WriteLine($"iOS app log file: {appLogFilename}");

                    // Set up port forwarding using "mobiledevice"
                    MobileDevice tunnel;
                    try
                    {
                        tunnel = await MobileDevice.CreateTunnelAsync(
                            38300,
                            38300,
                            details.Result.HardwareProperties.Udid,
                            cancellationToken);
                    }
                    catch (System.Exception ex)
                    {
                        System.Console.WriteLine("Failed to open tunnel: " + ex.Message);
                        testRunCompleted.TrySetResult(1);
                        return;
                    }
                    tunnel.Exited += (s, e) =>
                    {
                        if (s is MobileDevice mobileDevice && mobileDevice.IsIntentionalShutdown)
                        {
                            System.Console.WriteLine("Tunnel process exited after intentional shutdown.");
                            return;
                        }

                        testRunCompleted.TrySetResult(1);
                        System.Console.WriteLine("Tunnel process exited unexpectedly.");
                    };

                    CancellationTokenSource closeAppToken = new CancellationTokenSource();
                    closeAppToken.Token.Register(t => tunnel.Dispose(), null);
                    var appTask = devicectl.LaunchApp(device, bundleId, applicationArguments, appLogFilename, closeAppToken.Token);
                    _ = appTask.ContinueWith(t =>
                    {
                        if (t.IsCanceled)
                            System.Console.WriteLine("iOS app console capture stopped.");
                        else if (t.Exception != null)
                            System.Console.WriteLine("iOS app console capture failed: " + t.Exception.GetBaseException().Message);
                        else
                            System.Console.WriteLine("iOS app console capture completed.");
                    });
                    await OnApplicationLaunched(
                        System.Net.IPEndPoint.Parse("127.0.0.1:38300"),
                        appLogFilename,
                        cancellationToken);
                    GC.KeepAlive(tunnel);
                    closeAppToken.Cancel();
                    testRunCompleted.TrySetResult(0);
                    return;
                }

                default:
                {
                    // Launch Android android app
                    client = new AdbClient(adbServerPort);
                    var devices = await client.GetDevicesAsync().WaitAsync(cancellationToken);
                if (!devices.Any())
                {
                    System.Console.WriteLine("No devices connected to ADB");
                    testRunCompleted.TrySetResult(1);
                    return;
                }
                if (arguments.ContainsKey("deviceid"))
                {
                    device = devices.Where(d => d.Serial == arguments["deviceid"]).FirstOrDefault();
                    if (device == null)
                    {
                        System.Console.WriteLine($"ERROR Device '{arguments["deviceid"]}' not found");
                        testRunCompleted.TrySetResult(1);
                        return;
                    }
                }
                if (device == null)
                {
                    if (devices.Count() > 1)
                    {
                        System.Console.WriteLine($"ERROR. Multiple devices connected. Please specify -deviceId <deviceid>");
                        foreach (var d in devices)
                        {
                            System.Console.WriteLine($"\t{d.Serial}\t{d.Name}\t{d.Model}\t{d.Product}\t{d.State}");
                        }
                        testRunCompleted.TrySetResult(1);
                        return;
                    }
                    device = devices.First();
                }
                if (arguments.ContainsKey("apkid"))
                    apk_id = arguments["apkid"];
                if (arguments.ContainsKey("activity"))
                    activityName = arguments["activity"];

                await ShutdownApp(cancellationToken);

                if (arguments.ContainsKey("apkpath"))
                {
                    var path = arguments["apkpath"];
                    if (!File.Exists(path))
                    {
                        System.Console.WriteLine("ERROR. APK Not Found: " + path);
                        testRunCompleted.TrySetResult(1);
                    }
                    if(apk_id == null || activityName == null)
                    {
                        string id;
                        string name;
                        ApkHelper.GetAPKInfo(path, out id, out name);
                        if (apk_id == null)
                            apk_id = id;
                        if (activityName == null)
                            activityName = name;
                    }
                    System.Console.WriteLine("Installing app...");
                    await device.InstallApk(path).WaitAsync(cancellationToken);
                }

                // Get unlock state
                var state = await device.GetPowerState().WaitAsync(cancellationToken);
                if (!state.IsDisplayOn)
                {
                    await device.TurnOnDisplayAsync().WaitAsync(cancellationToken);

                    while (!state.IsDisplayOn)
                    {
                        await Task.Delay(500, cancellationToken);
                        state = await device.GetPowerState().WaitAsync(cancellationToken);
                    }
                }

                if (state.DisplayState == Device.DisplayState.OnAndLocked)
                {
                    if (arguments.ContainsKey("pin"))
                    {
                        string? pin = null;
                        if (int.TryParse(arguments["pin"], out int numericPin)) //Ensures it's numeric
                        {
                            pin = arguments["pin"];
                        }
                        System.Console.WriteLine("Unlocking phone...");
                        await device.UnlockAsync(pin).WaitAsync(cancellationToken);
                    }
                    else
                    {
                        System.Console.WriteLine("Device appears to be locked. Please unlock your device");
                    //    System.Console.ReadKey();
                    }
                }
                if (File.Exists("RunLog.txt"))
                    File.Delete("RunLog.txt");

                if (string.IsNullOrEmpty(outputFilename))
                {
                    string defaultFilename = device.Serial + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH_mm_ss");
                    if (apk_id != null)
                        defaultFilename += "_" + apk_id;
                    outputFilename = Path.Combine(System.Environment.CurrentDirectory, defaultFilename + ".trx");
                }

                monitor = new LogCatMonitor(device.Serial, adbServerPort);
                // Connecting to logcat...                                
                await client.SendDeviceCommandAsync("shell:logcat -b all -c", device.Serial).WaitAsync(cancellationToken); //Configure logcat
                bool result;
                if (device.ApiLevel > 22)
                    result = await monitor.OpenAsync($"{apk_id} -T 1").WaitAsync(cancellationToken);
                else
                    result = await monitor.OpenAsync($"").WaitAsync(cancellationToken);
                if (result)
                {
                    System.Console.WriteLine("Connected logcat to " + device.Serial);
                }
                else
                {
                    System.Console.WriteLine("Failed to connect to LogCat");
                    testRunCompleted.TrySetResult(1);
                    return;
                }
                monitor.LogReceived += Monitor_LogReceived;

                await device.SetPortForward(TestAdapterPort, TestAdapterPort).WaitAsync(cancellationToken);  //Set up port forwarding for socket communication
                androidForwardCreated = true;
                
                System.Console.WriteLine($"Launching app {apk_id}/{activityName} on device " + device.Serial + "...");

                await device.LaunchApp(apk_id, activityName, new Dictionary<string, object>() { { "TestAdapterPort", TestAdapterPort } }).WaitAsync(cancellationToken);
               
                // Keep looking for process starting up
                bool launched = false;
                while (!launched)
                {
                    await Task.Delay(1000, cancellationToken);
                    var pid = await device.GetProcessId(apk_id).WaitAsync(cancellationToken);
                    if (appLaunchDetected)
                        break;
                    if (pid > 0)
                    {
                        launched = true;
                        System.Console.WriteLine($"Test Host Launched. Process ID '{pid}'");
                        _ = OnApplicationLaunched(
                            cancellationToken: cancellationToken);
                        break;
                    }
                    else
                    {
                        //Keep retrying. Doesn't always launch the first time
                        await device.LaunchApp(apk_id, activityName, new Dictionary<string, object>() { { "TestAdapterPort", TestAdapterPort } }).WaitAsync(cancellationToken);
                    }
                }

                    return;
                }
            }
        }

        internal static async Task<System.Net.IPEndPoint> WaitForRemoteEndpointAsync(
            TcpListener pingListener,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            pingListener.Start();
            using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout);
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellationTokenSource.Token);
            try
            {
                using var pingClient = await pingListener.AcceptTcpClientAsync(
                    linkedCancellationTokenSource.Token);
                var remoteEndpoint = (System.Net.IPEndPoint)pingClient.Client.RemoteEndPoint!;
                return new System.Net.IPEndPoint(remoteEndpoint.Address, TestAdapterPort);
            }
            catch (OperationCanceledException) when (
                timeoutCancellationTokenSource.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for a remote test host.");
            }
            finally
            {
                pingListener.Stop();
            }
        }

        private static async Task ShutdownApp(CancellationToken cancellationToken)
        {
            if (device is null) return;
            var id = await device.GetProcessId(apk_id).WaitAsync(cancellationToken);
            if (id > 0)
            {
                System.Console.WriteLine($"Application '{apk_id}' already running. Shutting down...");
                await device.StopApp(apk_id).WaitAsync(cancellationToken); //Ensure app isn't already running
                while (await device.GetProcessId(apk_id).WaitAsync(cancellationToken) > 0)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        private static bool appLaunchDetected;

        private static async Task OnApplicationLaunched(
            System.Net.IPEndPoint? endpoint = null,
            string? appLogFilename = null,
            CancellationToken cancellationToken = default)
        {
            if (appLaunchDetected)
                return;
            appLaunchDetected = true;
            // Exit-code contract: any completed test run returns 0, including runs with failed
            // assertions. Only runner/test-host failures caught below return 1.
            int result = 0;
            var runner = new TestRunner(
                endpoint,
                TimeSpan.FromSeconds(adapterConnectionTimeoutSeconds));
            try
            {
                await Task.Delay(5000, cancellationToken); //Give app some time to start up
                await runner.RunTests(outputFilename, settingsXml, cancellationToken, appLogFilename);
            }
            catch(System.Exception ex)
            {
                System.Console.WriteLine(ex.Message);
                result = 1;
            }
            testRunCompleted.TrySetResult(result);
        }

        private static async Task KillProcess()
        {
            var cleanupSteps = new List<(string Name, Func<Task> Action)>();
            if (monitor != null)
            {
                cleanupSteps.Add(("close logcat monitor", () =>
                {
                    monitor.LogReceived -= Monitor_LogReceived;
                    monitor.Close();
                    monitor = null;
                    return Task.CompletedTask;
                }));
            }
            if (device != null && !string.IsNullOrWhiteSpace(apk_id))
            {
                cleanupSteps.Add(("force-stop Android test app", () => device.StopApp(apk_id)));
            }
            if (device != null && androidForwardCreated)
            {
                cleanupSteps.Add(($"remove tcp:{TestAdapterPort} forward", async () =>
                {
                    await device.RemovePortForward(TestAdapterPort);
                    androidForwardCreated = false;
                }));
            }

            await RunCleanupStepsAsync(cleanupSteps);
        }

        internal static async Task RunCleanupStepsAsync(
            IEnumerable<(string Name, Func<Task> Action)> cleanupSteps)
        {
            foreach (var cleanupStep in cleanupSteps)
            {
                try
                {
                    await cleanupStep.Action().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine($"Failed to {cleanupStep.Name}: {ex.Message}");
                }
            }
        }

        private static int processID = -1;
        
        private static void Monitor_LogReceived(object? sender, LogCatMonitor.LogEntry e)
        {
            var msg = e.DataString;
            if (msg == null) return;
            //Log anything from logcat for the process or it the APK ID is in the message
            if (processID > 0 && e.ProcessId == processID || apk_id != null && msg.Contains(apk_id))
            {
                File.AppendAllText("RunLog.txt", $"{e.TimeStamp}\t{e.Type}\t{e.Tag}\t{e.DataString}{Environment.NewLine}");
            }

            if (processID < 0)
            {
                if(e.Tag == "ActivityManager" && 
                        msg.StartsWith("Start proc ") &&
                        msg.Contains($":{apk_id}") &&
                        msg.Contains($" for activity {apk_id}/{activityName}")
                        && !msg.Contains(" for backup "))
                {
                    string processIDStr = msg.Substring(11, msg.IndexOf(":") - 11);
                    if (!int.TryParse(processIDStr, out processID))
                        return;
                    else
                    {
                        File.AppendAllText("RunLog.txt", $"{Environment.NewLine}====================================={Environment.NewLine}Application launched with Process ID {processID}{Environment.NewLine}");
                        System.Console.WriteLine($"Application launched with Process ID {processID}");
                    }
                }
            }

            //Log anything from logcat for the process or it the APK ID is in the message
            if (processID > 0 && e.ProcessId == processID || apk_id != null && msg.Contains(apk_id))
            {
                File.AppendAllText("RunLog.txt", $"{e.TimeStamp}\r{e.Type}\r{e.Tag}\t{e.DataString}{Environment.NewLine}");
            }
            if(e.Tag == "ActivityManager")
            {

            }
            if (e.Tag == "ActivityManager" && msg.StartsWith($"Displayed {apk_id}/{activityName}"))
            {
                _ = OnApplicationLaunched(
                    cancellationToken: processExitCancellationTokenSource?.Token ?? CancellationToken.None); //Detect app launched and start VSTest connection
                return;
            }
            if (processID > 0 && e.Tag == "ActivityManager" && msg == $"Process {apk_id} (pid {processID}) has died.")
            {
                // Application died
                System.Console.WriteLine($"{Environment.NewLine}Android application process has died. Exiting...");
                processExitCancellationTokenSource?.Cancel();
                //OnTestRunAborted("Android application process has died");
                //testRunCompleted.TrySetResult(1);
                return;
            }
            if (processID > 0 && e.ProcessId == processID)
            {
                if (e.Tag == "AndroidRuntime" && e.Type == LogCatMonitor.LogEntry.LogType.Error)
                {
                    System.Console.ForegroundColor = ConsoleColor.Red;
                    System.Console.WriteLine(Environment.NewLine + "ERROR: " + e.DataString);
                    System.Console.ResetColor();
                }
            }
            else if (e.Tag == "libprocessgroup")
            {
                if (msg.StartsWith("Successfully killed process") && msg.Contains($" pid {processID}")) 
                {
                    //App likely exited. Check if it's still alive
                    var _ = device!.GetProcessId(apk_id).ContinueWith(t => 
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            if (t.Result == 0)
                            {
                                System.Console.WriteLine($"{Environment.NewLine}Android application process killed. Exiting...");
                                processExitCancellationTokenSource?.Cancel();
                            }
                        }
                    });

                }
            }
        }

        internal static ParsedArguments ParseArguments(string[] args)
        {
            var result = new Dictionary<string, string?>();
            var delimiterIndex = Array.IndexOf(args, "--");
            var consoleArgumentCount = delimiterIndex >= 0 ? delimiterIndex : args.Length;

            for (int i = 0; i < consoleArgumentCount; i++)
            {
                string? key = null;
                string? value = null;
                if (args[i].StartsWith("-"))
                {
                    key = args[i].TrimStart('-');
                    if (i < consoleArgumentCount - 1 && !args[i + 1].StartsWith("-"))
                    {
                        i++;
                        value = args[i];
                    }
                }
                if (key != null)
                    result[key] = value;
            }

            IReadOnlyList<string> applicationArguments = delimiterIndex >= 0
                ? args.Skip(delimiterIndex + 1).ToArray()
                : Array.Empty<string>();

            if (applicationArguments.Count > 0)
            {
                switch (GetLaunchMode(result))
                {
                    case LaunchMode.AppleApp:
                        ValidateApplicationArguments(applicationArguments);
                        break;
                    case LaunchMode.RemoteAdapter:
                        throw new ArgumentException(
                            "Application arguments after '--' are not supported with -remoteIp or -waitForRemote. " +
                            "Remote applications, including Mac Catalyst, remain caller-launched.");
                    case LaunchMode.AndroidAdb:
                        throw new ArgumentException(
                            "Application arguments after '--' are only supported for physical Apple launches using -apppath; " +
                            "Android launch mode does not support passthrough.");
                }
            }

            ValidateIntegerOption(result, "adbServerPort", maximum: 65535);
            ValidateIntegerOption(result, "adapterConnectionTimeoutSeconds");

            return new ParsedArguments(result, applicationArguments);
        }

        private static void ValidateIntegerOption(
            IReadOnlyDictionary<string, string?> arguments,
            string optionName,
            int maximum = int.MaxValue)
        {
            if (!arguments.TryGetValue(optionName, out var value))
                return;

            if (!int.TryParse(value, out var parsedValue) || parsedValue <= 0 || parsedValue > maximum)
            {
                throw new ArgumentException(
                    $"-{optionName} must be an integer between 1 and {maximum}.");
            }
        }

        private static int GetValidatedIntegerOption(
            IReadOnlyDictionary<string, string?> arguments,
            string optionName,
            int defaultValue)
        {
            return arguments.TryGetValue(optionName, out var value)
                ? int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture)
                : defaultValue;
        }

        private static void ValidateApplicationArguments(IReadOnlyList<string> applicationArguments)
        {
            foreach (var argument in applicationArguments)
            {
                var normalizedArgument = argument.TrimStart('-');
                if (string.Equals(normalizedArgument, "TestAdapterPort", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedArgument, "AutoExit", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Application argument '{argument}' is reserved and cannot be overridden. " +
                        "MSTestX.Console manages --TestAdapterPort and --AutoExit.");
                }
            }
        }

        private static string MergeTestCaseFilter(string? existingSettingsXml, string filterExpression)
        {
            var xmlDoc = new XmlDocument();
            const string minimalRunSettings = @"<?xml version=""1.0"" encoding=""utf-8""?><RunSettings />";

            if (string.IsNullOrWhiteSpace(existingSettingsXml))
            {
                xmlDoc.LoadXml(minimalRunSettings);
            }
            else
            {
                try
                {
                    xmlDoc.LoadXml(existingSettingsXml);
                }
                catch (XmlException ex)
                {
                    System.Console.Error.WriteLine("Warning: The specified runsettings content is not valid XML and will be ignored. Details: " + ex.Message);
                    xmlDoc.LoadXml(minimalRunSettings);
                }
            }

            if (!string.Equals(xmlDoc.DocumentElement?.Name, "RunSettings", StringComparison.Ordinal))
            {
                System.Console.Error.WriteLine("Warning: The specified runsettings content does not have a <RunSettings> root element and will be ignored.");
                xmlDoc.LoadXml(minimalRunSettings);
            }
            var runSettings = xmlDoc.SelectSingleNode("RunSettings") ?? xmlDoc.AppendChild(xmlDoc.CreateElement("RunSettings"));
            var runConfiguration = runSettings.SelectSingleNode("RunConfiguration") ?? runSettings.AppendChild(xmlDoc.CreateElement("RunConfiguration"));
            var testCaseFilter = runConfiguration.SelectSingleNode("TestCaseFilter") as XmlElement;
            if (testCaseFilter == null)
            {
                testCaseFilter = xmlDoc.CreateElement("TestCaseFilter");
                runConfiguration.AppendChild(testCaseFilter);
            }

            testCaseFilter.InnerText = filterExpression;

            using var sw = new StringWriter();
            using var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = false });
            xmlDoc.Save(xw);
            xw.Flush();
            return sw.ToString();
        }
    }
}
