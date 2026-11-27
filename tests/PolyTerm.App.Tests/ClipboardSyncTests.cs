using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Framebuffer;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>What <see cref="ClipboardSync"/> passes on in each direction (issue #391).</summary>
public sealed class ClipboardSyncTests
{
    private static (ClipboardSync Sync, ClipboardChannel Channel, FakeLocalClipboard Local) Make(string? localText = null)
    {
        var channel = new ClipboardChannel();
        var local = new FakeLocalClipboard { Text = localText };
        var sync = new ClipboardSync(channel);
        sync.UseClipboard(local);
        return (sync, channel, local);
    }

    private static void RunUi() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public async Task LocalText_IsSent_ThenOnlyAgainOnceItChanges()
    {
        var (sync, channel, local) = Make("first");

        await sync.SendLocalAsync();
        await sync.SendLocalAsync();
        local.Text = "second";
        await sync.SendLocalAsync();
        local.Text = "first";
        await sync.SendLocalAsync();

        channel.Sent.Should().Equal("first", "second", "first");
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AClipboardWithoutText_SendsNothing(string? text)
    {
        var (sync, channel, _) = Make(text);

        await sync.SendLocalAsync();

        channel.Sent.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task WithoutALocalClipboard_NothingHappens()
    {
        var channel = new ClipboardChannel();
        var sync = new ClipboardSync(channel);

        await sync.SendLocalAsync();
        channel.CopyOnRemote("remote");
        RunUi();

        channel.Sent.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task RemoteText_GoesOnTheLocalClipboard_AndIsNotSentBack()
    {
        var (sync, channel, local) = Make();

        channel.CopyOnRemote("from the remote");
        RunUi();
        await sync.SendLocalAsync();

        local.Written.Should().Equal("from the remote");
        channel.Sent.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task RemoteText_ArrivesFromAnotherThread()
    {
        var (_, channel, local) = Make();

        await Task.Run(() => channel.CopyOnRemote("from the remote"), TestContext.Current.CancellationToken);
        RunUi();

        local.Written.Should().Equal("from the remote");
    }

    [AvaloniaFact]
    public async Task BeforeRemoteTextReachesTheUiThread_TheOlderLocalTextIsNotSent()
    {
        var (sync, channel, local) = Make("old local text");

        channel.CopyOnRemote("newer, from the remote");
        await sync.SendLocalAsync(); // before the UI thread has run the write

        channel.Sent.Should().BeEmpty();
        local.Reads.Should().Be(0);
    }

    [AvaloniaFact]
    public async Task WhileRemoteTextIsBeingWritten_TheOlderLocalTextIsNotSent()
    {
        var (sync, channel, local) = Make("old local text");
        local.HoldWrites = new TaskCompletionSource();
        channel.CopyOnRemote("newer, from the remote");
        RunUi();

        await sync.SendLocalAsync();
        local.HoldWrites.SetResult();
        RunUi();
        await sync.SendLocalAsync();

        channel.Sent.Should().BeEmpty();
        local.Written.Should().Equal("newer, from the remote");
    }

    [AvaloniaFact]
    public async Task AReadThatRemoteTextOvertakes_IsThrownAway()
    {
        var (sync, channel, local) = Make("old local text");
        local.HoldReads = new TaskCompletionSource();
        var sending = sync.SendLocalAsync();

        channel.CopyOnRemote("newer, from the remote");
        RunUi(); // written while the read is still open
        local.HoldReads.SetResult();
        RunUi();
        await sending;

        channel.Sent.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task IfRemoteTextCantBeWritten_TheOlderLocalTextIsNotSentOverIt_ButTheNextCopyIs()
    {
        // Never read before, so only the failed write says this text is older than the remote's.
        var (sync, channel, local) = Make("old local text");
        local.FailWrites = true;
        channel.CopyOnRemote("newer, from the remote");
        RunUi();

        await sync.SendLocalAsync();
        await sync.SendLocalAsync();
        local.Text = "copied afterwards";
        await sync.SendLocalAsync();

        channel.Sent.Should().Equal("copied afterwards");
    }

    [AvaloniaFact]
    public void RemoteTexts_AreWrittenInTheOrderTheyArrive()
    {
        var (_, channel, local) = Make();
        var first = local.HoldWrites = new TaskCompletionSource();
        channel.CopyOnRemote("one");
        RunUi();
        local.HoldWrites = null; // only the first write is slow
        channel.CopyOnRemote("two");
        RunUi();

        first.SetResult();
        RunUi();

        local.Written.Should().Equal("one", "two");
    }

    [AvaloniaFact]
    public async Task AReadStillRunning_IsNotStartedAgain()
    {
        var (sync, channel, local) = Make("text");
        local.HoldReads = new TaskCompletionSource();

        var first = sync.SendLocalAsync();
        await sync.SendLocalAsync();
        local.HoldReads.SetResult();
        RunUi();
        await first;

        local.Reads.Should().Be(1);
        channel.Sent.Should().Equal("text");
    }

    [AvaloniaFact]
    public async Task AFailedRead_IsTriedAgainNextTime()
    {
        var (sync, channel, local) = Make("text");
        local.FailReads = true;
        await sync.SendLocalAsync();

        local.FailReads = false;
        await sync.SendLocalAsync();

        channel.Sent.Should().Equal("text");
    }

    [AvaloniaFact]
    public async Task OnceDisposed_NothingIsSentOrWritten()
    {
        var (sync, channel, local) = Make("text");
        channel.CopyOnRemote("arrived, not yet written");

        sync.Dispose();
        RunUi();
        await sync.SendLocalAsync();

        channel.HasClipboardListener.Should().BeFalse();
        channel.Sent.Should().BeEmpty();
        local.Written.Should().BeEmpty();
    }
}
