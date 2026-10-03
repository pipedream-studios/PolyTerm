using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;

namespace PolyTerm.App.McpBridge;

/// <summary>
/// Named-pipe JSON-RPC server that lets PolyTerm.Mcp.exe control terminal sessions.
/// Pipe name: <c>polyterm</c> (configurable via POLYTERM_PIPE env var at client side).
/// </summary>
internal sealed class McpBridgeService : IDisposable
{
    /// <summary>
    /// How many clients can be connected to the pipe at once. Named pipes require each concurrent
    /// server instance to be created upfront with a matching <c>maxNumberOfServerInstances</c> — this
    /// spawns that many independent accept/serve loops (see <see cref="Start"/>/<see cref="RunPipeServerAsync"/>),
    /// each of which creates a fresh <see cref="NamedPipeServerStream"/> as soon as its previous client
    /// disconnects, so up to this many clients can be connected simultaneously rather than the pipe
    /// only ever accepting one connection at a time.
    /// </summary>
    private const int MaxConcurrentClients = 4;

    private readonly string _pipeName;
    private readonly ISessionManager _sessions;
    private readonly IProfileStore _profiles;
    private readonly IApprovalGate _gate;
    private readonly ILogger<McpBridgeService> _logger;
    private readonly McpAuditLogger _audit;

    private readonly Dictionary<Guid, SessionEntry> _tracked = [];
    private readonly HashSet<Guid> _attached = [];
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    private sealed record SessionEntry(IProtocolSession Session, TerminalOutputBuffer Buffer, FramebufferVersionTracker FramebufferVersion)
    {
        /// <summary>
        /// The delegate subscribed to <c>Session.Framebuffer.RegionUpdated</c> in <see cref="OnSessionOpened"/>,
        /// kept here so <see cref="OnSessionClosed"/> can unsubscribe the exact same instance. Null for
        /// sessions with no framebuffer capability.
        /// </summary>
        public EventHandler<FramebufferRegionUpdatedEventArgs>? FramebufferRegionHandler { get; init; }
    }

    /// <summary>
    /// <paramref name="pipeName"/> defaults to "polyterm" (the real app's pipe) — overridable so
    /// <c>PolyTerm.App.Tests</c> can run a real instance on a private pipe name without conflicting
    /// with a live app. <paramref name="auditLogPath"/> similarly defaults to the real
    /// <c>mcp-audit.log</c> in the app's config directory but can be overridden so tests don't write
    /// to it.
    /// </summary>
    public McpBridgeService(ISessionManager sessions, IProfileStore profiles, IApprovalGate gate, ILogger<McpBridgeService> logger, string pipeName = "polyterm", string? auditLogPath = null)
    {
        _sessions = sessions;
        _profiles = profiles;
        _gate     = gate;
        _logger   = logger;
        _pipeName = pipeName;
        _audit    = new McpAuditLogger(auditLogPath);

        _sessions.SessionOpened += OnSessionOpened;
        _sessions.SessionClosed += OnSessionClosed;
    }

    /// <summary>Starts accepting pipe connections on <see cref="MaxConcurrentClients"/> background tasks, so that many clients can be connected at once.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        for (var i = 0; i < MaxConcurrentClients; i++)
            _ = Task.Run(() => RunPipeServerAsync(_cts.Token));
    }

    /// <summary>Signals the pipe server loop to stop accepting/serving connections.</summary>
    public void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Dispose();
        _sessions.SessionOpened -= OnSessionOpened;
        _sessions.SessionClosed -= OnSessionClosed;
    }

    // ── Session tracking ────────────────────────────────────────────────────

    /// <summary>
    /// Starts tracking a newly opened session: creates its output buffer, registers the buffer
    /// as an output sink on the session's terminal (if any), and subscribes to state changes so
    /// the sink can be re-registered across a reconnect (see <see cref="OnTrackedSessionStateChanged"/>).
    /// </summary>
    private void OnSessionOpened(object? sender, SessionEventArgs e)
    {
        var session   = e.Session;
        var buffer    = new TerminalOutputBuffer();
        var fbVersion = new FramebufferVersionTracker();

        // Only bumps a counter — no pixel data touched here. INativeHostChannel has no equivalent
        // change-notification event (unimplemented by any real protocol today — both VNC and RDP
        // implement IFramebufferChannel directly), so there's deliberately no native-host branch here.
        EventHandler<FramebufferRegionUpdatedEventArgs>? fbHandler = null;
        if (session.Framebuffer is { } fb)
        {
            fbHandler = (_, _) => fbVersion.Bump();
            fb.RegionUpdated += fbHandler;
        }

        var entry = new SessionEntry(session, buffer, fbVersion) { FramebufferRegionHandler = fbHandler };
        lock (_lock) { _tracked[session.SessionId] = entry; }

        if (session.Terminal is { } terminal)
            terminal.RegisterOutputSink(data => buffer.Append(data));

        session.StateChanged += OnTrackedSessionStateChanged;

        _logger.LogDebug("MCP bridge: tracking session {Id}", session.SessionId);
    }

    /// <summary>Stops tracking a closed session: unsubscribes from state changes and drops its buffer.</summary>
    private void OnSessionClosed(object? sender, SessionEventArgs e)
    {
        e.Session.StateChanged -= OnTrackedSessionStateChanged;

        SessionEntry? entry;
        lock (_lock)
        {
            _tracked.Remove(e.Session.SessionId, out entry);
            _attached.Remove(e.Session.SessionId);
        }

        if (entry?.FramebufferRegionHandler is not null && e.Session.Framebuffer is { } fb)
            fb.RegionUpdated -= entry.FramebufferRegionHandler;

        _logger.LogDebug("MCP bridge: dropped session {Id}", e.Session.SessionId);
    }

    // Auto-reconnect swaps in a new ITerminalChannel instance; re-subscribe the buffer
    // to it so recording doesn't silently go stale after the session recovers.
    /// <summary>
    /// Re-registers the session's output buffer as a sink on the session's (new) terminal channel
    /// when a session transitions Reconnecting → Connected. This is necessary because SSH
    /// auto-reconnect replaces <see cref="IProtocolSession.Terminal"/> with a brand-new
    /// <c>SshTerminalChannel</c> instance — the buffer's original sink registration was on the old,
    /// now-discarded channel, so without this it would silently stop receiving output after a
    /// successful reconnect even though the session itself recovers normally.
    /// </summary>
    private void OnTrackedSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        // No equivalent framebuffer branch here: unlike SSH's terminal channel, neither VncSession nor
        // RdpFreerdpSession currently declares ProtocolCapabilities.Reconnect, and each assigns its
        // IFramebufferChannel exactly once in ConnectAsync — so this Reconnecting→Connected transition
        // never fires for a framebuffer-capable session today. If a future protocol change gives
        // RDP/VNC reconnect-with-channel-swap semantics, this will need a branch that re-subscribes
        // RegionUpdated on the new channel, mirroring the terminal case below.
        if (e.Previous != SessionState.Reconnecting || e.Current != SessionState.Connected) return;
        if (sender is not IProtocolSession session) return;

        SessionEntry? entry;
        lock (_lock) { _tracked.TryGetValue(session.SessionId, out entry); }
        if (entry is null || session.Terminal is not { } terminal) return;

        terminal.RegisterOutputSink(data => entry.Buffer.Append(data));
    }

    /// <summary>Looks up a tracked session by its string session ID, or throws if it's not (or no longer) tracked.</summary>
    private SessionEntry GetEntry(string sessionId)
    {
        var id = Guid.Parse(sessionId);
        lock (_lock)
        {
            return _tracked.TryGetValue(id, out var entry)
                ? entry
                : throw new KeyNotFoundException(
                    $"Session '{sessionId}' not found. Use session_list to get current session IDs.");
        }
    }

    // ── Pipe server loop ────────────────────────────────────────────────────

    /// <summary>
    /// One of <see cref="MaxConcurrentClients"/> concurrent accept/serve loops: creates a fresh pipe
    /// instance, waits for a client, serves it fully via <see cref="HandleConnectionAsync"/>, then
    /// loops back to accept the next one — while the other loops (each with their own pipe instance)
    /// keep serving whichever clients are already connected.
    /// </summary>
    private async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    maxNumberOfServerInstances: MaxConcurrentClients,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                _logger.LogInformation("MCP bridge: listening on \\\\.\\ pipe\\{Name}", _pipeName);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("MCP bridge: client connected");

                await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP bridge: connection error");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    /// <summary>Reads newline-delimited JSON-RPC requests from the pipe and writes a response line for each, until the client disconnects or cancellation is requested.</summary>
    private async Task HandleConnectionAsync(PipeStream pipe, CancellationToken ct)
    {
        // UTF8Encoding(false) — no BOM. Encoding.UTF8 (the shared singleton) has a 3-byte preamble
        // that StreamWriter writes on its first Flush; over a named pipe with nothing reading yet,
        // that write blocks indefinitely (confirmed: the exact hang this bridge had — see docs/todos.md).
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(pipe, noBomUtf8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, noBomUtf8, leaveOpen: true) { AutoFlush = true };

        // One rate limiter per connection — a busy client only ever throttles itself, never another
        // one connected to a different pipe instance at the same time.
        var rateLimiter = new McpRateLimiter();

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (line is null) break; // client disconnected

            string response;
            try   { response = await HandleRequestAsync(line, rateLimiter, ct).ConfigureAwait(false); }
            catch (Exception ex) { response = BuildError(0, ex.Message); }

            try { await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("MCP bridge: client disconnected");
    }

    /// <summary>
    /// Dispatches a single JSON-RPC request by method name, rate-limits it (spec §4.7 rule 4),
    /// serializes the result (or an error response on failure or over-limit), and records a redacted
    /// audit entry either way (spec §4.7 rule 5).
    /// </summary>
    private async Task<string> HandleRequestAsync(string requestJson, McpRateLimiter rateLimiter, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(requestJson);
        var root   = doc.RootElement;
        var id     = root.GetProperty("id").GetInt32();
        var method = root.GetProperty("method").GetString()!;
        root.TryGetProperty("params", out var @params);

        var sessionId = TryGetSessionId(@params);
        var profile   = TryGetProfileName(sessionId);
        var detail    = RedactParamsForAudit(@params);

        if (!rateLimiter.TryAcquire(App.Settings.McpMaxCallsPerMinute))
        {
            var limitError = $"Rate limit exceeded ({App.Settings.McpMaxCallsPerMinute} calls/minute). Slow down and retry.";
            _audit.Log(method, sessionId, profile, detail, success: false, error: limitError);
            return BuildError(id, limitError);
        }

        try
        {
            var result = method switch
            {
                "session_list"       => SessionList(),
                "connection_list"    => await ConnectionListAsync(ct).ConfigureAwait(false),
                "connection_open"    => await ConnectionOpenAsync(@params, ct).ConfigureAwait(false),
                "connection_close"   => await ConnectionCloseAsync(@params, ct).ConfigureAwait(false),
                "terminal_read"      => TerminalRead(@params),
                "terminal_execute"   => await TerminalExecuteAsync(@params, ct).ConfigureAwait(false),
                "terminal_send_keys" => await TerminalSendKeysAsync(@params, ct).ConfigureAwait(false),
                "attach_start"       => await AttachStartAsync(@params, ct).ConfigureAwait(false),
                "attach_stop"        => AttachStop(@params),
                "attach_read"        => AttachRead(@params),
                "attach_send_input"  => await AttachSendInputAsync(@params, ct).ConfigureAwait(false),
                "attach_framebuffer_read"         => await AttachFramebufferReadAsync(@params, ct).ConfigureAwait(false),
                "attach_framebuffer_send_pointer" => await AttachFramebufferSendPointerAsync(@params, ct).ConfigureAwait(false),
                "attach_framebuffer_send_key"     => await AttachFramebufferSendKeyAsync(@params, ct).ConfigureAwait(false),
                "attach_framebuffer_resize"       => await AttachFramebufferResizeAsync(@params, ct).ConfigureAwait(false),
                "sftp_list"          => await SftpListAsync(@params, ct).ConfigureAwait(false),
                "sftp_read"          => await SftpReadAsync(@params, ct).ConfigureAwait(false),
                "sftp_write"         => await SftpWriteAsync(@params, ct).ConfigureAwait(false),
                "sftp_delete"        => await SftpDeleteAsync(@params, ct).ConfigureAwait(false),
                "sftp_rename"        => await SftpRenameAsync(@params, ct).ConfigureAwait(false),
                "framebuffer_screenshot" => await FramebufferScreenshotAsync(@params, ct).ConfigureAwait(false),
                "framebuffer_click"      => await FramebufferClickAsync(@params, ct).ConfigureAwait(false),
                "framebuffer_type"       => await FramebufferTypeAsync(@params, ct).ConfigureAwait(false),
                "framebuffer_key"        => await FramebufferKeyAsync(@params, ct).ConfigureAwait(false),
                "framebuffer_resize"     => await FramebufferResizeAsync(@params, ct).ConfigureAwait(false),
                "webview_navigate"       => await WebNavigateAsync(@params, ct).ConfigureAwait(false),
                "webview_get_url"        => WebGetUrl(@params),
                "webview_screenshot"     => await WebScreenshotAsync(@params, ct).ConfigureAwait(false),
                "webview_invoke_script"  => await WebInvokeScriptAsync(@params, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown method '{method}'."),
            };
            _audit.Log(method, sessionId, profile, detail, success: true);
            return JsonSerializer.Serialize(new { id, result });
        }
        catch (Exception ex)
        {
            _audit.Log(method, sessionId, profile, detail, success: false, error: ex.Message);
            return BuildError(id, ex.Message);
        }
    }

    /// <summary>
    /// Extracts the session ID param if present, for audit-log correlation and profile lookup. Accepts
    /// "id" as an alias for "session_id": some MCP proxy layers strip a literal "session_id" argument
    /// before it reaches the server, and "id" matches what session_list returns.
    /// </summary>
    private static string? TryGetSessionId(JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in SessionIdParamNames)
        {
            if (@params.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String)
                return s.GetString();
        }
        return null;
    }

    private static readonly string[] SessionIdParamNames = ["session_id", "id"];

    /// <summary>Reads the required session ID param ("session_id", or its alias "id"), with a clear error if neither is present.</summary>
    private static string GetSessionId(JsonElement @params) =>
        TryGetSessionId(@params)
        ?? throw new InvalidOperationException("Missing required parameter 'session_id' (alias: 'id').");

    /// <summary>Resolves a tracked session's profile name for the audit log, or null if the session id is missing/unknown (e.g. connection_open, which has no session yet at call time).</summary>
    private string? TryGetProfileName(string? sessionId)
    {
        if (sessionId is null || !Guid.TryParse(sessionId, out var id)) return null;
        lock (_lock) { return _tracked.TryGetValue(id, out var entry) ? entry.Session.Profile.Name : null; }
    }

    /// <summary>
    /// Renders <paramref name="params"/> as a flat <c>key=value</c> summary for the audit log, with
    /// bulk content-bearing fields ("content", "text" — i.e. sftp_write's payload) replaced by their
    /// length rather than logged verbatim. This is a narrow, deterministic redaction of specific
    /// known-bulk fields, not an attempt to pattern-match secrets inside arbitrary values (that
    /// approach was already judged unreliable for the session-recording feature — see docs/todos.md's
    /// Security section — and the same reasoning applies here).
    /// </summary>
    internal static string RedactParamsForAudit(JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Object) return "{}";

        var parts = @params.EnumerateObject().Select(prop =>
        {
            // "data" (attach_send_input's raw keystrokes) redacted the same way as "content"/"text" —
            // it can carry anything the attached human types, passwords included.
            if (prop.Name is "content" or "text" or "data" && prop.Value.ValueKind == JsonValueKind.String)
                return $"{prop.Name}=<{prop.Value.GetString()!.Length} chars redacted>";
            return $"{prop.Name}={prop.Value}";
        });

        return "{" + string.Join(", ", parts) + "}";
    }

    // ── Tool implementations ─────────────────────────────────────────────────

    private object SessionList()
    {
        IProtocolSession[] sessions;
        lock (_lock) { sessions = [.. _tracked.Values.Select(e => e.Session)]; }

        return new
        {
            sessions = sessions.Select(s => new
            {
                id       = s.SessionId.ToString(),
                name     = s.Profile.Name,
                protocol = s.Profile.ProtocolId,
                state    = s.State.ToString(),
                has_sftp = s.FileTransfer is not null,
            }).ToArray(),
        };
    }

    private async Task<object> ConnectionListAsync(CancellationToken ct)
    {
        var all = await _profiles.GetAllAsync(ct).ConfigureAwait(false);
        return new
        {
            connections = all.Where(p => !p.IsFolder).Select(p => new
            {
                id       = p.Id.ToString(),
                name     = p.Name,
                protocol = p.ProtocolId,
                host     = p.Host,
            }).ToArray(),
        };
    }

    /// <summary>
    /// Resolves the target profile by exact <c>profile_id</c> if given, else by <c>name</c>
    /// (case-insensitive: an exact match wins outright, otherwise a substring match is only
    /// accepted if it's unambiguous), gate-checks opening it, and opens it via
    /// <see cref="ISessionManager.OpenAsync"/>. Newly opened sessions are picked up automatically by
    /// <see cref="OnSessionOpened"/> — the same handler used for GUI-opened ones — so no separate
    /// tracking step is needed here, and the session shows up as a real tab in the running app too.
    /// </summary>
    private async Task<object> ConnectionOpenAsync(JsonElement @params, CancellationToken ct)
    {
        var profile = await ResolveProfileForOpenAsync(@params, ct).ConfigureAwait(false);

        await GateCheckAsync(McpAction.ConnectionOpen, profile, $"open a new {profile.ProtocolId} session", ct).ConfigureAwait(false);

        var session = await _sessions.OpenAsync(profile.Id, ct).ConfigureAwait(false);
        return new
        {
            session_id = session.SessionId.ToString(),
            name       = session.Profile.Name,
            protocol   = session.Profile.ProtocolId,
            state      = session.State.ToString(),
        };
    }

    /// <summary>Internal (not private) so <c>PolyTerm.App.Tests</c> can exercise the name-resolution logic directly without going through the named pipe.</summary>
    internal async Task<ConnectionProfile> ResolveProfileForOpenAsync(JsonElement @params, CancellationToken ct)
    {
        if (@params.TryGetProperty("profile_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            && Guid.TryParse(idEl.GetString(), out var id))
        {
            return await _profiles.ResolveAsync(id, ct).ConfigureAwait(false);
        }

        var name = @params.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("connection_open requires either 'name' or 'profile_id'.");

        var all = await _profiles.GetAllAsync(ct).ConfigureAwait(false);
        var candidates = all.Where(p => !p.IsFolder).ToList();

        var exact = candidates.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return await _profiles.ResolveAsync(exact.Id, ct).ConfigureAwait(false);

        var matches = candidates.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
            return await _profiles.ResolveAsync(matches[0].Id, ct).ConfigureAwait(false);
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"'{name}' matches multiple connections: " +
                string.Join(", ", matches.Select(p => $"\"{p.Name}\" ({p.Id})")) +
                " — use profile_id to disambiguate.");

        throw new InvalidOperationException($"No connection found matching '{name}'. Use connection_list to see what's available.");
    }

    private async Task<object> ConnectionCloseAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        await _sessions.CloseAsync(Guid.Parse(sessionId), ct).ConfigureAwait(false);
        return new { ok = true };
    }

    private object TerminalRead(JsonElement @params)
    {
        var sessionId = GetSessionId(@params);
        var lines     = @params.TryGetProperty("lines", out var l) ? l.GetInt32() : 50;

        var buffer = GetEntry(sessionId).Buffer;
        return new { text = string.Join('\n', buffer.GetRecentLines(lines)) };
    }

    /// <summary>
    /// Sends a command and waits for output to settle — polling every 150 ms until 500 ms pass
    /// with no new lines, or until <c>timeout_seconds</c> elapses — then returns everything
    /// written since the command was sent.
    /// </summary>
    private async Task<object> TerminalExecuteAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var command   = @params.GetProperty("command").GetString()!;
        var timeout   = @params.TryGetProperty("timeout_seconds", out var t) ? t.GetInt32() : 30;

        var entry = GetEntry(sessionId);
        if (entry.Session.Terminal is null)
            throw new InvalidOperationException("Session has no terminal.");

        await GateCheckAsync(McpAction.TerminalExecute, entry.Session.Profile, command, ct).ConfigureAwait(false);

        var startLine = entry.Buffer.TotalLinesWritten;

        await entry.Session.Terminal
            .SendInputAsync(Encoding.UTF8.GetBytes(command + "\n"), ct)
            .ConfigureAwait(false);

        // Wait for output to settle (no new lines for 500 ms) or timeout
        var deadline  = DateTime.UtcNow.AddSeconds(timeout);
        var lastCount = entry.Buffer.TotalLinesWritten;
        var quietSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            var current = entry.Buffer.TotalLinesWritten;
            if (current != lastCount)
            {
                lastCount  = current;
                quietSince = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - quietSince).TotalMilliseconds >= 500)
            {
                break;
            }
        }

        return new { text = string.Join('\n', entry.Buffer.GetLinesSince(startLine)) };
    }

    private async Task<object> TerminalSendKeysAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var keys      = @params.GetProperty("keys").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.Terminal is null)
            throw new InvalidOperationException("Session has no terminal.");

        await GateCheckAsync(McpAction.TerminalSendKeys, entry.Session.Profile, EscapeForDisplay(keys), ct).ConfigureAwait(false);

        await entry.Session.Terminal
            .SendInputAsync(Encoding.UTF8.GetBytes(keys), ct)
            .ConfigureAwait(false);

        return new { ok = true };
    }

    // ── Attach (issue #77/#93/#114/#115/#116 — PolyTerm.Cli's `polyterm attach`) ────────────────────
    // A human directly driving their own already-open session over the pipe, not an AI agent acting
    // through it — the same trust level as typing/clicking into the session's tab in the desktop app
    // itself (never gated), so unlike TerminalSendKeysAsync/FramebufferClickAsync above,
    // AttachSendInputAsync/the attach_framebuffer_* methods do not call GateCheckAsync. session_list
    // (already existing) doubles as the attach-candidate list for both terminal and framebuffer
    // sessions: sessions stay tracked here from SessionOpened to SessionClosed regardless of whether
    // MainViewModel still has a tab open for them, so a detached-but-live session already shows up
    // there with no changes. attach_start/attach_stop are shared across both capabilities (one
    // exclusivity concept per session — a session is Terminal- or Framebuffer-capable, never both);
    // attach_read/attach_send_input are terminal-specific, attach_framebuffer_read/send_pointer/
    // send_key/resize are the framebuffer-specific counterparts added for #115.

    /// <summary>
    /// Claims exclusive attach on a session — a second <c>attach_start</c> for the same session fails
    /// until the first caller releases it via <see cref="AttachStop"/> or its session closes. Recommended-
    /// exclusive per #114/#116: attach is a live single-reader/writer takeover, not a read-only
    /// observation like <c>terminal_read</c> — simultaneous writers would race on stdin/input. One
    /// exclusivity concept regardless of capability (issue #115): a session is Terminal-capable or
    /// Framebuffer-capable, never both, so the response shape below simply branches on which. Terminal
    /// sessions get the last <c>backlog</c> lines plus the buffer's current line count (unchanged from
    /// #114/#116), so the caller can render context immediately and then poll <see cref="AttachRead"/>
    /// for only what arrives afterward; framebuffer sessions get an initial frame via the same encode
    /// path <see cref="AttachFramebufferReadAsync"/> polls afterward.
    /// </summary>
    private async Task<object> AttachStartAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var id    = Guid.Parse(sessionId);
        var entry = GetEntry(sessionId);

        lock (_lock)
        {
            if (!_attached.Add(id))
                throw new InvalidOperationException($"Session '{sessionId}' is already attached from another client.");
        }

        try
        {
            if (entry.Session.Terminal is not null)
            {
                var backlog = @params.TryGetProperty("backlog", out var b) ? b.GetInt32() : 100;
                return new
                {
                    kind        = "terminal",
                    text        = string.Join('\n', entry.Buffer.GetRecentLines(backlog)),
                    total_lines = entry.Buffer.TotalLinesWritten,
                };
            }

            if (entry.Session.Framebuffer is not null || entry.Session.NativeHost is not null)
                return await EncodeFramebufferOrThrowAsync(entry, ct).ConfigureAwait(false);

            throw new InvalidOperationException("Session has neither a terminal nor a graphical desktop to attach to.");
        }
        catch
        {
            // Roll back the claim above — otherwise a session with no attachable capability would be
            // permanently unattachable after one failed attach_start call.
            lock (_lock) { _attached.Remove(id); }
            throw;
        }
    }

    /// <summary>Releases the exclusive attach claimed by <see cref="AttachStart"/>. A no-op (not an error) if the session was never attached, so a best-effort cleanup call can't itself fail.</summary>
    private object AttachStop(JsonElement @params)
    {
        var id = Guid.Parse(GetSessionId(@params));
        lock (_lock) { _attached.Remove(id); }
        return new { ok = true };
    }

    /// <summary>
    /// Returns only the lines committed since <c>since</c> (a <c>total_lines</c> cursor from a prior
    /// <see cref="AttachStart"/>/<see cref="AttachRead"/> response) — the incremental-tail counterpart
    /// to <see cref="TerminalRead"/>'s fixed "last N lines" window, needed so an attach loop can poll
    /// without re-printing lines it already showed.
    /// </summary>
    private object AttachRead(JsonElement @params)
    {
        var sessionId = GetSessionId(@params);
        var since     = @params.GetProperty("since").GetInt64();

        var buffer = GetEntry(sessionId).Buffer;
        return new
        {
            text        = string.Join('\n', buffer.GetLinesSince(since)),
            total_lines = buffer.TotalLinesWritten,
        };
    }

    /// <summary>Sends raw input to an attached session's terminal — see this section's header comment for why this bypasses <see cref="GateCheckAsync"/>, unlike <see cref="TerminalSendKeysAsync"/>.</summary>
    private async Task<object> AttachSendInputAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var data      = @params.GetProperty("data").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.Terminal is null)
            throw new InvalidOperationException("Session has no terminal.");

        await entry.Session.Terminal.SendInputAsync(Encoding.UTF8.GetBytes(data), ct).ConfigureAwait(false);
        return new { ok = true };
    }

    private async Task<object> SftpListAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var path      = @params.TryGetProperty("path", out var p) ? p.GetString()! : "/";

        var entry = GetEntry(sessionId);
        if (entry.Session.FileTransfer is null)
            throw new InvalidOperationException("Session has no SFTP channel.");

        var entries = await entry.Session.FileTransfer.ListAsync(path, ct).ConfigureAwait(false);
        return new
        {
            path,
            entries = entries.Select(e => new
            {
                name     = e.Name,
                path     = e.FullPath,
                is_dir   = e.IsDirectory,
                size     = e.Size,
                modified = e.LastModified.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            }).ToArray(),
        };
    }

    private async Task<object> SftpReadAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var path      = @params.GetProperty("path").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.FileTransfer is null)
            throw new InvalidOperationException("Session has no SFTP channel.");

        using var ms = new MemoryStream();
        await entry.Session.FileTransfer.DownloadAsync(path, ms, null, ct).ConfigureAwait(false);

        if (ms.Length > 200_000)
            throw new InvalidOperationException(
                $"File is {ms.Length:N0} bytes — too large for sftp_read (max 200 KB). Download it instead.");

        string text;
        try   { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(ms.ToArray()); }
        catch { throw new InvalidOperationException($"'{path}' contains non-UTF-8 bytes — it is likely a binary file."); }

        return new { path, text, size = ms.Length };
    }

    private async Task<object> SftpWriteAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var path      = @params.GetProperty("path").GetString()!;
        var content   = @params.GetProperty("content").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.FileTransfer is null)
            throw new InvalidOperationException("Session has no SFTP channel.");

        await GateCheckAsync(McpAction.SftpWrite, entry.Session.Profile, $"write {path}", ct).ConfigureAwait(false);

        var bytes = new UTF8Encoding(false).GetBytes(content);
        using var ms = new MemoryStream(bytes);
        await entry.Session.FileTransfer.UploadAsync(ms, path, null, ct).ConfigureAwait(false);

        return new { path, bytes_written = bytes.Length };
    }

    private async Task<object> SftpDeleteAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var path      = @params.GetProperty("path").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.FileTransfer is null)
            throw new InvalidOperationException("Session has no SFTP channel.");

        await GateCheckAsync(McpAction.SftpDelete, entry.Session.Profile, $"delete {path}", ct).ConfigureAwait(false);

        await entry.Session.FileTransfer.DeleteAsync(path, ct).ConfigureAwait(false);

        return new { ok = true, path };
    }

    private async Task<object> SftpRenameAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId   = GetSessionId(@params);
        var source      = @params.GetProperty("source").GetString()!;
        var destination = @params.GetProperty("destination").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.FileTransfer is null)
            throw new InvalidOperationException("Session has no SFTP channel.");

        await GateCheckAsync(McpAction.SftpRename, entry.Session.Profile, $"rename {source} -> {destination}", ct).ConfigureAwait(false);

        await entry.Session.FileTransfer.RenameAsync(source, destination, ct).ConfigureAwait(false);

        return new { ok = true, source, destination };
    }

    // ── FramebufferTools (RDP/VNC via MCP — spec §4.7's Tools/FramebufferTools.cs) ──────────────────
    // Capability-scoped, not protocol-scoped (design rule 2): every method below checks
    // Session.Framebuffer (VNC) then Session.NativeHost (RDP) and works the same way regardless of
    // which one a session actually has — a new graphical-desktop protocol gets these for free the
    // same way TerminalTools already works for any Terminal-capable session.

    /// <summary>
    /// One-shot MCP tool: captures and returns a framebuffer session's current frame right now. Delegates
    /// to <see cref="EncodeFramebufferOrThrowAsync"/> (shared with the attach polling path below, issue
    /// #115) — the response now also carries <c>kind</c>/<c>format</c>/<c>version</c> fields that only
    /// the attach callers use, harmless additions alongside the original <c>image_base64</c>/<c>width</c>/
    /// <c>height</c> this tool has always returned.
    /// </summary>
    private async Task<object> FramebufferScreenshotAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        return await EncodeFramebufferOrThrowAsync(GetEntry(sessionId), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures and PNG-encodes a framebuffer session's current frame — shared by the one-shot MCP
    /// <c>framebuffer_screenshot</c> tool and the polling <c>attach_start</c>/<c>attach_framebuffer_read</c>
    /// pair (issue #115), so this encode path (reusing <see cref="IFramebufferChannel.CaptureSnapshot"/> +
    /// <see cref="EncodePngBase64Async"/>) exists in exactly one place. Tags the result <c>kind:
    /// "framebuffer"</c> and includes the session's current <see cref="FramebufferVersionTracker"/> value,
    /// both only meaningful to the attach callers.
    /// </summary>
    private static async Task<object> EncodeFramebufferOrThrowAsync(SessionEntry entry, CancellationToken ct)
    {
        if (entry.Session.Framebuffer is { } vnc)
        {
            var pixels = vnc.CaptureSnapshot()
                ?? throw new InvalidOperationException("No framebuffer data received from the server yet.");
            var bgra = vnc.Format switch
            {
                FramebufferFormat.Rgba32 => false,
                FramebufferFormat.Bgra32 => true,
                _ => throw new InvalidOperationException($"Screenshot not supported for framebuffer format {vnc.Format}."),
            };
            var base64 = await EncodePngBase64Async(pixels, vnc.Width, vnc.Height, bgra).ConfigureAwait(false);
            return new { kind = "framebuffer", image_base64 = base64, width = vnc.Width, height = vnc.Height, format = vnc.Format.ToString(), version = entry.FramebufferVersion.Value };
        }

        if (entry.Session.NativeHost is { } native)
        {
            var snapshot = native.CaptureSnapshot()
                ?? throw new InvalidOperationException("Native window isn't ready yet, or capture failed.");
            var base64 = await EncodePngBase64Async(snapshot.Data, snapshot.Width, snapshot.Height, bgra: true).ConfigureAwait(false);
            // Always 0: nothing bumps FramebufferVersion for a NativeHost-only session (no
            // RegionUpdated-equivalent event exists on that interface) — unimplemented by any real
            // protocol today, so attach_framebuffer_read would report changed:true on every poll
            // against a hypothetical future native-host session. Not a bug, just not yet meaningful.
            return new { kind = "framebuffer", image_base64 = base64, width = snapshot.Width, height = snapshot.Height, format = "Bgra32", version = entry.FramebufferVersion.Value };
        }

        throw new InvalidOperationException("Session has no graphical desktop (framebuffer or native host).");
    }

    /// <summary>
    /// Returns only-on-change framebuffer polling for <c>polyterm attach</c>-style clients (issue #115):
    /// <c>{ changed: false, version }</c> with no encode cost at all when nothing changed since the
    /// caller's <c>since_version</c> cursor, otherwise a fresh frame via <see cref="EncodeFramebufferOrThrowAsync"/>.
    /// <c>since_version &lt;= 0</c> always encodes (treated as "I have nothing yet", mirroring
    /// <c>attach_start</c> having just handed out a real version to a genuine first-time caller).
    /// </summary>
    private async Task<object> AttachFramebufferReadAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId    = GetSessionId(@params);
        var sinceVersion = @params.TryGetProperty("since_version", out var s) ? s.GetInt64() : 0;

        var entry          = GetEntry(sessionId);
        var currentVersion = entry.FramebufferVersion.Value;

        if (sinceVersion > 0 && sinceVersion >= currentVersion)
            return new { changed = false, version = currentVersion };

        return await EncodeFramebufferOrThrowAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one raw pointer event to an attached framebuffer session — see the "Attach" section header
    /// comment above for why this bypasses <see cref="GateCheckAsync"/>, unlike the gated
    /// <see cref="FramebufferClickAsync"/>. Deliberately does NOT send an implicit press+release pair
    /// like the gated click tool does: a human dragging or holding a button needs the two edges
    /// independently timed by their own input loop, not collapsed into one call.
    /// </summary>
    private async Task<object> AttachFramebufferSendPointerAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var x      = @params.GetProperty("x").GetInt32();
        var y      = @params.GetProperty("y").GetInt32();
        var button = @params.TryGetProperty("button", out var b) ? b.GetString() ?? "left" : "left";
        var kind   = @params.GetProperty("kind").GetString()!; // "press" | "release" | "move"

        var entry = GetEntry(sessionId);
        var pointerButton = button.ToLowerInvariant() switch
        {
            "left"   => PointerButton.Left,
            "right"  => PointerButton.Right,
            "middle" => PointerButton.Middle,
            "none"   => PointerButton.None,
            _ => throw new InvalidOperationException($"Unknown button '{button}' — expected left, right, middle, or none."),
        };
        var eventKind = kind.ToLowerInvariant() switch
        {
            "press"   => PointerEventKind.Press,
            "release" => PointerEventKind.Release,
            "move"    => PointerEventKind.Move,
            _ => throw new InvalidOperationException($"Unknown pointer event kind '{kind}' — expected press, release, or move."),
        };

        await SendPointerToSessionAsync(entry, x, y, pointerButton, eventKind, ct).ConfigureAwait(false);
        return new { ok = true };
    }

    /// <summary>Sends one raw key event to an attached framebuffer session — same bypass-the-gate rationale as <see cref="AttachFramebufferSendPointerAsync"/>, one event per call rather than the gated <see cref="FramebufferKeyAsync"/>'s implicit press+release pair.</summary>
    private async Task<object> AttachFramebufferSendKeyAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var keySymbol = @params.GetProperty("keysym").GetString()!;
        var kind      = @params.GetProperty("kind").GetString()!; // "press" | "release"

        var entry = GetEntry(sessionId);
        var eventKind = kind.ToLowerInvariant() switch
        {
            "press"   => KeyEventKind.Press,
            "release" => KeyEventKind.Release,
            _ => throw new InvalidOperationException($"Unknown key event kind '{kind}' — expected press or release."),
        };

        await SendKeyToSessionAsync(entry, keySymbol, eventKind, ct).ConfigureAwait(false);
        return new { ok = true };
    }

    /// <summary>Resizes an attached framebuffer session's remote desktop — same bypass-the-gate rationale as the other attach_framebuffer_* methods; identical dispatch to the gated <see cref="FramebufferResizeAsync"/> via the shared <see cref="ResizeSessionAsync"/> helper.</summary>
    private async Task<object> AttachFramebufferResizeAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var width     = @params.GetProperty("width").GetInt32();
        var height    = @params.GetProperty("height").GetInt32();

        await ResizeSessionAsync(GetEntry(sessionId), width, height, ct).ConfigureAwait(false);
        return new { ok = true };
    }

    private async Task<object> FramebufferClickAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var x      = @params.GetProperty("x").GetInt32();
        var y      = @params.GetProperty("y").GetInt32();
        var button = @params.TryGetProperty("button", out var b) ? b.GetString() ?? "left" : "left";

        var entry = GetEntry(sessionId);
        await GateCheckAsync(McpAction.FramebufferClick, entry.Session.Profile, $"click ({button}) at ({x}, {y})", ct).ConfigureAwait(false);

        var pointerButton = button.ToLowerInvariant() switch
        {
            "left"   => PointerButton.Left,
            "right"  => PointerButton.Right,
            "middle" => PointerButton.Middle,
            _ => throw new InvalidOperationException($"Unknown button '{button}' — expected left, right, or middle."),
        };

        await SendPointerToSessionAsync(entry, x, y, pointerButton, PointerEventKind.Press, ct).ConfigureAwait(false);
        await SendPointerToSessionAsync(entry, x, y, pointerButton, PointerEventKind.Release, ct).ConfigureAwait(false);

        return new { ok = true };
    }

    private async Task<object> FramebufferTypeAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var text      = @params.GetProperty("text").GetString()!;

        var entry = GetEntry(sessionId);
        await GateCheckAsync(McpAction.FramebufferType, entry.Session.Profile, $"type {text.Length} character(s)", ct).ConfigureAwait(false);

        foreach (var c in text)
        {
            if (entry.Session.Framebuffer is { } vnc)
            {
                // Latin-1 range maps directly to the character's code point; beyond that the X11
                // Unicode keysym convention is 0x01000000 + code point (X11 keysymdef.h spec) — same
                // conversion PolyTerm.App.Surfaces/Framebuffer/FramebufferSurface.cs uses for real
                // keyboard input, so an agent's typed text behaves identically to a human typing it.
                var symbol = ((int)(c <= 0xFF ? c : 0x01000000 + c)).ToString(CultureInfo.InvariantCulture);
                await vnc.SendKeyAsync(new KeyEvent { KeySymbol = symbol, Kind = KeyEventKind.Press }, ct).ConfigureAwait(false);
                await vnc.SendKeyAsync(new KeyEvent { KeySymbol = symbol, Kind = KeyEventKind.Release }, ct).ConfigureAwait(false);
            }
            else if (entry.Session.NativeHost is { } native)
            {
                // RdpNativeHostChannel treats a length-1 KeySymbol as a literal Unicode character
                // (WM_CHAR) — no separate release event needed for that path.
                await native.SendKeyAsync(new KeyEvent { KeySymbol = c.ToString(), Kind = KeyEventKind.Press }, ct).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("Session has no graphical desktop (framebuffer or native host).");
            }
        }

        return new { ok = true, chars_sent = text.Length };
    }

    private async Task<object> FramebufferKeyAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var key       = @params.GetProperty("key").GetString()!;

        var entry = GetEntry(sessionId);
        await GateCheckAsync(McpAction.FramebufferKey, entry.Session.Profile, $"press {key}", ct).ConfigureAwait(false);

        await SendKeyToSessionAsync(entry, key, KeyEventKind.Press, ct).ConfigureAwait(false);
        await SendKeyToSessionAsync(entry, key, KeyEventKind.Release, ct).ConfigureAwait(false);

        return new { ok = true };
    }

    private async Task<object> FramebufferResizeAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var width     = @params.GetProperty("width").GetInt32();
        var height    = @params.GetProperty("height").GetInt32();

        await ResizeSessionAsync(GetEntry(sessionId), width, height, ct).ConfigureAwait(false);
        return new { ok = true };
    }

    /// <summary>Shared resize dispatch for the gated <see cref="FramebufferResizeAsync"/> MCP tool and the ungated <see cref="AttachFramebufferResizeAsync"/> attach method (issue #115) — one place for the Framebuffer/NativeHost capability branch.</summary>
    private static async Task ResizeSessionAsync(SessionEntry entry, int width, int height, CancellationToken ct)
    {
        if (entry.Session.Framebuffer is { } vnc)
            await vnc.ResizeAsync(width, height, ct).ConfigureAwait(false);
        else if (entry.Session.NativeHost is { } native)
            await native.ResizeAsync(width, height, ct).ConfigureAwait(false);
        else
            throw new InvalidOperationException("Session has no graphical desktop (framebuffer or native host).");
    }

    // ── WebTools (embedded HTTP/S sessions via MCP) ─────────────────────────
    // A dedicated tool set rather than reusing FramebufferTools: framebuffer_click/type are
    // pixel-coordinate and X11-keysym based, calibrated against IFramebufferChannel.Width/Height —
    // neither concept applies to a DOM, and force-fitting them would either misinterpret coordinates
    // against page reflow/zoom or grow a hidden DOM-vs-pixel branch into that capability-scoped code.

    private async Task<object> WebNavigateAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var url       = @params.GetProperty("url").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.Web is not { } web)
            throw new InvalidOperationException("Session has no embedded web view.");

        await GateCheckAsync(McpAction.WebNavigate, entry.Session.Profile, $"navigate to {url}", ct).ConfigureAwait(false);
        await web.NavigateAsync(new Uri(url), ct).ConfigureAwait(false);
        return new { ok = true };
    }

    private object WebGetUrl(JsonElement @params)
    {
        var sessionId = GetSessionId(@params);
        var entry = GetEntry(sessionId);
        if (entry.Session.Web is not { } web)
            throw new InvalidOperationException("Session has no embedded web view.");

        return new { url = web.CurrentUri.ToString() };
    }

    private async Task<object> WebScreenshotAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var entry = GetEntry(sessionId);
        if (entry.Session.Web is not { } web)
            throw new InvalidOperationException("Session has no embedded web view.");

        // CaptureScreenshotAsync already returns PNG-encoded bytes (WebView2's CapturePreviewAsync,
        // where implemented) — unlike FramebufferScreenshotAsync's raw pixels, this must NOT be
        // routed through EncodePngBase64Async, which would try to reinterpret already-compressed
        // PNG bytes as a raw row-major pixel buffer.
        var png = await web.CaptureScreenshotAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Screenshot capture isn't available for this session.");
        return new { image_base64 = Convert.ToBase64String(png) };
    }

    private async Task<object> WebInvokeScriptAsync(JsonElement @params, CancellationToken ct)
    {
        var sessionId = GetSessionId(@params);
        var script    = @params.GetProperty("script").GetString()!;

        var entry = GetEntry(sessionId);
        if (entry.Session.Web is not { } web)
            throw new InvalidOperationException("Session has no embedded web view.");

        await GateCheckAsync(McpAction.WebInvokeScript, entry.Session.Profile, $"run script: {script}", ct).ConfigureAwait(false);
        var result = await web.InvokeScriptAsync(script, ct).ConfigureAwait(false);
        return new { result };
    }

    private static ValueTask SendPointerToSessionAsync(SessionEntry entry, int x, int y, PointerButton button, PointerEventKind kind, CancellationToken ct)
    {
        var e = new PointerEvent { X = x, Y = y, Button = button, Kind = kind };
        if (entry.Session.Framebuffer is { } vnc) return vnc.SendPointerAsync(e, ct);
        if (entry.Session.NativeHost is { } native) return native.SendPointerAsync(e, ct);
        throw new InvalidOperationException("Session has no graphical desktop (framebuffer or native host).");
    }

    private static ValueTask SendKeyToSessionAsync(SessionEntry entry, string keySymbol, KeyEventKind kind, CancellationToken ct)
    {
        var e = new KeyEvent { KeySymbol = keySymbol, Kind = kind };
        if (entry.Session.Framebuffer is { } vnc) return vnc.SendKeyAsync(e, ct);
        if (entry.Session.NativeHost is { } native) return native.SendKeyAsync(e, ct);
        throw new InvalidOperationException("Session has no graphical desktop (framebuffer or native host).");
    }

    /// <summary>
    /// Encodes raw row-major pixel bytes as a base64 PNG via Avalonia's own <see cref="WriteableBitmap"/>
    /// (backed by Skia, already a dependency — no separate imaging package needed). Runs on the
    /// Avalonia UI thread defensively: <see cref="McpBridgeService"/>'s request handling runs on a
    /// background task, and while WriteableBitmap construction/Save aren't documented as UI-thread-only,
    /// every other Avalonia bitmap/rendering call in this codebase is marshalled there, so this matches
    /// that same discipline rather than assuming otherwise.
    /// </summary>
    private static async Task<string> EncodePngBase64Async(byte[] pixels, int width, int height, bool bgra) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                bgra ? PixelFormat.Bgra8888 : PixelFormat.Rgba8888, AlphaFormat.Unpremul);

            using (var fb = bitmap.Lock())
            {
                var stride = width * 4;
                for (var row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * stride, fb.Address + row * fb.RowBytes, stride);
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, PngBitmapEncoderOptions.Default);
            return Convert.ToBase64String(ms.ToArray());
        });

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> (surfaced to the agent as a normal tool error, not a crash) if <paramref name="gate"/> denies the action.</summary>
    private async Task GateCheckAsync(McpAction action, ConnectionProfile profile, string detail, CancellationToken ct)
    {
        if (!await _gate.CheckAsync(action, profile, detail, ct).ConfigureAwait(false))
            throw new UnauthorizedAccessException($"Denied by MCP approval policy for '{profile.Name}'.");
    }

    /// <summary>Renders control characters (e.g. the \x03 in a Ctrl+C send_keys call) as visible escapes for the approval prompt, instead of raw bytes.</summary>
    private static string EscapeForDisplay(string s) =>
        string.Concat(s.Select(c => c < 0x20 || c == 0x7F ? $"\\x{(int)c:x2}" : c.ToString()));

    private static string BuildError(int id, string message) =>
        JsonSerializer.Serialize(new { id, error = message });
}
