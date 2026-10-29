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
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MSTestX.Console.Tests;

[TestClass]
public class TestRunnerTests
{
    private static readonly Uri ExecutorUri = new("executor://mstestx");
    private static readonly TestProperty TestCategoryProperty = TestProperty.Register(
        "MSTestX.Console.Tests.TestCategory",
        "TestCategory",
        typeof(string[]),
        typeof(TestRunnerTests));

    [TestMethod]
    public void SelectTests_ReturnsAllDiscoveredTests_WhenFilterIsMissing()
    {
        var tests = new[]
        {
            CreateTestCase("Samples.First", Guid.NewGuid()),
            CreateTestCase("Samples.Second", Guid.NewGuid())
        };

        var selected = TestCaseSelector.SelectTests(tests, "<RunSettings />");

        CollectionAssert.AreEqual(tests.Select(test => test.Id).ToArray(), selected.Select(test => test.Id).ToArray());
    }

    [TestMethod]
    public void SelectTests_MatchesExactFullyQualifiedName()
    {
        var expected = CreateTestCase("Samples.ExactTest", Guid.NewGuid());
        var tests = new[]
        {
            CreateTestCase("Samples.OtherTest", Guid.NewGuid()),
            expected
        };

        var selected = TestCaseSelector.SelectTests(
            tests,
            Settings("FullyQualifiedName=Samples.ExactTest"));

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(expected.Id, selected[0].Id);
    }

    [TestMethod]
    public void SelectTests_MatchesContainsFilter()
    {
        var first = CreateTestCase("Samples.Mapping.First", Guid.NewGuid());
        var second = CreateTestCase("Samples.Mapping.Second", Guid.NewGuid());
        var tests = new[]
        {
            first,
            CreateTestCase("Samples.Geometry.Test", Guid.NewGuid()),
            second
        };

        var selected = TestCaseSelector.SelectTests(
            tests,
            Settings("FullyQualifiedName~Mapping"));

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id },
            selected.Select(test => test.Id).ToArray());
    }

    [TestMethod]
    public void SelectTests_MatchesCategoriesAndTraits()
    {
        var smoke = CreateTestCase(
            "Samples.Smoke",
            Guid.NewGuid(),
            new Trait("Owner", "Maps"));
        var regression = CreateTestCase(
            "Samples.Regression",
            Guid.NewGuid(),
            new Trait("Owner", "Maps"));
        smoke.SetPropertyValue(TestCategoryProperty, new[] { "Smoke", "Fast" });
        regression.SetPropertyValue(TestCategoryProperty, new[] { "Regression" });

        var selected = TestCaseSelector.SelectTests(
            new[] { smoke, regression },
            Settings("TestCategory=Smoke&Owner=Maps"));

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(smoke.Id, selected[0].Id);
    }

    [TestMethod]
    public void SelectTests_MatchesBooleanAndEscapedExpressions()
    {
        var escaped = CreateTestCase(
            "Samples.Name|With(Characters)",
            Guid.NewGuid(),
            new Trait("TestCategory", "Smoke"));
        var fallback = CreateTestCase(
            "Samples.Fallback",
            Guid.NewGuid(),
            new Trait("TestCategory", "Regression"));
        var excluded = CreateTestCase(
            "Samples.Excluded",
            Guid.NewGuid(),
            new Trait("TestCategory", "Smoke"));

        var selected = TestCaseSelector.SelectTests(
            new[] { escaped, fallback, excluded },
            Settings(@"(FullyQualifiedName=Samples.Name\|With\(Characters\)&TestCategory=Smoke)|FullyQualifiedName=Samples.Fallback"));

        CollectionAssert.AreEqual(
            new[] { escaped.Id, fallback.Id },
            selected.Select(test => test.Id).ToArray());
    }

    [TestMethod]
    public void SelectTests_ThrowsActionableError_ForInvalidFilter()
    {
        const string invalidFilter = "(FullyQualifiedName=Samples.Test";

        var exception = Assert.ThrowsException<FormatException>(
            () => TestCaseSelector.SelectTests(
                new[] { CreateTestCase("Samples.Test", Guid.NewGuid()) },
                Settings(invalidFilter)));

        StringAssert.Contains(exception.Message, "Invalid test case filter");
        StringAssert.Contains(exception.Message, invalidFilter);
        StringAssert.Contains(exception.Message, "Incorrect format");
    }

    [TestMethod]
    public void SelectTests_ReturnsEmptyList_WhenValidFilterMatchesNothing()
    {
        var selected = TestCaseSelector.SelectTests(
            new[] { CreateTestCase("Samples.Test", Guid.NewGuid()) },
            Settings("FullyQualifiedName=Samples.Missing"));

        Assert.AreEqual(0, selected.Count);
    }

    [TestMethod]
    public void SelectTests_MatchesAllDataRowsSharingFullyQualifiedName()
    {
        var firstRow = CreateTestCase("Samples.DataTest", Guid.NewGuid());
        var secondRow = CreateTestCase("Samples.DataTest", Guid.NewGuid());

        var selected = TestCaseSelector.SelectTests(
            new[] { firstRow, secondRow, CreateTestCase("Samples.Other", Guid.NewGuid()) },
            Settings("FullyQualifiedName=Samples.DataTest"));

        CollectionAssert.AreEqual(
            new[] { firstRow.Id, secondRow.Id },
            selected.Select(test => test.Id).ToArray());
    }

    [TestMethod]
    public void SelectTests_MatchesSingleDataRowById()
    {
        var firstRow = CreateTestCase("Samples.DataTest", Guid.NewGuid());
        var secondRow = CreateTestCase("Samples.DataTest", Guid.NewGuid());

        var selected = TestCaseSelector.SelectTests(
            new[] { firstRow, secondRow },
            Settings($"Id={secondRow.Id}"));

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(secondRow.Id, selected[0].Id);
    }

    [TestMethod]
    public void ShouldWriteTestResult_ReturnsTrue_ForTopLevelResult()
    {
        Assert.IsTrue(TestRunner.ShouldWriteTestResult(Guid.Empty, isOutputRedirected: false));
        Assert.IsTrue(TestRunner.ShouldWriteTestResult(Guid.Empty, isOutputRedirected: true));
    }

    [TestMethod]
    public void ShouldWriteRunningTest_ReturnsTrue_ForTopLevelResult_WhenOutputIsInteractive()
    {
        Assert.IsTrue(TestRunner.ShouldWriteRunningTest(Guid.Empty, isOutputRedirected: false));
    }

    [TestMethod]
    public void ShouldWriteRunningTest_ReturnsFalse_ForTopLevelResult_WhenOutputIsRedirected()
    {
        Assert.IsFalse(TestRunner.ShouldWriteRunningTest(Guid.Empty, isOutputRedirected: true));
    }

    [TestMethod]
    public void ShouldWriteTestResult_ReturnsTrue_ForChildResult_WhenOutputIsInteractive()
    {
        Assert.IsTrue(TestRunner.ShouldWriteTestResult(Guid.NewGuid(), isOutputRedirected: false));
    }

    [TestMethod]
    public void ShouldWriteTestResult_ReturnsFalse_ForChildResult_WhenOutputIsRedirected()
    {
        Assert.IsFalse(TestRunner.ShouldWriteTestResult(Guid.NewGuid(), isOutputRedirected: true));
    }

    [TestMethod]
    public void ShouldWriteRunningTest_ReturnsTrue_ForChildResult_WhenOutputIsInteractive()
    {
        Assert.IsTrue(TestRunner.ShouldWriteRunningTest(Guid.NewGuid(), isOutputRedirected: false));
    }

    [TestMethod]
    public void ShouldWriteRunningTest_ReturnsFalse_ForChildResult_WhenOutputIsRedirected()
    {
        Assert.IsFalse(TestRunner.ShouldWriteRunningTest(Guid.NewGuid(), isOutputRedirected: true));
    }

    [TestMethod]
    public void ShouldWriteRunningTest_ReturnsFalse_ForRedirectedOutput_RegardlessOfParentExecId()
    {
        Assert.IsFalse(TestRunner.ShouldWriteRunningTest(Guid.Empty, isOutputRedirected: true));
        Assert.IsFalse(TestRunner.ShouldWriteRunningTest(Guid.NewGuid(), isOutputRedirected: true));
    }

    [TestMethod]
    public void ShouldWriteFailureDetails_ReturnsTrue_ForTopLevelFailureWithMessage()
    {
        Assert.IsTrue(TestRunner.ShouldWriteFailureDetails(Guid.Empty, isOutputRedirected: false, "boom"));
        Assert.IsTrue(TestRunner.ShouldWriteFailureDetails(Guid.Empty, isOutputRedirected: true, "boom"));
    }

    [TestMethod]
    public void ShouldWriteFailureDetails_ReturnsTrue_ForChildFailureWithMessage_WhenOutputIsInteractive()
    {
        Assert.IsTrue(TestRunner.ShouldWriteFailureDetails(Guid.NewGuid(), isOutputRedirected: false, "boom"));
    }

    [TestMethod]
    public void ShouldWriteFailureDetails_ReturnsFalse_ForChildFailureWithMessage_WhenOutputIsRedirected()
    {
        Assert.IsFalse(TestRunner.ShouldWriteFailureDetails(Guid.NewGuid(), isOutputRedirected: true, "boom"));
    }

    [TestMethod]
    public void ShouldWriteFailureDetails_ReturnsFalse_WhenFailureMessageIsMissing()
    {
        Assert.IsFalse(TestRunner.ShouldWriteFailureDetails(Guid.Empty, isOutputRedirected: false, null));
        Assert.IsFalse(TestRunner.ShouldWriteFailureDetails(Guid.Empty, isOutputRedirected: false, string.Empty));
        Assert.IsFalse(TestRunner.ShouldWriteFailureDetails(Guid.Empty, isOutputRedirected: true, null));
        Assert.IsFalse(TestRunner.ShouldWriteFailureDetails(Guid.Empty, isOutputRedirected: true, string.Empty));
    }

    [TestMethod]
    public void GetRedirectedOutcomeLabel_UsesStableLabels()
    {
        Assert.AreEqual("PASS", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Passed));
        Assert.AreEqual("FAIL", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Failed));
        Assert.AreEqual("SKIP", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Skipped));
        Assert.AreEqual("NONE", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.None));
    }

    [TestMethod]
    public void GetRedirectedOutcomeLabel_PreservesNonPassFailOutcomeNames()
    {
        Assert.AreEqual("NOTFOUND", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.NotFound));
    }

    [TestMethod]
    public void FormatRedirectedRunningTestLine_UsesRunPrefix()
    {
        Assert.AreEqual("RUN  Sample.Test", TestRunner.FormatRedirectedRunningTestLine("Sample.Test"));
    }

    [TestMethod]
    public void FormatRedirectedResultLine_UsesStableRedirectedFormat()
    {
        Assert.AreEqual("FAIL Sample.Test [1s 250ms]", TestRunner.FormatRedirectedResultLine(TestOutcome.Failed, "Sample.Test", TimeSpan.FromMilliseconds(1250)));
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
    public void TestRunDiagnostics_FormatsAbortSummary_WithRunState()
    {
        var diagnostics = new TestRunDiagnostics("/tmp/run.log");

        diagnostics.SetPhase("running tests");
        diagnostics.RecordSelectedTests(2);
        diagnostics.RecordMessage("TestExecution.TestResult");
        diagnostics.RecordResult("Sample.Test", "Passed", TimeSpan.FromMilliseconds(42));

        var summary = diagnostics.FormatAbortSummary();

        StringAssert.StartsWith(summary, "MSTestX abort: adapter disconnected before ExecutionComplete");
        StringAssert.Contains(summary, "phase=running tests");
        StringAssert.Contains(summary, "selectedTests=2");
        StringAssert.Contains(summary, "results=1");
        StringAssert.Contains(summary, "lastResult=Passed Sample.Test [42ms]");
        StringAssert.Contains(summary, "lastMessage=TestExecution.TestResult");
        StringAssert.Contains(summary, "executionCompleteReceived=False");
        StringAssert.Contains(summary, "appLog=/tmp/run.log");
    }

    [TestMethod]
    public void IsDisconnectBeforeExecutionComplete_ReturnsTrue_UntilCompletionMessageArrives()
    {
        var diagnostics = new TestRunDiagnostics(null);

        Assert.IsTrue(TestRunner.IsDisconnectBeforeExecutionComplete(diagnostics));

        diagnostics.RecordMessage(Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel.MessageType.ExecutionComplete);

        Assert.IsFalse(TestRunner.IsDisconnectBeforeExecutionComplete(diagnostics));
    }

    [TestMethod]
    public void GetOutcomeCount_ReturnsZero_WhenStatisticsOrOutcomeAreMissing()
    {
        Assert.AreEqual(0, TestRunner.GetOutcomeCount(null, TestOutcome.Passed));

        var statistics = new TestRunStatistics(
            executedTests: 2,
            new Dictionary<TestOutcome, long>
            {
                [TestOutcome.Failed] = 2
            });

        Assert.AreEqual(0, TestRunner.GetOutcomeCount(statistics, TestOutcome.Passed));
        Assert.AreEqual(2, TestRunner.GetOutcomeCount(statistics, TestOutcome.Failed));
    }

    [TestMethod]
    public void IsUnsuccessfulCompletion_ReturnsTrue_ForErrorAbortedOrCanceled()
    {
        Assert.IsFalse(TestRunner.IsUnsuccessfulCompletion(
            new TestRunCompleteEventArgs(null, false, false, null, null, TimeSpan.Zero)));
        Assert.IsTrue(TestRunner.IsUnsuccessfulCompletion(
            new TestRunCompleteEventArgs(null, false, false, new InvalidOperationException("boom"), null, TimeSpan.Zero)));
        Assert.IsTrue(TestRunner.IsUnsuccessfulCompletion(
            new TestRunCompleteEventArgs(null, false, true, null, null, TimeSpan.Zero)));
        Assert.IsTrue(TestRunner.IsUnsuccessfulCompletion(
            new TestRunCompleteEventArgs(null, true, false, null, null, TimeSpan.Zero)));
    }

    [TestMethod]
    public void CompletedAssertionFailures_PreserveSuccessfulProcessContract()
    {
        var completion = new TestRunCompleteEventArgs(
            new TestRunStatistics(
                executedTests: 2,
                new Dictionary<TestOutcome, long>
                {
                    [TestOutcome.Passed] = 1,
                    [TestOutcome.Failed] = 1
                }),
            false,
            false,
            null,
            null,
            TimeSpan.FromSeconds(1));

        Assert.IsFalse(TestRunner.IsUnsuccessfulCompletion(completion));
    }

    [TestMethod]
    public void TestRunCompletedWithErrorException_UsesCompletionFailureMessage()
    {
        var completion = new TestRunCompleteEventArgs(
            null,
            false,
            false,
            new InvalidOperationException("boom"),
            null,
            TimeSpan.Zero);

        var exception = new TestRunCompletedWithErrorException(completion);

        Assert.AreEqual("Test run completed with error: boom", exception.Message);
    }

    [TestMethod]
    public void TestRunAbortedException_UsesStructuredDiagnosticMessage()
    {
        var diagnostics = new TestRunDiagnostics("/tmp/run.log");
        diagnostics.SetPhase("adapter disconnected");

        var exception = new TestRunAbortedException(
            diagnostics,
            new System.IO.EndOfStreamException());

        StringAssert.StartsWith(exception.Message, "MSTestX abort: adapter disconnected before ExecutionComplete");
        StringAssert.Contains(exception.Message, "phase=adapter disconnected");
        Assert.AreSame(diagnostics, exception.Diagnostics);
        Assert.IsInstanceOfType(exception.InnerException, typeof(System.IO.EndOfStreamException));
    }

    private static TestCase CreateTestCase(string fullyQualifiedName, Guid id, params Trait[] traits)
    {
        var testCase = new TestCase(fullyQualifiedName, ExecutorUri, "Tests.dll")
        {
            Id = id
        };

        foreach (var trait in traits)
        {
            testCase.Traits.Add(trait);
        }

        return testCase;
    }

    private static string Settings(string filter)
    {
        return $"<RunSettings><RunConfiguration><TestCaseFilter>{System.Security.SecurityElement.Escape(filter)}</TestCaseFilter></RunConfiguration></RunSettings>";
    }
}
