using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.App.Views;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers getting an error message out of the app (issue #345): the connection error banner's selectable text
/// and Copy button, which used to be a plain TextBlock that had to be retyped by hand.
/// </summary>
public sealed class ConnectionErrorCopyTests
{
    private const string Error = "freerdp_connect failed: The connection transport layer failed. (0x0002000D)";

    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private static MainViewModel MakeViewModel()
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        var sessions = new Mock<ISessionManager>();
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);
        var tree = new ConnectionTreeViewModel(store.Object, sessions.Object, registry.Object, new FakeComPortWatcher());
        return new MainViewModel(sessions.Object, tree);
    }

    private static (Window Window, ConnectionErrorBanner Banner, MainViewModel Vm) ShowBanner()
    {
        var vm = MakeViewModel();
        vm.ConnectionError = Error;
        var banner = new ConnectionErrorBanner { DataContext = vm };
        var window = new Window { Width = 900, Height = 200, Content = banner };
        window.Show();
        window.UpdateLayout();
        return (window, banner, vm);
    }

    [AvaloniaFact]
    public void Banner_ShowsTheError_AsSelectableText()
    {
        var (window, banner, _) = ShowBanner();

        var message = banner.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
        message.Text.Should().Be(Error);
        message.SelectAll();
        message.SelectedText.Should().Be(Error);

        window.Close();
    }

    [AvaloniaFact]
    public async Task CopyButton_PutsTheWholeError_OnTheClipboard_AndSaysSo()
    {
        var (window, banner, vm) = ShowBanner();

        banner.FindControl<Button>("CopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        (await window.Clipboard!.TryGetTextAsync()).Should().Be(Error);
        vm.StatusNotice.Should().Be($"Copied {Error.Length} characters to clipboard");
        vm.ConnectionError.Should().Be(Error, "copying shouldn't dismiss the banner");

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NothingToCopy_LeavesTheClipboardAlone(string? text)
    {
        var (window, banner, vm) = ShowBanner();
        await window.Clipboard!.SetTextAsync("untouched");

        await ErrorClipboard.CopyAsync(banner, vm, text);

        (await window.Clipboard!.TryGetTextAsync()).Should().Be("untouched");
        vm.HasStatusNotice.Should().BeFalse();

        window.Close();
    }
}
