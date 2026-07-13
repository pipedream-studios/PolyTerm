using AwesomeAssertions;
using PolyTerm.App.CommandPalette;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Pure fuzzy-match/ranking logic for the M4 command palette — no UI dependency, no Avalonia headless needed.</summary>
public sealed class CommandPaletteFilterTests
{
    private static PaletteCommand Cmd(string title, string category = "Session") =>
        new(title, category, null, () => { });

    [Fact]
    public void EmptyQuery_ReturnsEverythingInOriginalOrder()
    {
        var commands = new[] { Cmd("New Connection…"), Cmd("Quick Connect…"), Cmd("New Local Shell") };

        var result = CommandPaletteFilter.Filter(commands, "");

        result.Should().Equal(commands);
    }

    [Fact]
    public void SubstringMatch_OnTitle_IsFound()
    {
        var commands = new[] { Cmd("New Connection…"), Cmd("Quick Connect…") };

        var result = CommandPaletteFilter.Filter(commands, "connect");

        result.Should().Contain(commands);
    }

    [Fact]
    public void SubstringMatch_IsCaseInsensitive()
    {
        var commands = new[] { Cmd("Toggle SFTP Panel") };

        CommandPaletteFilter.Filter(commands, "SFTP").Should().ContainSingle();
        CommandPaletteFilter.Filter(commands, "sftp").Should().ContainSingle();
    }

    [Fact]
    public void EarlierSubstringMatch_RanksAboveLaterOne()
    {
        var startsWith = Cmd("Recording: Start");
        var containsLater = Cmd("Stop Recording");

        var result = CommandPaletteFilter.Filter([containsLater, startsWith], "recording");

        // Both contain "Recording", but startsWith's match begins earlier in "Category: Title".
        result[0].Should().Be(startsWith);
    }

    [Fact]
    public void SubsequenceMatch_FindsNonContiguousCharacters_LikeAFuzzyPaletteSearch()
    {
        var commands = new[] { Cmd("New Local Shell") };

        // "nlsh" matches N-ew L-ocal Sh-ell in order, not contiguously — the classic VS Code
        // "Ctrl+Shift+P" fuzzy-match behavior this is modeled on.
        var result = CommandPaletteFilter.Filter(commands, "nlsh");

        result.Should().ContainSingle();
    }

    [Fact]
    public void SubstringMatch_AlwaysRanksAboveASubsequenceOnlyMatch()
    {
        var substringHit = Cmd("Manage Macros…");     // contains "mac" as a real substring
        var subsequenceHit = Cmd("Make A Connection"); // "mac" only as scattered characters

        var result = CommandPaletteFilter.Filter([subsequenceHit, substringHit], "mac");

        result[0].Should().Be(substringHit);
    }

    [Fact]
    public void NoMatchingCharacters_ExcludesTheCommand()
    {
        var commands = new[] { Cmd("New Connection…") };

        CommandPaletteFilter.Filter(commands, "xyz123").Should().BeEmpty();
    }

    [Fact]
    public void QueryMatchingCategory_AlsoFindsTheCommand()
    {
        var commands = new[] { Cmd("Rate Limit…", category: "MCP") };

        CommandPaletteFilter.Filter(commands, "mcp").Should().ContainSingle();
    }
}
