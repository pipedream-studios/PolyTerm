using System.Globalization;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.RdpFreerdp.Tests;

/// <summary>
/// Real, end-to-end coverage of the FreeRDP-backed RDP module (issue #67) against a real Windows RDP
/// host — connect, receive real framebuffer data, send a benign input event, disconnect cleanly.
/// Needs a real, reachable RDP-enabled Windows host, not a resource CI or every dev machine has, so
/// this skips gracefully (not a failure) when the host/username/password environment variables below
/// aren't set, matching this repo's existing precedent for other real-infrastructure-dependent tests
/// (e.g. <c>SshAutoReconnectTests</c>/<c>SshX11InboundBridgeTests</c> in the SSH/FlaUI suites). None of
/// this test's real server details are committed to the repo - they're only ever set in this
/// developer's own local environment.
/// </summary>
public sealed class RdpFreerdpRealServerTests
{
    private static readonly string? TestHost = Environment.GetEnvironmentVariable("POLYTERM_TEST_RDP_HOST");
    private static readonly string? TestUsername = Environment.GetEnvironmentVariable("POLYTERM_TEST_RDP_USERNAME");
    private static readonly string? TestPassword = Environment.GetEnvironmentVariable("POLYTERM_TEST_RDP_PASSWORD");

    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    private static IProtocolSession CreateSessionOrSkip(CredentialMaterial credential,
        Dictionary<string, JsonElement>? settings = null)
    {
        var descriptor = new RdpFreerdpProtocolDescriptor(NullLoggerFactory.Instance);
        descriptor.IsAvailable(out var reason).Should().BeTrue(reason ?? "native shim should load in this test environment");

        var profile = new ConnectionProfile
        {
            Name = "rdp-freerdp-real-server-test",
            ProtocolId = "rdp",
            Host = TestHost,
            Settings = settings ?? [],
        };
        return descriptor.CreateSession(new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = profile,
            Credential = credential,
            HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
        });
    }

    private static CredentialMaterial CredentialOrSkip()
    {
        if (string.IsNullOrEmpty(TestHost) || string.IsNullOrEmpty(TestUsername) || string.IsNullOrEmpty(TestPassword))
            Assert.Skip("POLYTERM_TEST_RDP_HOST/POLYTERM_TEST_RDP_USERNAME/POLYTERM_TEST_RDP_PASSWORD not set - this test needs a real RDP-enabled Windows host. See this class's own doc comment.");

        return new CredentialMaterial { Username = TestUsername, Secret = Encoding.UTF8.GetBytes(TestPassword) };
    }

    /// <summary>
    /// Text both ways over cliprdr (issue #389): a PowerShell command typed into the host's Run box
    /// reads the host's clipboard (what this client offered) and replaces it with "ack:" plus that
    /// text, which has to come back through <see cref="IFramebufferChannel.ClipboardReceived"/>. Takes
    /// over the host's keyboard and clipboard, and needs the account to land on an unlocked desktop.
    /// </summary>
    [Fact]
    public async Task Clipboard_AgainstARealWindowsHost_CopiesTextBothWays()
    {
        using var credential = CredentialOrSkip();
        await using var session = CreateSessionOrSkip(credential);
        var ct = TestContext.Current.CancellationToken;

        var received = new List<string>();
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Unique to this run, so text left on the host's clipboard by an earlier one can't pass.
        var firstLine = $"héllo ✓ 😀 {Guid.NewGuid():N}";
        var sent = $"{firstLine}{Environment.NewLine}second line";
        var expected = "ack:" + sent;

        try
        {
            await session.ConnectAsync(ct);
            var channel = (RdpFreerdpFramebufferChannel)session.Framebuffer!;
            channel.ClipboardReceived += (_, payload) =>
            {
                lock (received) received.Add(payload.Text);
                if (payload.Text == expected) ack.TrySetResult();
            };

            // Straight after connecting, so usually before the channel is ready - the harder ordering.
            await channel.SendClipboardAsync(new ClipboardPayload { Text = sent }, ct);
            await Task.Delay(TimeSpan.FromSeconds(20), ct); // let the desktop finish logging on

            await RunOnHostAsync(channel,
                "powershell -NoProfile -WindowStyle Hidden -c \"Set-Clipboard ('ack:'+(Get-Clipboard -Raw))\"", ct);
            await ack.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            // Windows announces a copy more than once; give the repeat time to arrive.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            lock (received) received.Count(text => text == expected).Should().Be(1);
        }
        finally
        {
            await session.DisconnectAsync(ct);
        }
    }

    /// <summary>
    /// With "Share clipboard" off (issue #390): text is copied on the host, then a second connection
    /// offers its own. The host's clipboard, read back through a shared folder rather than cliprdr,
    /// must still hold the host's text - neither emptied by connecting nor replaced - and nothing may
    /// reach the client. Takes over the host's keyboard and clipboard, like the test above.
    /// </summary>
    [Fact]
    public async Task ClipboardSharingOff_AgainstARealWindowsHost_SharesNothingAndLeavesItsClipboardAlone()
    {
        using var credential = CredentialOrSkip();
        var ct = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("polyterm-rdp-test-");
        var probe = Path.Combine(folder.FullName, "clipboard.txt");
        var hostText = $"host-{Guid.NewGuid():N}";
        var settings = new Dictionary<string, JsonElement>
        {
            [RdpClipboard.ShareSettingKey] = JsonSerializer.SerializeToElement(false),
            [RdpDriveShares.DriveSharesKey] =
                JsonSerializer.SerializeToElement(new[] { new { name = "probe", path = folder.FullName } }),
        };
        var received = new List<string>();

        async Task OnANewConnectionAsync(Func<RdpFreerdpFramebufferChannel, Task> steps)
        {
            await using var session = CreateSessionOrSkip(credential, settings);
            try
            {
                await session.ConnectAsync(ct);
                var channel = (RdpFreerdpFramebufferChannel)session.Framebuffer!;
                channel.ClipboardReceived += (_, payload) =>
                {
                    lock (received) received.Add(payload.Text);
                };
                await channel.SendClipboardAsync(new ClipboardPayload { Text = "from the client" }, ct);
                await Task.Delay(TimeSpan.FromSeconds(20), ct); // let the desktop finish logging on
                await steps(channel);
            }
            finally
            {
                await session.DisconnectAsync(ct);
            }
        }

        try
        {
            await OnANewConnectionAsync(async channel =>
            {
                // Don't go on without proof that the command ran: otherwise whatever the host held before
                // looks like this test's doing.
                await RunUntilItWritesAsync(channel,
                    "powershell -NoProfile -WindowStyle Hidden -c " +
                    $@"""Set-Clipboard '{hostText}'; Set-Content \\tsclient\probe\set.txt done""",
                    Path.Combine(folder.FullName, "set.txt"), "done", ct);
            });

            await OnANewConnectionAsync(async channel =>
            {
                var onHost = await RunUntilItWritesAsync(channel,
                    "powershell -NoProfile -WindowStyle Hidden -c " +
                    @"""Set-Content \\tsclient\probe\clipboard.txt ('had['+(Get-Clipboard -Raw)+']')""",
                    probe, "]", ct);

                onHost.Should().Be($"had[{hostText}]");
            });

            lock (received) received.Should().BeEmpty();
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> on the host until <paramref name="path"/> in a shared folder ends
    /// with <paramref name="ending"/>, and returns the file's text. More than one go, because a
    /// reconnected session can still be showing the sign-in screen, where the typed keys go nowhere.
    /// </summary>
    private static async Task<string> RunUntilItWritesAsync(RdpFreerdpFramebufferChannel channel, string command,
        string path, string ending, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await RunOnHostAsync(channel, command, ct);
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(500, ct);
                if (!File.Exists(path)) continue;
                var text = (await File.ReadAllTextAsync(path, ct)).Trim();
                if (text.EndsWith(ending, StringComparison.Ordinal)) return text;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"Attempt {attempt}: the host didn't run the command.");
        }

        Assert.Fail("The host never ran the command: is its desktop unlocked?");
        return "";
    }

    /// <summary>
    /// Shortcuts sent as keys (issue #398): text typed into the host's Run box is selected with Ctrl+A
    /// and copied with Ctrl+C, so it only comes back over cliprdr if the host acted on both. Takes over
    /// the host's keyboard and clipboard, like the tests above.
    /// </summary>
    [Fact]
    public async Task CtrlShortcuts_AgainstARealWindowsHost_SelectAndCopyWhatWasTyped()
    {
        using var credential = CredentialOrSkip();
        await using var session = CreateSessionOrSkip(credential);
        var ct = TestContext.Current.CancellationToken;
        var typed = $"polyterm-{Guid.NewGuid():N}";
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await session.ConnectAsync(ct);
            var channel = (RdpFreerdpFramebufferChannel)session.Framebuffer!;
            channel.ClipboardReceived += (_, payload) =>
            {
                if (payload.Text == typed) copied.TrySetResult();
            };
            await Task.Delay(TimeSpan.FromSeconds(20), ct); // let the desktop finish logging on

            await ShortcutAsync(channel, "Super_L", "R", 'r', ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await TypeAsync(channel, typed, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await ShortcutAsync(channel, "Control_L", "A", 'a', ct);
            await ShortcutAsync(channel, "Control_L", "C", 'c', ct);

            try
            {
                await copied.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            finally
            {
                await PressAsync(channel, "Escape", ct); // close the Run box without running anything
            }
        }
        finally
        {
            await session.DisconnectAsync(ct);
        }
    }

    /// <summary>Win+R, then types <paramref name="command"/> and presses Enter.</summary>
    private static async Task RunOnHostAsync(RdpFreerdpFramebufferChannel channel, string command, CancellationToken ct)
    {
        await ShortcutAsync(channel, "Super_L", "R", 'r', ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await TypeAsync(channel, command, ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await PressAsync(channel, "Return", ct);
    }

    private static Task KeyAsync(RdpFreerdpFramebufferChannel channel, string symbol, KeyEventKind kind,
        CancellationToken ct, string? physicalKey = null) =>
        channel.SendKeyAsync(new KeyEvent { KeySymbol = symbol, PhysicalKey = physicalKey, Kind = kind }, ct).AsTask();

    private static async Task PressAsync(RdpFreerdpFramebufferChannel channel, string symbol, CancellationToken ct)
    {
        await KeyAsync(channel, symbol, KeyEventKind.Press, ct);
        await KeyAsync(channel, symbol, KeyEventKind.Release, ct);
    }

    /// <summary>A character key pressed while a modifier is held, as <c>FramebufferSurface</c> sends it.</summary>
    private static async Task ShortcutAsync(RdpFreerdpFramebufferChannel channel, string modifier, string physicalKey,
        char character, CancellationToken ct)
    {
        var symbol = ((int)character).ToString(CultureInfo.InvariantCulture);
        await KeyAsync(channel, modifier, KeyEventKind.Press, ct);
        await Task.Delay(100, ct);
        await KeyAsync(channel, symbol, KeyEventKind.Press, ct, physicalKey);
        await KeyAsync(channel, symbol, KeyEventKind.Release, ct, physicalKey);
        await Task.Delay(100, ct);
        await KeyAsync(channel, modifier, KeyEventKind.Release, ct);
    }

    private static async Task TypeAsync(RdpFreerdpFramebufferChannel channel, string text, CancellationToken ct)
    {
        foreach (var c in text)
        {
            await PressAsync(channel, ((int)c).ToString(CultureInfo.InvariantCulture), ct);
            await Task.Delay(15, ct);
        }
    }

    [Fact]
    public async Task ConnectAsync_AgainstARealWindowsHost_ReceivesFramebufferDataAndAcceptsInput()
    {
        using var credential = CredentialOrSkip();
        await using var session = CreateSessionOrSkip(credential);

        var regionUpdated = new TaskCompletionSource<FramebufferRegionUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRegionUpdated(object? sender, FramebufferRegionUpdatedEventArgs e) => regionUpdated.TrySetResult(e);

        try
        {
            await session.ConnectAsync(TestContext.Current.CancellationToken);
            session.State.Should().Be(SessionState.Connected);

            var channel = session.Framebuffer;
            channel.Should().NotBeNull("RDP is a Framebuffer-surface protocol");
            channel!.RegionUpdated += OnRegionUpdated;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (timeoutCts.Token.Register(() => regionUpdated.TrySetCanceled()))
            {
                // The very first RegionUpdated is just gdi_init's blank initial canvas (all-zero) -
                // fired before the server's first real bitmap update arrives. Assert structure on it,
                // but only assert real pixel content after giving the server a few seconds to actually
                // paint the desktop (confirmed against the real test host: a 350ms-total test run that
                // asserted on this first event alone saw nothing but zeros).
                var firstRegion = await regionUpdated.Task;
                firstRegion.Width.Should().BeGreaterThan(0);
                firstRegion.Height.Should().BeGreaterThan(0);
                firstRegion.Data.Length.Should().Be(firstRegion.Width * firstRegion.Height * 4);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var snapshot = channel.CaptureSnapshot();
            snapshot.Should().NotBeNull();
            snapshot!.Should().Contain(b => b != 0, "a real remote desktop frame should not be entirely black/transparent after several seconds connected");

            // Benign - just proves input injection round-trips through the shim without erroring.
            await channel.SendPointerAsync(new PointerEvent { Kind = PointerEventKind.Move, Button = PointerButton.None, X = 10, Y = 10 }, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (session.Framebuffer is { } ch) ch.RegionUpdated -= OnRegionUpdated;
            await session.DisconnectAsync(TestContext.Current.CancellationToken);
        }

        session.State.Should().Be(SessionState.Closed);
    }
}
