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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MSTestX.Console.Tests;

[TestClass]
public class ProgramTests
{
    [TestMethod]
    public void ParseArguments_ForwardsApplicationArgumentsVerbatimAndInOrder()
    {
        var parsed = Program.ParseArguments(new[]
        {
            "-apppath",
            "/tmp/My Tests.app",
            "--",
            "--tag",
            "first",
            "--tag",
            "second",
            "--mode",
            "-diagnostic",
            "value with spaces",
            "\"literal quotes\"",
            "",
            "--"
        });

        Assert.AreEqual("/tmp/My Tests.app", parsed.ConsoleOptions["apppath"]);
        CollectionAssert.AreEqual(
            new[]
            {
                "--tag",
                "first",
                "--tag",
                "second",
                "--mode",
                "-diagnostic",
                "value with spaces",
                "\"literal quotes\"",
                "",
                "--"
            },
            parsed.ApplicationArguments.ToArray());
    }

    [TestMethod]
    public void ParseArguments_AllowsDelimiterWithoutApplicationArguments()
    {
        var parsed = Program.ParseArguments(new[] { "-apkid", "com.example.tests", "--" });

        Assert.AreEqual("com.example.tests", parsed.ConsoleOptions["apkid"]);
        Assert.AreEqual(0, parsed.ApplicationArguments.Count);
    }

    [TestMethod]
    public void ParseArguments_PreservesLegacyBehaviorWithoutDelimiter()
    {
        var parsed = Program.ParseArguments(new[]
        {
            "-apppath",
            "/tmp/Test.app",
            "-device",
            "My Phone",
            "--filter",
            "TestCategory=Smoke"
        });

        Assert.AreEqual(3, parsed.ConsoleOptions.Count);
        Assert.AreEqual("/tmp/Test.app", parsed.ConsoleOptions["apppath"]);
        Assert.AreEqual("My Phone", parsed.ConsoleOptions["device"]);
        Assert.AreEqual("TestCategory=Smoke", parsed.ConsoleOptions["filter"]);
        Assert.AreEqual(0, parsed.ApplicationArguments.Count);
    }

    [DataTestMethod]
    [DataRow("-adbServerPort", "0")]
    [DataRow("-adbServerPort", "65536")]
    [DataRow("-adbServerPort", "not-a-port")]
    [DataRow("-adapterConnectionTimeoutSeconds", "0")]
    [DataRow("-adapterConnectionTimeoutSeconds", "-1")]
    [DataRow("-adapterConnectionTimeoutSeconds", "not-a-timeout")]
    public void ParseArguments_RejectsInvalidAndroidConnectionOptions(string option, string value)
    {
        var exception = Assert.ThrowsException<ArgumentException>(
            () => Program.ParseArguments(new[] { "-apkid", "com.example.tests", option, value }));

        StringAssert.Contains(exception.Message, option.TrimStart('-'));
    }

    [TestMethod]
    public void ParseArguments_AcceptsAndroidConnectionOptions()
    {
        var parsed = Program.ParseArguments(new[]
        {
            "-apkid", "com.example.tests",
            "-adbServerPort", "5038",
            "-adapterConnectionTimeoutSeconds", "15"
        });

        Assert.AreEqual("5038", parsed.ConsoleOptions["adbServerPort"]);
        Assert.AreEqual("15", parsed.ConsoleOptions["adapterConnectionTimeoutSeconds"]);
    }

    [TestMethod]
    public async Task RunCleanupStepsAsync_AttemptsEveryStageAfterFailure()
    {
        var attempted = new List<string>();

        await Program.RunCleanupStepsAsync(new (string Name, Func<Task> Action)[]
        {
            ("first", () =>
            {
                attempted.Add("first");
                throw new InvalidOperationException("expected");
            }),
            ("second", () =>
            {
                attempted.Add("second");
                return Task.CompletedTask;
            }),
            ("third", () =>
            {
                attempted.Add("third");
                return Task.CompletedTask;
            })
        });

        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, attempted);
    }

    [TestMethod]
    public async Task WaitForRemoteEndpointAsync_PropagatesCallerCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var cancellationTokenSource = new CancellationTokenSource();
        var waitTask = Program.WaitForRemoteEndpointAsync(
            listener,
            TimeSpan.FromSeconds(5),
            cancellationTokenSource.Token);

        cancellationTokenSource.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => waitTask);
    }

    [TestMethod]
    public async Task WaitForRemoteEndpointAsync_ReportsTimeoutSeparatelyFromCallerCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            Program.WaitForRemoteEndpointAsync(
                listener,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task WaitForRemoteEndpointAsync_ReturnsAdapterPortForConnectedHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var waitTask = Program.WaitForRemoteEndpointAsync(
            listener,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var listenerEndpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient();

        await client.ConnectAsync(listenerEndpoint.Address, listenerEndpoint.Port);
        var adapterEndpoint = await waitTask;

        Assert.AreEqual(IPAddress.Loopback, adapterEndpoint.Address);
        Assert.AreEqual(38300, adapterEndpoint.Port);
    }

    [TestMethod]
    public void ParseArguments_RejectsApplicationArgumentsForAndroid()
    {
        var exception = Assert.ThrowsException<ArgumentException>(
            () => Program.ParseArguments(new[] { "-apkid", "com.example.tests", "--", "--tag", "smoke" }));

        StringAssert.Contains(exception.Message, "Android");
        StringAssert.Contains(exception.Message, "-apppath");
    }

    [TestMethod]
    public void ParseArguments_RejectsApplicationArgumentsForRemoteLaunch()
    {
        var exception = Assert.ThrowsException<ArgumentException>(
            () => Program.ParseArguments(new[]
            {
                "-remoteIp",
                "127.0.0.1:38300",
                "-apppath",
                "/tmp/Test.app",
                "--",
                "--tag",
                "smoke"
            }));

        StringAssert.Contains(exception.Message, "-remoteIp");
        StringAssert.Contains(exception.Message, "caller-launched");
    }

    [DataTestMethod]
    [DataRow("--TestAdapterPort")]
    [DataRow("-testadapterport")]
    [DataRow("--AUTOEXIT")]
    [DataRow("-autoexit")]
    public void ParseArguments_RejectsReservedApplicationArgumentOverrides(string reservedArgument)
    {
        var exception = Assert.ThrowsException<ArgumentException>(
            () => Program.ParseArguments(new[]
            {
                "-apppath",
                "/tmp/Test.app",
                "--",
                "--tag",
                "smoke",
                reservedArgument,
                "caller-value"
            }));

        StringAssert.Contains(exception.Message, reservedArgument);
        StringAssert.Contains(exception.Message, "reserved");
        StringAssert.Contains(exception.Message, "cannot be overridden");
    }

    [TestMethod]
    public void GetLaunchMode_UsesRemoteAdapter_WhenRemoteIpIsProvided()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["remoteIp"] = "127.0.0.1:38300"
        };

        Assert.AreEqual(LaunchMode.RemoteAdapter, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_UsesRemoteAdapter_WhenWaitForRemoteIsProvided()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["waitForRemote"] = null
        };

        Assert.AreEqual(LaunchMode.RemoteAdapter, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_PrefersRemoteAdapter_OverAppPath()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["remoteIp"] = "127.0.0.1:38300",
            ["apppath"] = "/tmp/Test.app"
        };

        Assert.AreEqual(LaunchMode.RemoteAdapter, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_UsesAppleApp_WhenAppPathIsProvidedWithoutRemoteArguments()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["apppath"] = "/tmp/Test.app"
        };

        Assert.AreEqual(LaunchMode.AppleApp, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_DefaultsToAndroidAdb_WhenNoRemoteOrAppleArgumentsAreProvided()
    {
        var arguments = new Dictionary<string, string?>();

        Assert.AreEqual(LaunchMode.AndroidAdb, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void FormatDuration_FormatsSubMillisecondDuration()
    {
        Assert.AreEqual("[< 1ms]", TimeSpan.Zero.FormatDuration());
    }

    [TestMethod]
    public void FormatDuration_FormatsSecondScaleDuration()
    {
        Assert.AreEqual("[1s 250ms]", TimeSpan.FromMilliseconds(1250).FormatDuration());
    }

    [TestMethod]
    public void GetRedirectedOutcomeLabel_UsesStableLabels()
    {
        Assert.AreEqual("PASS", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Passed));
        Assert.AreEqual("FAIL", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Failed));
        Assert.AreEqual("SKIP", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Skipped));
        Assert.AreEqual("NONE", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.None));
        Assert.AreEqual("NOTFOUND", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.NotFound));
    }

    [TestMethod]
    public void GetAppLogFilename_UsesSiblingLogFile()
    {
        Assert.AreEqual("/tmp/results/run.log", Program.GetAppLogFilename("/tmp/results/run.trx"));
    }

    [TestMethod]
    public void GetAppLogFilename_ReturnsNull_WhenTrxPathIsMissing()
    {
        Assert.IsNull(Program.GetAppLogFilename(null));
        Assert.IsNull(Program.GetAppLogFilename(""));
    }

    [TestMethod]
    public void MobileDevice_IsUnexpectedTunnelExit_ReturnsFalse_ForIntentionalShutdown()
    {
        Assert.IsFalse(MobileDevice.IsUnexpectedTunnelExit(isIntentionalShutdown: true));
        Assert.IsTrue(MobileDevice.IsUnexpectedTunnelExit(isIntentionalShutdown: false));
    }

    [TestMethod]
    public void MobileDevice_FormatTunnelExitMessage_LabelsIntentionalShutdown()
    {
        var message = MobileDevice.FormatTunnelExitMessage(
            exitCode: 0,
            lastMessage: "closed by test runner",
            isIntentionalShutdown: true);

        StringAssert.Contains(message, "stopped intentionally");
        StringAssert.Contains(message, "closed by test runner");
    }

    [TestMethod]
    public void MobileDevice_FormatTunnelExitMessage_LabelsUnexpectedExit()
    {
        var message = MobileDevice.FormatTunnelExitMessage(
            exitCode: 1,
            lastMessage: "lost connection",
            isIntentionalShutdown: false);

        StringAssert.Contains(message, "exited unexpectedly");
        StringAssert.Contains(message, "lost connection");
    }
}
