using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PolyTerm.Cli.Commands;

/// <summary>
/// Attaches to a session already open in a <em>running PolyTerm.App instance</em> — including one it
/// detached but kept alive (issue #77/#93 "On Close = Detach") — over the same named pipe
/// <c>McpBridgeService</c> exposes to PolyTerm.Mcp (issue #114/#116). Unlike <see cref="ConnectCommand"/>/
/// <see cref="ExecCommand"/>, this does not open its own in-process session at all: PolyTerm.App owns
/// the real connection, and this command is a thin remote terminal into it, so it needs no
/// <see cref="IServiceProvider"/> — nothing here talks to <c>ISessionManager</c> directly.
///
/// Also covers RDP/VNC (framebuffer) sessions (issue #119, following up on #115's server-side-only
/// plumbing): a terminal can't render a live desktop, so "attach" means something different there —
/// periodic PNG snapshots to a directory (<see cref="AttachToFramebufferSessionAsync"/>) plus one-shot
/// <c>--click</c>/<c>--key</c>/<c>--type</c>/<c>--resize</c> flags for scripted control
/// (<see cref="RunFramebufferOneShotActionsAsync"/>), rather than the live rendering a real remote-
/// desktop client would offer (see #119's own description for why that's out of scope here).
/// </summary>
internal static class AttachCommand
{
    public static Command Build()
    {
        var sessionIdArg = new Argument<string?>("session-id")
        {
            Description = "Session ID to attach to (see --list)",
            Arity       = ArgumentArity.ZeroOrOne,
        };
        var listOpt = new Option<bool>("--list")
        {
            Description = "List sessions available to attach to in the running PolyTerm.App, instead of attaching",
        };
        var snapshotDirOpt = new Option<string?>("--snapshot-dir")
        {
            Description = "Framebuffer (RDP/VNC) sessions only: directory to save numbered PNG snapshots to while attached, polling on change",
        };
        var intervalOpt = new Option<int>("--interval-ms")
        {
            Description = "Framebuffer sessions only: poll interval in milliseconds for --snapshot-dir (default 1000)",
            DefaultValueFactory = _ => 1000,
        };
        var clickOpt = new Option<string?>("--click")
        {
            Description = "Framebuffer sessions only: one-shot click at \"x,y\" (see --button), then detach",
        };
        var buttonOpt = new Option<string>("--button")
        {
            Description = "Mouse button for --click: left, right, or middle (default left)",
            DefaultValueFactory = _ => "left",
        };
        var keyOpt = new Option<string?>("--key")
        {
            Description = "Framebuffer sessions only: one-shot named key press, e.g. Return, Escape, Tab, BackSpace, Delete, Up/Down/Left/Right, Home, End, F1-F12",
        };
        var typeOpt = new Option<string?>("--type")
        {
            Description = "Framebuffer sessions only: one-shot literal text to type",
        };
        var resizeOpt = new Option<string?>("--resize")
        {
            Description = "Framebuffer sessions only: one-shot resize of the remote desktop, as \"width x height\" (e.g. 1920x1080)",
        };

        var cmd = new Command("attach", "Attach to a session already open in a running PolyTerm.App instance")
        {
            sessionIdArg, listOpt, snapshotDirOpt, intervalOpt, clickOpt, buttonOpt, keyOpt, typeOpt, resizeOpt,
        };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var sessionId = parseResult.GetValue(sessionIdArg);
            var list      = parseResult.GetValue(listOpt);
            var pipeName  = Environment.GetEnvironmentVariable("POLYTERM_PIPE") ?? "polyterm";

            using var client = new AttachPipeClient(pipeName);
            try
            {
                await client.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cannot connect to PolyTerm on pipe '{pipeName}': {ex.Message}");
                Console.Error.WriteLine("Make sure PolyTerm.App is running.");
                return 1;
            }

            if (list || sessionId is null)
                return await ListSessionsAsync(client, cancellationToken).ConfigureAwait(false);

            var framebufferOptions = new FramebufferAttachOptions(
                SnapshotDir: parseResult.GetValue(snapshotDirOpt),
                IntervalMs:  parseResult.GetValue(intervalOpt),
                Click:       parseResult.GetValue(clickOpt),
                Button:      parseResult.GetValue(buttonOpt) ?? "left",
                Key:         parseResult.GetValue(keyOpt),
                Type:        parseResult.GetValue(typeOpt),
                Resize:      parseResult.GetValue(resizeOpt));

            return await AttachToSessionAsync(client, sessionId, framebufferOptions, cancellationToken).ConfigureAwait(false);
        });

        return cmd;
    }

    /// <summary>Framebuffer-only attach flags, bundled since only <see cref="AttachToFramebufferSessionAsync"/>/<see cref="RunFramebufferOneShotActionsAsync"/> consume them — a terminal-kind session ignores all of these.</summary>
    private sealed record FramebufferAttachOptions(string? SnapshotDir, int IntervalMs, string? Click, string Button, string? Key, string? Type, string? Resize)
    {
        public bool HasOneShotAction => Click is not null || Key is not null || Type is not null || Resize is not null;
    }

    private static async Task<int> ListSessionsAsync(AttachPipeClient client, CancellationToken ct)
    {
        var result   = await client.CallAsync("session_list", null, ct).ConfigureAwait(false);
        var sessions = result.GetProperty("sessions");

        if (sessions.GetArrayLength() == 0)
        {
            Console.WriteLine("No sessions open in PolyTerm.App.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"Protocol",-12} {"State",-14} Name");
        Console.WriteLine(new string('-', 90));
        foreach (var s in sessions.EnumerateArray())
            Console.WriteLine($"{s.GetProperty("id").GetString(),-38} {s.GetProperty("protocol").GetString(),-12} {s.GetProperty("state").GetString(),-14} {s.GetProperty("name").GetString()}");
        return 0;
    }

    private static async Task<int> AttachToSessionAsync(AttachPipeClient client, string sessionId, FramebufferAttachOptions fbOptions, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        JsonElement started;
        try
        {
            started = await client.CallAsync("attach_start", new { session_id = sessionId }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot attach: {ex.Message}");
            return 1;
        }

        var kind = started.GetProperty("kind").GetString();
        return kind == "framebuffer"
            ? await AttachToFramebufferSessionAsync(client, sessionId, started, fbOptions, cts).ConfigureAwait(false)
            : await AttachToTerminalSessionAsync(client, sessionId, started, cts).ConfigureAwait(false);
    }

    private static async Task<int> AttachToTerminalSessionAsync(AttachPipeClient client, string sessionId, JsonElement started, CancellationTokenSource cts)
    {
        Console.Error.WriteLine($"[polyterm attach] Attached to {sessionId} — Ctrl+C to detach.");
        var backlog = started.GetProperty("text").GetString();
        if (!string.IsNullOrEmpty(backlog))
            Console.WriteLine(backlog);
        var since = started.GetProperty("total_lines").GetInt64();

        try
        {
            // Stdin → attach_send_input, forwarded by a background loop while the poll loop below
            // tails new output — same split responsibility as ConnectCommand's stdin task, just over
            // the pipe instead of directly against a local ITerminalChannel. Deliberately not awaited
            // after the poll loop below returns: like ConnectCommand's stdinTask, a blocking console
            // read may never observe cancellation, so exiting the process without joining it is the
            // same accepted tradeoff made there.
            _ = Task.Run(async () =>
            {
                var buf = new byte[256];
                while (!cts.IsCancellationRequested)
                {
                    var read = await Console.OpenStandardInput().ReadAsync(buf, cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    var text = Encoding.UTF8.GetString(buf, 0, read);
                    await client.CallAsync("attach_send_input", new { session_id = sessionId, data = text }, cts.Token).ConfigureAwait(false);
                }
            }, cts.Token);

            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
                var chunk = await client.CallAsync("attach_read", new { session_id = sessionId, since }, cts.Token).ConfigureAwait(false);
                var text  = chunk.GetProperty("text").GetString();
                if (!string.IsNullOrEmpty(text))
                    Console.WriteLine(text);
                since = chunk.GetProperty("total_lines").GetInt64();
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — fall through to attach_stop below.
        }
        finally
        {
            try { await client.CallAsync("attach_stop", new { session_id = sessionId }, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort release — the bridge also clears this on SessionClosed */ }
        }

        return 0;
    }

    /// <summary>
    /// Framebuffer counterpart to <see cref="AttachToTerminalSessionAsync"/> (issue #119). A terminal
    /// can't render a live desktop, so this offers two modes instead of a live view: one-shot
    /// input/resize flags (<see cref="RunFramebufferOneShotActionsAsync"/>), and/or continuous
    /// snapshot-to-disk polling via <c>--snapshot-dir</c> (<see cref="PollSnapshotsAsync"/>). Both can
    /// be combined in one invocation — one-shot actions run first, then a fresh snapshot is saved so
    /// the caller can see the result — matching how a scripted "click, then check what happened" flow
    /// would actually be used.
    /// </summary>
    private static async Task<int> AttachToFramebufferSessionAsync(AttachPipeClient client, string sessionId, JsonElement started, FramebufferAttachOptions options, CancellationTokenSource cts)
    {
        try
        {
            if (options.HasOneShotAction)
            {
                var actionResult = await RunFramebufferOneShotActionsAsync(client, sessionId, options, cts.Token).ConfigureAwait(false);
                if (actionResult != 0)
                    return actionResult;

                if (options.SnapshotDir is null)
                    return 0;

                // Fetch a fresh frame (not the possibly-stale one from attach_start) so the saved
                // snapshot reflects the action(s) just performed, then fall through to a single save
                // rather than the continuous loop below — a one-shot flag means "do this and detach",
                // not "start monitoring".
                var fresh = await client.CallAsync("attach_framebuffer_read", new { session_id = sessionId, since_version = 0 }, cts.Token).ConfigureAwait(false);
                SaveSnapshotAndReport(options.SnapshotDir, fresh);
                return 0;
            }

            if (options.SnapshotDir is null)
            {
                Console.Error.WriteLine($"[polyterm attach] '{sessionId}' is a graphical (RDP/VNC) session — a terminal can't render it live.");
                Console.Error.WriteLine("Use --snapshot-dir <path> to save periodic PNG snapshots while attached, or drive it directly with one-shot flags:");
                Console.Error.WriteLine("  --click x,y [--button left|right|middle]   --key <NamedKey>   --type \"text\"   --resize widthxheight");
                return 1;
            }

            return await PollSnapshotsAsync(client, sessionId, started, options, cts).ConfigureAwait(false);
        }
        finally
        {
            try { await client.CallAsync("attach_stop", new { session_id = sessionId }, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort release — the bridge also clears this on SessionClosed */ }
        }
    }

    /// <summary>Continuous <c>--snapshot-dir</c> polling loop: saves the frame <see cref="AttachToFramebufferSessionAsync"/>'s <c>attach_start</c> already fetched, then only writes a new one when <c>attach_framebuffer_read</c> reports a version change — mirroring the terminal path's incremental-tail polling, just for frames instead of lines.</summary>
    private static async Task<int> PollSnapshotsAsync(AttachPipeClient client, string sessionId, JsonElement started, FramebufferAttachOptions options, CancellationTokenSource cts)
    {
        var width  = started.GetProperty("width").GetInt32();
        var height = started.GetProperty("height").GetInt32();
        Console.Error.WriteLine($"[polyterm attach] Attached to {sessionId} (framebuffer, {width}x{height}) — saving snapshots to '{options.SnapshotDir}' every {options.IntervalMs}ms. Ctrl+C to detach.");

        var since = SaveSnapshotAndReport(options.SnapshotDir!, started);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(options.IntervalMs, cts.Token).ConfigureAwait(false);
                var chunk = await client.CallAsync("attach_framebuffer_read", new { session_id = sessionId, since_version = since }, cts.Token).ConfigureAwait(false);

                if (chunk.TryGetProperty("changed", out var changed) && !changed.GetBoolean())
                {
                    since = chunk.GetProperty("version").GetInt64();
                    continue;
                }

                since = SaveSnapshotAndReport(options.SnapshotDir!, chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — handled by the caller's finally (attach_stop).
        }

        return 0;
    }

    /// <summary>Decodes a <c>{ image_base64, width, height, version }</c> frame response and writes it as <c>frame-&lt;version&gt;.png</c>, returning the version so callers can track their polling cursor.</summary>
    private static long SaveSnapshotAndReport(string dir, JsonElement frame)
    {
        Directory.CreateDirectory(dir);

        var version = frame.GetProperty("version").GetInt64();
        var width   = frame.GetProperty("width").GetInt32();
        var height  = frame.GetProperty("height").GetInt32();
        var bytes   = Convert.FromBase64String(frame.GetProperty("image_base64").GetString()!);

        var path = Path.Combine(dir, $"frame-{version:D8}.png");
        File.WriteAllBytes(path, bytes);
        Console.Error.WriteLine($"[polyterm attach] Saved {width}x{height} frame v{version} -> {path}");

        return version;
    }

    /// <summary>
    /// Runs whichever one-shot flags were provided, in resize → click → key → type order (resize
    /// first so subsequent coordinates are against the final size; click before typing so a target
    /// field is focused first) — a click/key is sent as an ungated press immediately followed by a
    /// release, mirroring the gated <c>framebuffer_click</c>/<c>framebuffer_key</c> MCP tools' own
    /// implicit pairing, just via two <c>attach_framebuffer_send_pointer</c>/<c>send_key</c> calls
    /// instead of one server-side method (issue #119 - #115's server surface already covers this with
    /// no further server-side work needed). <c>--type</c> reuses the exact numeric-keysym conversion
    /// <c>FramebufferTypeAsync</c> uses server-side for VNC (X11 Unicode keysym convention: the code
    /// point directly for Latin-1, 0x01000000 + code point beyond it) — works unmodified against RDP's
    /// <c>RdpFreerdpFramebufferChannel.SendKeyAsync</c> too, which parses the same numeric convention.
    /// </summary>
    private static async Task<int> RunFramebufferOneShotActionsAsync(AttachPipeClient client, string sessionId, FramebufferAttachOptions options, CancellationToken ct)
    {
        try
        {
            if (options.Resize is not null)
            {
                if (!TryParseWidthHeight(options.Resize, out var width, out var height))
                {
                    Console.Error.WriteLine($"--resize expects \"widthxheight\" (e.g. 1920x1080), got '{options.Resize}'.");
                    return 1;
                }
                await client.CallAsync("attach_framebuffer_resize", new { session_id = sessionId, width, height }, ct).ConfigureAwait(false);
                Console.Error.WriteLine($"[polyterm attach] Resized to {width}x{height}.");
            }

            if (options.Click is not null)
            {
                if (!TryParseXy(options.Click, out var x, out var y))
                {
                    Console.Error.WriteLine($"--click expects \"x,y\", got '{options.Click}'.");
                    return 1;
                }
                await client.CallAsync("attach_framebuffer_send_pointer", new { session_id = sessionId, x, y, button = options.Button, kind = "press" }, ct).ConfigureAwait(false);
                await client.CallAsync("attach_framebuffer_send_pointer", new { session_id = sessionId, x, y, button = options.Button, kind = "release" }, ct).ConfigureAwait(false);
                Console.Error.WriteLine($"[polyterm attach] Clicked ({options.Button}) at ({x}, {y}).");
            }

            if (options.Key is not null)
            {
                await client.CallAsync("attach_framebuffer_send_key", new { session_id = sessionId, keysym = options.Key, kind = "press" }, ct).ConfigureAwait(false);
                await client.CallAsync("attach_framebuffer_send_key", new { session_id = sessionId, keysym = options.Key, kind = "release" }, ct).ConfigureAwait(false);
                Console.Error.WriteLine($"[polyterm attach] Pressed {options.Key}.");
            }

            if (options.Type is not null)
            {
                foreach (var c in options.Type)
                {
                    var symbol = ((int)(c <= 0xFF ? c : 0x01000000 + c)).ToString(CultureInfo.InvariantCulture);
                    await client.CallAsync("attach_framebuffer_send_key", new { session_id = sessionId, keysym = symbol, kind = "press" }, ct).ConfigureAwait(false);
                    await client.CallAsync("attach_framebuffer_send_key", new { session_id = sessionId, keysym = symbol, kind = "release" }, ct).ConfigureAwait(false);
                }
                Console.Error.WriteLine($"[polyterm attach] Typed {options.Type.Length} character(s).");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Action failed: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static bool TryParseXy(string s, out int x, out int y)
    {
        x = y = 0;
        var parts = s.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    private static bool TryParseWidthHeight(string s, out int width, out int height)
    {
        width = height = 0;
        var parts = s.Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
    }
}
