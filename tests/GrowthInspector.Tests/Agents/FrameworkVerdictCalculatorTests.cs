using System.Text.Json;
using GrowthInspector.Agents.Execution;
using GrowthInspector.Core.Models;

namespace GrowthInspector.Tests.Agents;

/// <summary>TPG-2709：框架复核的取值、比较符与失败语义。</summary>
public sealed class FrameworkVerdictCalculatorTests
{
    [Fact]
    public void NumericAssertionSatisfied_ReturnsOk()
    {
        var item = Item(ComparisonOperator.Lt, "500");
        var result = FrameworkVerdictCalculator.Compute(item, [Log("""{"value":100}""")]);

        Assert.Equal(FrameworkVerdictStatus.Ok, result.Verdict);
        Assert.Equal("100", result.RecomputedValue);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void NumericAssertionViolated_ReturnsAbnormal()
    {
        var item = Item(ComparisonOperator.Gte, "500");
        var result = FrameworkVerdictCalculator.Compute(item, [Log("""{"value":400}""")]);

        Assert.Equal(FrameworkVerdictStatus.Abnormal, result.Verdict);
        Assert.Equal("400", result.RecomputedValue);
    }

    [Fact]
    public void EqPrefersNumericComparison_WhenBothSidesNumeric()
    {
        var item = Item(ComparisonOperator.Eq, "500.0");
        var result = FrameworkVerdictCalculator.Compute(item, [Log("""{"value":"500"}""")]);

        Assert.Equal(FrameworkVerdictStatus.Ok, result.Verdict);
    }

    [Fact]
    public void EqFallsBackToOrdinalStringComparison()
    {
        var equal = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Eq, "healthy"),
            [Log("""{"value":"healthy"}""")]);
        var caseMismatch = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Eq, "healthy"),
            [Log("""{"value":"HEALTHY"}""")]);

        Assert.Equal(FrameworkVerdictStatus.Ok, equal.Verdict);
        Assert.Equal(FrameworkVerdictStatus.Abnormal, caseMismatch.Verdict);
    }

    [Fact]
    public void ContainsAndNotContains_UseOrdinalSubstring()
    {
        var contains = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Contains, "timeout"),
            [Log("""{"value":"upstream timeout"}""")]);
        var notContains = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.NotContains, "timeout"),
            [Log("""{"value":"upstream timeout"}""")]);

        Assert.Equal(FrameworkVerdictStatus.Ok, contains.Verdict);
        Assert.Equal(FrameworkVerdictStatus.Abnormal, notContains.Verdict);
    }

    [Fact]
    public void NonNumericValueForOrderingOperator_ReturnsError()
    {
        var result = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Lt, "500"),
            [Log("""{"value":"slow"}""")]);

        Assert.Equal(FrameworkVerdictStatus.Error, result.Verdict);
        Assert.Contains("需要数值", result.ErrorMessage);
    }

    [Fact]
    public void TruncatedResult_ReturnsError()
    {
        var result = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Lt, "500"),
            [Log("""{"value":100}""", truncated: true)]);

        Assert.Equal(FrameworkVerdictStatus.Error, result.Verdict);
        Assert.Contains("截断", result.ErrorMessage);
    }

    [Fact]
    public void MissingSuccessfulCall_ReturnsError()
    {
        var failed = Log("""{"value":100}""", succeeded: false);
        var otherTool = Log("""{"value":100}""", toolName: "mock__Other");
        var otherRole = Log("""{"value":100}""", role: AgentRole.Reflection);

        var result = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Lt, "500"),
            [failed, otherTool, otherRole]);

        Assert.Equal(FrameworkVerdictStatus.Error, result.Verdict);
        Assert.Contains("找不到", result.ErrorMessage);
    }

    [Fact]
    public void MissingValuePath_ReturnsError()
    {
        var result = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Lt, "500"),
            [Log("""{"other":100}""")]);

        Assert.Equal(FrameworkVerdictStatus.Error, result.Verdict);
        Assert.Contains("取值路径", result.ErrorMessage);
    }

    [Fact]
    public void InformationalItem_ReturnsInsufficientEvidence()
    {
        var item = new ChecklistSnapshotItem(
            Guid.NewGuid(),
            "信息型",
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            1,
            null,
            null);

        var result = FrameworkVerdictCalculator.Compute(item, []);

        Assert.Equal(FrameworkVerdictStatus.InsufficientEvidence, result.Verdict);
        Assert.Null(result.RecomputedValue);
    }

    [Fact]
    public void NestedValuePath_ResolvesArrayElement()
    {
        var item = Item(ComparisonOperator.Lt, "500", valuePath: "$.data[0].v");
        var result = FrameworkVerdictCalculator.Compute(
            item,
            [Log("""{"data":[{"v":42}]}""")]);

        Assert.Equal(FrameworkVerdictStatus.Ok, result.Verdict);
        Assert.Equal("42", result.RecomputedValue);
    }

    [Fact]
    public void LatestSuccessfulCallWins()
    {
        var older = Log("""{"value":900}""", createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = Log("""{"value":100}""", createdAt: DateTimeOffset.UtcNow);

        var result = FrameworkVerdictCalculator.Compute(
            Item(ComparisonOperator.Lt, "500"),
            [older, newer]);

        Assert.Equal(FrameworkVerdictStatus.Ok, result.Verdict);
        Assert.Equal("100", result.RecomputedValue);
    }

    [Fact]
    public void ClaimParser_AcceptsJsonInsideProseAndCodeFence()
    {
        var itemId = Guid.NewGuid();
        var text = $$"""
            下面是判定：
            ```json
            {
              "checklistItemId": "{{itemId}}",
              "toolName": "mock__QueryMetric",
              "value": "123",
              "evidence": "p99=123",
              "verdict": "ok",
              "narrative": "延迟正常",
              "observations": ["观察一"]
            }
            ```
            """;

        var parsed = ExecutionClaimParser.TryParse(text, itemId, out var claim, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(claim);
        Assert.Equal(VerdictStatus.Ok, claim!.Verdict);
        Assert.Equal("123", claim.Value);
        Assert.Single(claim.Observations);
    }

    [Fact]
    public void ClaimParser_AcceptsUnderscoreVerdictSpelling()
    {
        var itemId = Guid.NewGuid();
        var text = JsonSerializer.Serialize(new
        {
            checklistItemId = itemId,
            verdict = "insufficient_evidence",
            narrative = "证据不足",
        });

        Assert.True(ExecutionClaimParser.TryParse(text, itemId, out var claim, out _));
        Assert.Equal(VerdictStatus.InsufficientEvidence, claim!.Verdict);
    }

    [Fact]
    public void ClaimParser_RejectsInvalidJson()
    {
        Assert.False(ExecutionClaimParser.TryParse("不是 JSON", Guid.NewGuid(), out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ClaimParser_RejectsMismatchedItemIdOrVerdict()
    {
        var itemId = Guid.NewGuid();
        var mismatched = JsonSerializer.Serialize(new
        {
            checklistItemId = Guid.NewGuid(),
            verdict = "Ok",
        });
        var invalidVerdict = JsonSerializer.Serialize(new
        {
            checklistItemId = itemId,
            verdict = "Good",
        });

        Assert.False(ExecutionClaimParser.TryParse(mismatched, itemId, out _, out _));
        Assert.False(ExecutionClaimParser.TryParse(invalidVerdict, itemId, out _, out _));
    }

    private static ChecklistSnapshotItem Item(
        ComparisonOperator comparison,
        string threshold,
        string valuePath = "$.value",
        string toolName = "mock__QueryMetric")
        => new(
            Guid.NewGuid(),
            "判定项",
            0,
            toolName,
            """{"metric":"p99"}""",
            valuePath,
            comparison,
            threshold,
            "1h",
            Severity.Critical,
            1,
            null,
            null);

    private static ToolCallLog Log(
        string result,
        bool succeeded = true,
        bool truncated = false,
        string toolName = "mock__QueryMetric",
        AgentRole role = AgentRole.Execution,
        DateTimeOffset? createdAt = null)
        => new()
        {
            ChecklistRunId = Guid.NewGuid(),
            ChecklistItemId = Guid.NewGuid(),
            AgentRole = role,
            ToolName = toolName,
            Result = result,
            Succeeded = succeeded,
            Truncated = truncated,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
}
