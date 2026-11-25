using System.Text.Json;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// The RDP "Shared Folders" editor (issue #270): named folder shares saved as <c>driveShares</c>, replacing
/// the drive-letter checklist, with old <c>redirectedDrives</c> profiles migrated when they're opened.
/// </summary>
public sealed class NewConnectionViewModelDriveShareTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private static (NewConnectionViewModel Vm, Func<ConnectionProfile?> Saved) MakeEditor(ConnectionProfile? existing = null)
    {
        ConnectionProfile? saved = null;
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        store.Setup(s => s.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .Callback<ConnectionProfile, CancellationToken>((p, _) => saved = p)
            .Returns<ConnectionProfile, CancellationToken>((p, _) => Task.FromResult(p));

        string? reason = null;
        var rdp = new Mock<IProtocolDescriptor>();
        rdp.Setup(d => d.Id).Returns("rdp");
        rdp.Setup(d => d.DisplayName).Returns("RDP");
        rdp.Setup(d => d.DefaultPort).Returns(3389);
        rdp.Setup(d => d.IsAvailable(out reason)).Returns(true);
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.All).Returns([rdp.Object]);
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, new Mock<ISessionManager>().Object, registry.Object, new FakeComPortWatcher());
        var vm = new NewConnectionViewModel(store.Object, tree, registry.Object, existing,
            initialProtocolId: "rdp", credentialProviders: []);
        vm.Name = "win";
        vm.Host = "win.example.com";
        return (vm, () => saved);
    }

    /// <summary>An existing RDP profile whose settings are the given JSON object, as stored on disk.</summary>
    private static ConnectionProfile RdpProfile(string settingsJson) => new()
    {
        Name = "win", Host = "win.example.com", ProtocolId = "rdp",
        Settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson)!,
    };

    private static IEnumerable<(string Name, string Path)> Shares(NewConnectionViewModel vm) =>
        vm.RdpDriveShares.Select(s => (s.Name, s.Path));

    [Theory]
    [InlineData(@"C:\", "C")]
    [InlineData(@"d:", "D")]
    [InlineData("/", "root")]
    [InlineData("/Users/rob/Projects/", "Projects")]
    [InlineData(@"C:\Users\rob\Projects", "Projects")]
    public void ShareNameFromPath_UsesTheDriveLetterOrFolderName(string path, string expected) =>
        NewConnectionViewModel.ShareNameFromPath(path).Should().Be(expected);

    [AvaloniaFact]
    public async Task NoSharesAreListedByDefault_AndNothingIsSaved()
    {
        var (vm, saved) = MakeEditor();

        vm.RdpDriveShares.Should().BeEmpty("the dialog no longer lists every mount point on the machine");
        vm.HasNoRdpDriveShares.Should().BeTrue();

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings.Should().NotContainKey("driveShares");
        saved()!.Settings.Should().NotContainKey("redirectDrives");
        saved()!.Settings.Should().NotContainKey("redirectedDrives");
    }

    [AvaloniaFact]
    public async Task AddFolder_UsesThePickedFolder_NamedAfterIt_AndSavesAsDriveShares()
    {
        var (vm, saved) = MakeEditor();
        vm.PickFolderAsync = () => Task.FromResult<string?>("/Users/rob/Projects");

        await vm.AddRdpDriveShareCommand.ExecuteAsync(null);
        vm.RdpDriveShares.Single().Name = "Code";
        await vm.SaveCommand.ExecuteAsync(null);

        Shares(vm).Should().Equal(("Code", "/Users/rob/Projects"));
        var json = saved()!.Settings["driveShares"];
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("name").GetString().Should().Be("Code");
        json[0].GetProperty("path").GetString().Should().Be("/Users/rob/Projects");
    }

    [AvaloniaFact]
    public async Task AddFolder_Cancelled_AddsNothing()
    {
        var (vm, _) = MakeEditor();
        vm.PickFolderAsync = () => Task.FromResult<string?>(null);

        await vm.AddRdpDriveShareCommand.ExecuteAsync(null);

        vm.RdpDriveShares.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void AddingTheSameFolderTwice_KeepsOne_AndAClashingNameGetsASuffix()
    {
        var (vm, _) = MakeEditor();

        vm.AddRdpDriveShare("/a/Data", "Data");
        vm.AddRdpDriveShare("/a/Data/", "Data");
        vm.AddRdpDriveShare("/b/Data", "Data");

        Shares(vm).Should().Equal(("Data", "/a/Data"), ("Data2", "/b/Data"));
    }

    [AvaloniaFact]
    public void ShareHomeFolder_AddsItOnce_ThenDisablesItself_UntilRemoved()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (vm, _) = MakeEditor();

        vm.ShareHomeFolderCommand.CanExecute(null).Should().BeTrue();
        vm.ShareHomeFolderCommand.Execute(null);

        Shares(vm).Should().Equal(("Home", home));
        vm.ShareHomeFolderCommand.CanExecute(null).Should().BeFalse();

        vm.RdpDriveShares.Single().RemoveCommand.Execute(null);

        vm.RdpDriveShares.Should().BeEmpty();
        vm.HasNoRdpDriveShares.Should().BeTrue();
        vm.ShareHomeFolderCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Save_SkipsRowsWithNoFolder_AndNamesUnnamedRowsAfterTheirFolder()
    {
        var (vm, saved) = MakeEditor();
        vm.AddRdpDriveShare("/srv/media", "media");
        vm.AddRdpDriveShare("/srv/empty", "empty");
        vm.RdpDriveShares[0].Name = "  ";
        vm.RdpDriveShares[1].Path = "";

        await vm.SaveCommand.ExecuteAsync(null);

        var json = saved()!.Settings["driveShares"];
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("name").GetString().Should().Be("media");
        json[0].GetProperty("path").GetString().Should().Be("/srv/media");
    }

    [AvaloniaFact]
    public async Task EditingAnOldProfile_MigratesRedirectedDrives_AndSavesOnlyTheNewShape()
    {
        var (vm, saved) = MakeEditor(RdpProfile(
            """{ "redirectDrives": true, "redirectedDrives": ["C:\\", "/Volumes/Data"] }"""));

        Shares(vm).Should().Equal(("C", @"C:\"), ("Data", "/Volumes/Data"));

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings.Should().ContainKey("driveShares");
        saved()!.Settings.Should().NotContainKey("redirectDrives");
        saved()!.Settings.Should().NotContainKey("redirectedDrives");
    }

    [AvaloniaFact]
    public void AnOldProfileWithRedirectionOff_LoadsNoShares()
    {
        var (vm, _) = MakeEditor(RdpProfile(
            """{ "redirectDrives": false, "redirectedDrives": ["C:\\"] }"""));

        vm.RdpDriveShares.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void DriveShares_WinOverTheOldList()
    {
        var (vm, _) = MakeEditor(RdpProfile(
            """{ "driveShares": [{ "name": "Proj", "path": "/Users/rob/Projects" }], "redirectedDrives": ["C:\\"] }"""));

        Shares(vm).Should().Equal(("Proj", "/Users/rob/Projects"));
    }
}
