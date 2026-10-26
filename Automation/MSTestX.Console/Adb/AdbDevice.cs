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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MSTestX.Console.Adb
{
    public class Device
    {
        private AdbClient _client;
        internal Device(AdbClient client) { _client = client; }
        const string AdbDeviceRegex = @"^(?<serial>[a-zA-Z0-9_-]+(?:\s?[\.a-zA-Z0-9_-]+)?(?:\:\d{1,})?)\s+(?<state>device|connecting|offline|unknown|bootloader|recovery|download|unauthorized|host|no permissions)(\s+usb:(?<usb>[^:]+))?(?:\s+product:(?<product>[^:]+))?(\s+model\:(?<model>[\S]+))?(\s+device\:(?<device>[\S]+))?(\s+features:(?<features>[^:]+))?(\s+transport_id:(?<transport_id>[^:]+))?$";
        static Regex Regex = new Regex(AdbDeviceRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string Serial { get; private set; }
        public string State { get; private set; }
        public string Model { get; private set; }
        public string Product { get; private set; }
        public string Name { get; private set; }
        public string Features { get; private set; }
        public string Usb { get; private set; }
        public string TransportId { get; private set; }
        public int ApiLevel { get; private set; }
        public string[] AdbFeatures { get; private set; }

        internal static Device FromDataString(string data, AdbClient client)
        {
            Match m = Regex.Match(data);
            if (m.Success)
            {
                return new Device(client)
                {
                    Serial = m.Groups["serial"].Value,
                    State = m.Groups["state"].Value,
                    Model = m.Groups["model"].Value,
                    Product = m.Groups["product"].Value,
                    Name = m.Groups["device"].Value,
                    Features = m.Groups["features"].Value,
                    Usb = m.Groups["usb"].Value,
                    TransportId = m.Groups["transport_id"].Value
                };
            }
            return null;
        }

        internal async Task Initialize()
        {
            var APILevelData = await _client.SendDeviceCommandGetDataAsync("shell:getprop ro.build.version.sdk", Serial).ConfigureAwait(false);
            int APILevel = 0;
            if (int.TryParse(Encoding.ASCII.GetString(APILevelData), out APILevel))
            {
                ApiLevel = APILevel;
            }
            var featureList = await _client.SendCommandGetStringAsync($"host-serial:{Serial}:features");
            AdbFeatures = featureList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }


        public async Task<string[]> GetPackagesAsync()
        {
            var packageList = await _client.SendDeviceCommandGetStringAsync("shell:pm list packages -f -3", Serial);
            var result = packageList.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim());
            return result.ToArray();
        }

        public async Task<int> GetProcessId(string apk_id)
        {
            if (ApiLevel >= 24) //Should probably use SupportsShellV2 check instead
            {
                var pid = await _client.SendDeviceCommandGetDataAsync($"shell:pidof {apk_id}", Serial);

                if (pid.Length > 0 && int.TryParse(Encoding.UTF8.GetString(pid), out int id))
                    return id;
            }
            else
            {
                var ps = Encoding.UTF8.GetString(await _client.SendDeviceCommandGetDataAsync("shell:ps", Serial));
                var processes = ps.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var process = processes.Where(p => p.Trim().EndsWith(apk_id)).FirstOrDefault();
                if (process != null)
                {
                    var data = process.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
                    if (data.Length > 1 && int.TryParse(data[1], out int id))
                        return id;
                }
            }
            return 0; //Process not found
        }

        public Task LaunchApp(string apk_id, string activityName, IDictionary<string, object> parameters)
        {
            StringBuilder launchCommand = new StringBuilder($"shell:am start -n {apk_id}/{activityName}");
            if (parameters != null && parameters.Any())
            {
                foreach (var pair in parameters)
                {
                    if (pair.Value is int || pair.Value is short)
                        launchCommand.Append($" --ei {pair.Key} {pair.Value}");
                    else if (pair.Value is long)
                        launchCommand.Append($" --el {pair.Key} \"{pair.Value}\"");
                    else if (pair.Value is bool b)
                        launchCommand.Append($" --ez {pair.Key} {(b ? "true" : "false")}");
                    else if (pair.Value is string)
                        launchCommand.Append($" --es {pair.Key} \"{pair.Value}\"");
                    else if (pair.Value is float || pair.Value is double)
                        launchCommand.Append($" --ef {pair.Key} \"{pair.Value}\"");
                    else
                        throw new ArgumentException($"Parameter type {pair.Value.GetType().Name} not supported for key value {pair.Key}");
                }
            }
            return _client.SendDeviceCommandAsync(launchCommand.ToString(), Serial);
        }

        public Task StopApp(string apk_id) => _client.SendDeviceCommandAsync($"shell:am force-stop {apk_id}", Serial);

        public Task SendKeyInput(string text) => _client.SendDeviceCommandAsync("shell:input text " + text, Serial);

        public Task SendSwipeEvent(int x1, int y1, int x2, int y2) => _client.SendDeviceCommandAsync($"shell:input touchscreen swipe {x1} {y1} {x2} {y2}", Serial);

        public Task SendUnlockButtonClick() => _client.SendDeviceCommandAsync("shell:input keyevent 26", Serial); //Unlock button

        public Task SendEnter() => _client.SendDeviceCommandAsync("shell:input keyevent 66", Serial); //Enter

        /// <summary>
        /// Sets up port forwarding for socket communication with device
        /// </summary>
        /// <param name="fromPort">From port</param>
        /// <param name="toPort">To port</param>
        /// <returns></returns>
        public Task SetPortForward(int fromPort, int toPort)
        {
            return _client.SendCommandAsync($"host-serial:{Serial}:forward:tcp:{fromPort};tcp:{toPort}");
        }

        /// <summary>
        /// Removes the exact local TCP forward owned by this runner.
        /// </summary>
        public Task RemovePortForward(int localPort)
        {
            return _client.SendCommandAsync($"host-serial:{Serial}:killforward:tcp:{localPort}");
        }
            
        public async Task TurnOnDisplayAsync()
        {
            await SendUnlockButtonClick();
            await SendSwipeEvent(930, 880, 930, 380);
        }

        public async Task UnlockAsync(string pin = null)
        {
            await SendKeyInput(pin.ToString());
            await SendEnter();
        }

        public bool SupportsCMD => AdbFeatures.Contains("cmd");
        public bool SupportsShellV2 => AdbFeatures.Contains("shell_v2");
        public bool SupportsStat2 => AdbFeatures.Contains("stat_v2");
        public bool SupportsLibusb => AdbFeatures.Contains("libusb");

        public async Task<PowerState> GetPowerState()
        {
            var dmpData = await _client.SendDeviceCommandGetDataAsync("shell:dumpsys power", Serial);
            string[] data = Encoding.UTF8.GetString(dmpData).Split('\n', StringSplitOptions.RemoveEmptyEntries); // Encoding.ASCII.GetString(dmpData).Split('\n');
			return new PowerState(data);
        }

        public enum DisplayState
        {
            Off,
            OnAndLocked,
            On
        }

        public class PowerState
        {
            internal PowerState(string[] data)
            {
                RawData = data;	
            }
			private bool KeyHasValue(string key, string value) => RawData.Where(d => d.TrimStart().StartsWith(key)).FirstOrDefault()?.TrimEnd()?.EndsWith($"={value}") == true;
            public bool HoldingDisplaySuspendBlocker => KeyHasValue("mHoldingDisplaySuspendBlocker", "true");
            public bool HoldingWakeLockSuspendBlocker => KeyHasValue("mHoldingWakeLockSuspendBlocker ", "true");
            public bool IsDisplayOn => HoldingDisplaySuspendBlocker;
            public DisplayState DisplayState => !IsDisplayOn ? DisplayState.Off : HoldingWakeLockSuspendBlocker ? DisplayState.On : DisplayState.OnAndLocked;

            public string[] RawData { get; }
        }

        public async Task InstallApk(string apkFilepath)
        {
            // ref: https://github.com/aosp-mirror/platform_system_core/blob/ec54ef7a8d762ca35f6b4a63925ab2e72ce85d13/adb/client/adb_install.cpp#L430-L434
            bool use_legacy_install = !SupportsCMD;
            if (use_legacy_install)
            {
                await InstallAppLegacy(apkFilepath);
            }
            else
            {
                using (var file = System.IO.File.OpenRead(apkFilepath))
                    await InstallApkStreamed(file);
            }
        }

        private async Task InstallAppLegacy(string apkFilepath)
        {
            // TODO: Upload package, then install from disk
            // InstallApkStreamed
            // var install_cmd = "exec:pm";
            // string DATA_DEST = "/data/local/tmp/%s";
            // string SD_DEST = "/sdcard/tmp/%s";
            throw new NotImplementedException("Device not currently supported for remote install. Install using ADB commandline");
        }

        private async Task InstallApkStreamed(System.IO.Stream apk)
        {
            if (apk == null)
            {
                throw new ArgumentNullException(nameof(apk));
            }

            if (!apk.CanRead || !apk.CanSeek)
            {
                throw new ArgumentOutOfRangeException(nameof(apk), "The apk stream must be a readable and seekable stream");
            }
            string command = $"exec:cmd package 'install' -S {apk.Length}";
            using (var s = await _client.OpenDeviceConnectionAsync(command, Serial))
            {
                byte[] buffer = new byte[32 * 1024];
                int read = 0;

                while ((read = apk.Read(buffer, 0, buffer.Length)) > 0)
                {
                    s.Send(buffer, read, System.Net.Sockets.SocketFlags.None);
                }

                read = s.Receive(buffer);
                var value = Encoding.UTF8.GetString(buffer, 0, read);
                if (!string.Equals(value, "Success\n"))
                {
                    throw new Exception(value);
                }
            }
        }
    }
}
