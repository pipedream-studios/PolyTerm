using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// When the local clipboard is and isn't shared (issue #391): only while the RDP view has focus in the
/// active window. Drives a live surface in a headless window, against the window's own clipboard.
/// </summary>
public sealed class FramebufferSurfaceClipboardTests
{
    /// <summary>Longer than the surface's timer interval.</summary>
    private static readonly TimeSpan PastOneCheck = TimeSpan.FromMilliseconds(900);

    private sealed record Shown(Window Window, FramebufferSurface Surface, ClipboardChannel Channel, Button Other)
    {
        public Task CopyLocallyAsync(string text) => Window.Clipboard!.SetTextAsync(text);

        /// <summary>Lets the surface's timer fire.</summary>
        public static async Task WaitAsync()
        {
            await Task.Delay(PastOneCheck, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<Shown> ShowAsync(string localText, bool withSync = true)
    {
        var channel = new ClipboardChannel();
        var surface = new FramebufferSurface { Width = 100, Height = 100, Channel = channel };
        if (withSync) surface.ClipboardSync = new ClipboardSync(channel);
        var other = new Button { Width = 100, Height = 20 };
        var panel = new StackPanel { Children = { other } };
        var window = new Window { Width = 100, Height = 120, Content = panel };
        window.Show();
        await window.Clipboard!.SetTextAsync(localText);
        panel.Children.Add(surface); // as a new session's tab does, which then focuses itself
        Dispatcher.UIThread.RunJobs();
        return new Shown(window, surface, channel, other);
    }

    [AvaloniaFact]
    public async Task ANewView_OffersTheLocalClipboardStraightAway()
    {
        var shown = await ShowAsync("copied before connecting");

        shown.Surface.IsFocused.Should().BeTrue();
        shown.Channel.Sent.Should().Equal("copied before connecting");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileFocused_ALaterCopyIsPickedUp()
    {
        var shown = await ShowAsync("first");

        await shown.CopyLocallyAsync("second");
        await Shown.WaitAsync();

        shown.Channel.Sent.Should().Equal("first", "second");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutFocus_NothingIsSent_UntilFocusReturns()
    {
        var shown = await ShowAsync("first");
        shown.Other.Focus();

        await shown.CopyLocallyAsync("copied elsewhere in the app");
        await Shown.WaitAsync();
        shown.Channel.Sent.Should().Equal("first");

        shown.Surface.Focus();
        Dispatcher.UIThread.RunJobs();

        shown.Channel.Sent.Should().Equal("first", "copied elsewhere in the app");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task InAnInactiveWindow_NothingIsSent_UntilItIsActivatedAgain()
    {
        var shown = await ShowAsync("first");
        Raise(shown.Window, "Deactivated");

        await shown.CopyLocallyAsync("copied in another program");
        await Shown.WaitAsync();
        shown.Surface.IsFocused.Should().BeTrue("the view keeps focus inside an inactive window");
        shown.Channel.Sent.Should().Equal("first");

        Raise(shown.Window, "Activated");
        Dispatcher.UIThread.RunJobs();

        shown.Channel.Sent.Should().Equal("first", "copied in another program");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task AViewTakenOffScreen_StopsSending()
    {
        var shown = await ShowAsync("first");

        shown.Window.Content = null;
        Dispatcher.UIThread.RunJobs();
        await shown.CopyLocallyAsync("copied after switching tabs");
        await Shown.WaitAsync();
        Raise(shown.Window, "Activated");
        Dispatcher.UIThread.RunJobs();

        shown.Channel.Sent.Should().Equal("first");
        shown.Window.Close();
    }

    /// <summary>As when switching back to a tab whose view is reused (see issue #364).</summary>
    [AvaloniaFact]
    public async Task AViewPutBackOnScreen_CarriesOnSending()
    {
        var shown = await ShowAsync("first");
        var content = shown.Window.Content;
        shown.Window.Content = null;
        Dispatcher.UIThread.RunJobs();
        await shown.CopyLocallyAsync("second");

        shown.Window.Content = content;
        Dispatcher.UIThread.RunJobs();

        shown.Channel.Sent.Should().Equal("first", "second");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task ASyncSetAfterTheViewHasFocus_SendsAtOnce()
    {
        var shown = await ShowAsync("already copied", withSync: false);
        shown.Channel.Sent.Should().BeEmpty();

        shown.Surface.ClipboardSync = new ClipboardSync(shown.Channel);
        Dispatcher.UIThread.RunJobs();

        shown.Channel.Sent.Should().Equal("already copied");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task TextCopiedOnTheRemote_ReachesTheWindowsClipboard_EvenWithTheViewOffScreen()
    {
        var shown = await ShowAsync("first");
        shown.Window.Content = null;
        Dispatcher.UIThread.RunJobs();

        shown.Channel.CopyOnRemote("from the remote");
        Dispatcher.UIThread.RunJobs();

        (await shown.Window.Clipboard!.TryGetTextAsync()).Should().Be("from the remote");
        shown.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATabHasClipboardSync_OnlyIfItsChannelSharesTheClipboard(bool shares)
    {
        var channel = new ClipboardChannel { SharesClipboard = shares };
        var session = new Mock<IProtocolSession>();
        session.Setup(s => s.Profile).Returns(new ConnectionProfile { Name = "win", ProtocolId = "rdp" });
        session.Setup(s => s.Framebuffer).Returns(channel);

        var tab = new SessionTabViewModel(session.Object);

        (tab.ClipboardSync is not null).Should().Be(shares);
        channel.HasClipboardListener.Should().Be(shares);

        tab.Dispose(); // closing or detaching the tab

        channel.HasClipboardListener.Should().BeFalse();
    }

    // Headless windows don't activate or deactivate on demand, so raise the platform's callback directly.
    // Avalonia doesn't let code outside it use these properties, hence the reflection.
    private static void Raise(Window window, string callback)
    {
        var platform = window.PlatformImpl!;
        var action = (Action?)platform.GetType().GetProperty(callback)!.GetValue(platform);
        action!.Invoke();
        Dispatcher.UIThread.RunJobs();
    }
}
