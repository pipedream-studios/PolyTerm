using AwesomeAssertions;
using PolyTerm.App.Surfaces.Terminal;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>
/// Covers <see cref="TerminalFonts"/>' choice of font family: the per-OS default, bundled families loading
/// from the app's own resources, and a saved family that isn't installed here falling back to a bundled
/// one instead of Avalonia's (proportional) default UI font. Installed-ness is injected, so none of this
/// needs a running font manager.
/// </summary>
public sealed class TerminalFontsTests
{
    [Theory]
    [InlineData("Windows", "Cascadia Mono")]
    [InlineData("MacOS", "Menlo")]
    [InlineData("Other", "JetBrains Mono")]
    public void DefaultFor_PicksTheExpectedFamilyPerOS(string os, string expected)
    {
        TerminalFonts.DefaultFor(Enum.Parse<TerminalFonts.OSPlatformKind>(os)).Should().Be(expected);
    }

    [Fact]
    public void EveryNonMacDefault_IsBundled_SoItNeverDependsOnWhatsInstalled()
    {
        TerminalFonts.IsBundled(TerminalFonts.DefaultFor(TerminalFonts.OSPlatformKind.Windows)).Should().BeTrue();
        TerminalFonts.IsBundled(TerminalFonts.DefaultFor(TerminalFonts.OSPlatformKind.Other)).Should().BeTrue();
        TerminalFonts.IsBundled(TerminalFonts.Fallback).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoChoice_UsesThePlatformDefault(string? saved)
    {
        TerminalFonts.Resolve(saved, _ => true).Name
            .Should().Be(TerminalFonts.Resolve(TerminalFonts.PlatformDefault, _ => true).Name);
    }

    [Fact]
    public void Resolve_BundledFamily_LoadsFromTheAppsResources_CaseInsensitively()
    {
        var family = TerminalFonts.Resolve("fira code", _ => false);

        family.Key.Should().NotBeNull("a bundled family is addressed through its avares:// source, not looked up by name");
        // Uri lowercases the host (the assembly name); avares:// resolution is case-insensitive there.
        family.Key!.Source.ToString().Should().BeEquivalentTo("avares://PolyTerm.App.Surfaces/Assets/Fonts");
        family.Name.Should().Be("Fira Code", "the bundled list's own casing - it has to match the font's internal family name");
    }

    [Fact]
    public void Resolve_InstalledFamily_IsUsedByName()
    {
        var family = TerminalFonts.Resolve("Ubuntu Mono", name => name == "Ubuntu Mono");

        family.Name.Should().Be("Ubuntu Mono");
        family.Key.Should().BeNull("an installed family is resolved by the OS font manager, not from the app's resources");
    }

    [Fact]
    public void Resolve_FamilyThatIsntInstalled_FallsBackToABundledFont()
    {
        // e.g. a settings file copied from a Mac (Menlo) to Linux.
        var family = TerminalFonts.Resolve("Menlo", _ => false);

        family.Name.Should().Be(TerminalFonts.Fallback);
        family.Key.Should().NotBeNull();
    }
}
