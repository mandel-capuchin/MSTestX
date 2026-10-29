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
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MSTestX.Console
{
    /// <summary>
    /// Helper for calling into https://github.com/imkira/mobiledevice
    /// </summary>
    internal sealed class MobileDevice : IDisposable
    {
        private Process? mobileDeviceProcess;
        private TaskCompletionSource? tunnelCompletion;
        private bool isDisposing;
        private MobileDevice()
        {
            if (!OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException();
            var path = Path.Combine(new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).Directory!.FullName, "mobiledevice");
            Process.Start(new ProcessStartInfo("chmod", "+x " + path));
            mobileDeviceProcess = new Process() { StartInfo = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false } };
            mobileDeviceProcess.EnableRaisingEvents = true;
            mobileDeviceProcess.OutputDataReceived += MobileDeviceProcess_OutputDataReceived;
            mobileDeviceProcess.ErrorDataReceived += MobileDeviceProcess_ErrorDataReceived;
            mobileDeviceProcess.Exited += MobileDeviceProcess_Exited;
        }
        string? lastMessage = null;

        internal bool IsIntentionalShutdown => isDisposing;

        internal static bool IsUnexpectedTunnelExit(bool isIntentionalShutdown)
        {
            return !isIntentionalShutdown;
        }

        internal static string FormatTunnelExitMessage(
            int exitCode,
            string? lastMessage,
            bool isIntentionalShutdown)
        {
            var reason = string.IsNullOrEmpty(lastMessage) ? "" : ". " + lastMessage;
            if (isIntentionalShutdown)
                return $"mobiledevice: tunnel stopped intentionally with code {exitCode}{reason}";

            return $"mobiledevice: tunnel exited unexpectedly with code {exitCode}{reason}";
        }

        private void MobileDeviceProcess_OutputDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lastMessage = e.Data;
            System.Console.WriteLine("mobiledevice: " + e.Data);
            if (e.Data.Contains("Tunneling from local port") && tunnelCompletion is not null)
            {
                tunnelCompletion.TrySetResult();
            }
            if (e.Data.Contains("Failed to set up port forwarding") && tunnelCompletion is not null)
            {
                tunnelCompletion.TrySetException(new Exception(e.Data));
            }
        }

        private void MobileDeviceProcess_ErrorDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lastMessage = e.Data;
            System.Console.WriteLine("mobiledevice stderr: " + e.Data);
        }

        private void MobileDeviceProcess_Exited(object? sender, EventArgs e)
        {
            var process = mobileDeviceProcess;
            if (process is null)
                return;

            var exitCode = process.ExitCode;
            var message = FormatTunnelExitMessage(exitCode, lastMessage, isDisposing);
            System.Console.WriteLine(message);
            if (IsUnexpectedTunnelExit(isDisposing))
            {
                tunnelCompletion?.TrySetException(new Exception(message));
            }
            Exited?.Invoke(this, exitCode);
            mobileDeviceProcess = null;
        }

        private async Task StartTunnelAsync(
            int fromPort,
            int toPort,
            string uuid,
            CancellationToken cancellationToken)
        {
            Process.Start("pkill", "mobiledevice"); // Ensure mobiledevice isn't already running
            await Task.Delay(100, cancellationToken);
            tunnelCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = tunnelCompletion;
            var process = mobileDeviceProcess ?? throw new ObjectDisposedException(nameof(MobileDevice));
            process.StartInfo.Arguments = $"tunnel -u {uuid} {fromPort} {toPort}";
            using var timeoutCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellationTokenSource.Token);
            process.Start();
            using var cancellationRegistration = linkedCancellationTokenSource.Token.Register(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    completion.TrySetCanceled(cancellationToken);
                else
                    completion.TrySetException(new TimeoutException());

                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Failed to stop mobiledevice after cancellation: {exception.Message}");
                }
            });
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await completion.Task;
        }

        public static async Task<MobileDevice> CreateTunnelAsync(
            int fromPort,
            int toPort,
            string uuid,
            CancellationToken cancellationToken = default)
        {
            var mobileDevice = new MobileDevice();
            try
            {
                await mobileDevice.StartTunnelAsync(fromPort, toPort, uuid, cancellationToken);
                return mobileDevice;
            }
            catch
            {
                mobileDevice.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            isDisposing = true;
            if (mobileDeviceProcess != null && !mobileDeviceProcess.HasExited)
            {
                System.Console.WriteLine("mobiledevice: stopping tunnel intentionally.");
                mobileDeviceProcess.Kill(true);
            }
            mobileDeviceProcess?.Dispose();
            mobileDeviceProcess = null;
        }

        ~MobileDevice()
        {
            Dispose();
        }

        public event EventHandler<int>? Exited;
    }
}
