using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using PolyTerm.Protocols.Rdp;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// <see cref="RdpSession"/>'s drive/audio redirection settings (M4's "RDP audio/drive rungs") are
/// wired directly into a live <c>AxMsRdpClient11NotSafeForScripting</c> control inside
/// <c>ConnectCore</c>, which needs a real ActiveX/COM control and (for a full connect) a real RDP
/// server — not mockable, same limitation as the rest of this protocol module (see docs/todos.md).
/// This covers the two things that *are* pure logic: the schema exposing the new fields with the
/// right defaults, and the setting-to-control-value mapping (<see cref="RdpSession.AudioModeToValue"/>/
/// <see cref="RdpSession.GetBoolSetting"/>, made <c>internal</c> + <c>InternalsVisibleTo</c> for this
/// purpose) — the actual control-property values these feed were confirmed against a live,
/// unconnected instance of the real control in a standalone probe (not assumed from docs), per this
/// project's usual verification discipline; see the comment in RdpSession.ConnectCore.
/// </summary>
public sealed class RdpAudioDriveSettingsTests
{
    [Fact]
    public void Schema_ExposesDriveAndAudioRedirectionFields_WithControlMatchingDefaults()
    {
        var descriptor = new RdpProtocolDescriptor(NullLoggerFactory.Instance);

        var redirectDrives = descriptor.Settings.Fields.Should().ContainSingle(f => f.Key == "redirectDrives").Subject;
        redirectDrives.Type.Should().Be(SettingsFieldType.Boolean);
        redirectDrives.Default!.Value.GetBoolean().Should().BeFalse("the control's own RedirectDrives defaults to false");

        var audioMode = descriptor.Settings.Fields.Should().ContainSingle(f => f.Key == "audioMode").Subject;
        audioMode.Type.Should().Be(SettingsFieldType.Enum);
        audioMode.Default!.Value.GetString().Should().Be("Local", "the control's own AudioRedirectionMode defaults to 0 (play on this computer)");

        var mic = descriptor.Settings.Fields.Should().ContainSingle(f => f.Key == "redirectMicrophone").Subject;
        mic.Type.Should().Be(SettingsFieldType.Boolean);
        mic.Default!.Value.GetBoolean().Should().BeFalse("the control's own AudioCaptureRedirectionMode defaults to false");

        // Unlike the fields above, this default is a reasonable assumption, not a live-confirmed
        // control default — RedirectDirectX's getter unconditionally throws NotImplementedException on
        // this control version (confirmed via a live standalone probe), so its actual default state
        // can never be read back to confirm against, only set. See RdpSession.ConnectCore's own comment.
        var directX = descriptor.Settings.Fields.Should().ContainSingle(f => f.Key == "redirectDirectX").Subject;
        directX.Type.Should().Be(SettingsFieldType.Boolean);
        directX.Default!.Value.GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("Local", 0u)]
    [InlineData("Remote", 1u)]
    [InlineData("Disabled", 2u)]
    [InlineData(null, 0u)]
    [InlineData("garbage", 0u)]
    public void AudioModeToValue_MapsToTheControlsDocumentedValues(string? audioMode, uint expected) =>
        RdpSession.AudioModeToValue(audioMode).Should().Be(expected);

    [Fact]
    public void GetBoolSetting_ReadsTrueAndFalse_AndDefaultsFalseWhenMissingOrWrongType()
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["redirectDrives"]     = JsonSerializer.SerializeToElement(true),
            ["redirectMicrophone"] = JsonSerializer.SerializeToElement(false),
            ["notABool"]           = JsonSerializer.SerializeToElement("nope"),
        };

        RdpSession.GetBoolSetting(settings, "redirectDrives").Should().BeTrue();
        RdpSession.GetBoolSetting(settings, "redirectMicrophone").Should().BeFalse();
        RdpSession.GetBoolSetting(settings, "notABool").Should().BeFalse();
        RdpSession.GetBoolSetting(settings, "missing").Should().BeFalse();
    }

    [Fact]
    public void ParseRedirectedDriveLetters_ReadsTheConfiguredDriveNames()
    {
        string[] driveNames = ["C:\\", "D:\\"];
        var settings = new Dictionary<string, JsonElement>
        {
            ["redirectedDrives"] = JsonSerializer.SerializeToElement(driveNames),
        };

        RdpSession.ParseRedirectedDriveLetters(settings).Should().Equal("C:\\", "D:\\");
    }

    [Fact]
    public void ParseRedirectedDriveLetters_IsEmpty_WhenSettingIsMissing()
    {
        RdpSession.ParseRedirectedDriveLetters(new Dictionary<string, JsonElement>()).Should().BeEmpty();
    }

    [Fact]
    public void ParseRedirectedDriveLetters_IsEmpty_WhenSettingIsNotAnArray()
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["redirectedDrives"] = JsonSerializer.SerializeToElement("C:\\"),
        };

        RdpSession.ParseRedirectedDriveLetters(settings).Should().BeEmpty();
    }

    [Fact]
    public void ParseRedirectedDriveLetters_SkipsNonStringAndEmptyElements()
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["redirectedDrives"] = JsonSerializer.Deserialize<JsonElement>("""["C:\\", "", 5, "D:\\"]"""),
        };

        RdpSession.ParseRedirectedDriveLetters(settings).Should().Equal("C:\\", "D:\\");
    }
}
