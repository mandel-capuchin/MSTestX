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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MSTestX.Console
{
    internal static class devicectl
    {
        public static async Task<bool> IsDeviceCtlInstalled()
        {
            try
            {
                _ = await DeviceCtl(new[] { "--version" }, CancellationToken.None);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Task<DeviceCtl.DeviceDetails> GetDeviceDetails(
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            return DeviceCtl<DeviceCtl.DeviceDetails>(
                new[] { "device", "info", "details", "--device", deviceId },
                cancellationToken);
        }

        public static Task<DeviceCtl.Devices> GetConnectedAppleDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            return DeviceCtl<DeviceCtl.Devices>(
                new[] { "list", "devices", "--timeout", "5" },
                cancellationToken);
        }

        public static async Task<string> InstallApp(
            string deviceId,
            string appPath,
            CancellationToken cancellationToken = default)
        {
            var output = await DeviceCtl(
                new[] { "device", "install", "app", "--device", deviceId, appPath },
                cancellationToken);
            var idLine = output.Split(Environment.NewLine).Where(s => s.Contains("bundleID: ")).FirstOrDefault();
            if(string.IsNullOrEmpty(idLine))
            {
                throw new Exception("Failed to obtain bundle ID from install");
            }
            var idx = idLine.LastIndexOf("bundleID: ") + 10;
            var bundleId = idLine.Substring(idx).Trim();
            return bundleId;
        }

        public static Task LaunchApp(
            string deviceId,
            string appId,
            IReadOnlyList<string>? applicationArguments = null,
            string? stdOutputFile = null,
            CancellationToken token = default)
        {
            return DeviceCtl(
                CreateLaunchProcessStartInfo(deviceId, appId, applicationArguments ?? Array.Empty<string>()),
                token,
                stdOutputFile);
        }

        internal static ProcessStartInfo CreateLaunchProcessStartInfo(
            string deviceId,
            string appId,
            IReadOnlyList<string> applicationArguments)
        {
            var arguments = new List<string>
            {
                "device",
                "process",
                "launch",
                "--device",
                deviceId,
                "--terminate-existing",
                "--console",
                appId,
                "--",
                "--TestAdapterPort",
                "38300",
                "--AutoExit",
                "True"
            };
            arguments.AddRange(applicationArguments);
            return CreateProcessStartInfo(arguments);
        }

        internal static ProcessStartInfo CreateProcessStartInfo(IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo("xcrun")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("devicectl");
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            return startInfo;
        }

        private static async Task<T> DeviceCtl<T>(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var tmpPath = System.IO.Path.GetTempFileName();
            var jsonArguments = new List<string>(arguments)
            {
                "--json-output",
                tmpPath
            };
            try
            {
                await DeviceCtl(jsonArguments, cancellationToken);
                var json = System.IO.File.ReadAllText(tmpPath);
                return System.Text.Json.JsonSerializer.Deserialize<T>(json)!;
            }
            finally
            {
                File.Delete(tmpPath);
            }
        }

        private static Task<string> DeviceCtl(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            string? stdOutputFile = null)
        {
            return DeviceCtl(CreateProcessStartInfo(arguments), cancellationToken, stdOutputFile);
        }

        private static async Task<string> DeviceCtl(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken,
            string? stdOutputFile = null)
        {
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Process xcrun = new Process
            {
                StartInfo = startInfo
            };
            xcrun.EnableRaisingEvents = true;
            StringBuilder sb = new StringBuilder();
            object outputLock = new object();
            PrepareOutputFile(stdOutputFile);
            
            xcrun.Exited += (s, e) =>
            {
                var output = SnapshotProcessOutput(sb, outputLock);
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else if(xcrun.ExitCode > 0)
                    tcs.TrySetException(new Exception(output));
                else
                    tcs.TrySetResult(output);
            };
            xcrun.ErrorDataReceived += (s, e) =>
            {
                if(e.Data is null)
                    return;
                AppendProcessOutput(sb, outputLock, stdOutputFile, e.Data);
            };
            xcrun.OutputDataReceived += (s,e) =>
            {
                Debug.WriteLine(e.Data);
                AppendProcessOutput(sb, outputLock, stdOutputFile, e.Data);
            };
            xcrun.Start();
            CancellationTokenRegistration cancellationRegistration = default;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!xcrun.HasExited)
                            xcrun.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Failed to stop devicectl after cancellation: {exception.Message}");
                        tcs.TrySetCanceled(cancellationToken);
                    }
                });
            }
            xcrun.BeginErrorReadLine();
            xcrun.BeginOutputReadLine();

            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }

        private static void PrepareOutputFile(string? outputFile)
        {
            if (string.IsNullOrWhiteSpace(outputFile))
                return;

            var directory = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(outputFile, "");
        }

        private static void AppendProcessOutput(
            StringBuilder output,
            object outputLock,
            string? outputFile,
            string? data)
        {
            if (data is null)
                return;

            lock (outputLock)
            {
                output.AppendLine(data);
                if (!string.IsNullOrWhiteSpace(outputFile))
                    File.AppendAllText(outputFile, data + Environment.NewLine);
            }
        }

        private static string SnapshotProcessOutput(StringBuilder output, object outputLock)
        {
            lock (outputLock)
            {
                return output.ToString();
            }
        }

    }
}
