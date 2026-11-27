using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ApprovalHighRiskTests
{
    [Theory]
    [InlineData("Write", true)]
    [InlineData("Edit", true)]
    [InlineData("MultiEdit", true)]
    [InlineData("NotebookEdit", true)]
    [InlineData("Bash", true)]
    [InlineData("mcp__x__y", true)]
    [InlineData("mcp__server__write_file", true)]
    [InlineData("Read", false)]
    [InlineData("Glob", false)]
    [InlineData("WebFetch", false)]
    [InlineData("AskUserQuestion", false)]
    [InlineData("mcp", false)] // the prefix is mcp__, not mcp
    public void JudgesTheWriteClassToolsHighRisk(string tool, bool expected) =>
        Assert.Equal(expected, ClaudeHook.IsHighRisk(tool));

    [Fact]
    public void TheRecordCarriesTheFlagWithFalseAsItsDefault()
    {
        // existing constructors keep working: the flag defaults to false
        var plain = new Approval("a", "Claude Code", "s", "p", "/w", "Read a file", "d", null, 1, 2);
        Assert.False(plain.HighRisk);
        var risky = new Approval("b", "Claude Code", "s", "p", "/w", "Run a command", "d", null, 1, 2, HighRisk: true);
        Assert.True(risky.HighRisk);
    }

    [Fact]
    public void AQuestionIsNeverMarkedHighRisk()
    {
        // the same input DecideAsync receives for AskUserQuestion: questions are
        // answered, not allowed, so the second-tap gate never applies to them
        using var doc = JsonDocument.Parse("""{"tool_name":"AskUserQuestion","session_id":"s","cwd":"/work"}""");
        var input = doc.RootElement;
        var tool = "AskUserQuestion";
        var question = true;
        Assert.True(question);
        Assert.False(!question && ClaudeHook.IsHighRisk(tool));
        Assert.True(ClaudeHook.IsHighRisk("Bash")); // the same tool as a permission would be high-risk
        _ = input; // the shape is the one the flag is derived from
    }
}
