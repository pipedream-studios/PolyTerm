# Testing — what's covered, what could be, and what can't

Snapshot as of 2026-07-09 (fourth pass). Companion to `docs/todos.md`'s "Manual / click-testing
needed" list — that one is a punch list of specific unverified features; this one is about testing
*infrastructure*: what exists, what a real test suite can cover, and what's fundamentally out of
reach without tooling this repo doesn't have.

## 1. What's actually tested today

**Eight** test projects now, `xunit` + `FluentAssertions` (+ `Moq`, `Avalonia.Headless.XUnit`, `FlaUI`
where needed), **224 tests total** (1 deliberately skipped, see `PolyTerm.FlaUI.Tests` below), all
passing, 0 build warnings.

- **`tests/PolyTerm.Engine.Tests`** (12 tests) — `ProfileStoreTests.cs`: save/retrieve, delete, folder
  children, update, plus 6 folder-inheritance cases (including a deliberate tripwire proving
  `Settings` inheritance doesn't actually work — see `ResolveAsync_DoesNotInheritSettings_KnownGap`).
  `SessionManagerTests.cs`: jump-host cycle detection via the public `OpenAsync` contract.
- **`tests/PolyTerm.Terminal.Tests`** (13 tests) — unchanged: the VT state machine and cell grid.
- **`tests/PolyTerm.App.Tests`** (164 tests) — macro expansion, both importers (MobaXterm/mRemoteNG,
  the latter now including real password-decryption coverage), MCP name resolution, approval-gate
  policy resolution (all pure logic, no UI), real `Avalonia.Headless` dialog tests (`ConfirmDialogTests`),
  `TunnelsViewModelTests`/`MacrosViewModelTests`, `SftpBrowserViewModelTests` (chmod plus M4's
  edit-in-local-editor round-trip — real temp-file I/O and a real `FileSystemWatcher`, not mocked),
  session-recorder rebind/pause-resume tests, `SessionTabViewModel` recording/error-tracking tests, and
  a growing MCP bridge suite: `McpBridgeServicePipeTests` (the named-pipe hang regression plus real
  multi-client coverage), `McpRateLimiterTests`/`McpAuditLoggerTests`/`McpBridgeServiceAuditRateLimitTests`,
  and `McpBridgeServiceFramebufferToolsTests`. Also `Win32NativeWindowInputTests` — RDP's Win32
  capture/input-injection helper, driven against a real (throwaway, non-RDP) WinForms window —
  `WriteableBitmapHeadlessSaveDiagnosticTests`, which documents a real `Avalonia.Headless` platform
  limitation found this pass (see §2) — `RdpAudioDriveSettingsTests` (M4's drive/audio redirection
  first rung: schema defaults matched against a live-probed real control instance, plus the pure
  setting-to-control-value mapping logic) — `SessionTabViewModelResizeTests` (M4's resize-support
  indicator, driven against a fake `INativeHostChannel`) — and `CommandPaletteFilterTests`/
  `CommandPaletteViewModelTests` (M4's command palette: pure fuzzy-match ranking, and the
  search/selection/confirm state machine).
- **`tests/PolyTerm.Protocols.Vnc.Tests`** (1 test) — `VncPixelFormatTests`: end-to-end against a
  hand-rolled fake RFB server, decoding real pixel bytes through the real `MarcusW.VncClient` library,
  now also covering `CaptureSnapshot()` (the MCP `framebuffer_screenshot` tool's VNC pull path) through
  the same real pipeline. **The original pixel-format test found and fixed a real, previously-shipped
  bug** — see §2.
- **`tests/PolyTerm.Protocols.Ssh.Tests`** (13 tests) — end-to-end against a real, ad-hoc Windows
  OpenSSH server: connect + command execution, a local tunnel actually forwarding TCP traffic, a full
  jump-host chain connecting through a bastion to a target, and auto-reconnect (kill the real `sshd`
  mid-session, restart it, confirm `SshSession`'s own backoff loop recovers and the session works
  again afterward) — all against *real* sshd processes, not mocks. Also `X11DisplayEndpointTests` (7
  tests, pure `DISPLAY`-string parsing — found and fixed a real bug in the `unix:` prefix case) and two
  `SshX11Tests` covering X11 forwarding's "loud failure when unsupported" path against a real server
  that doesn't support it (Windows' own sshd). See §2 for the M4 X11 section's full verification
  breakdown, including a real crash found in the happy-path data-flow test specifically under
  `dotnet test`'s test host (not reproducible outside it — see docs/todos.md).
- **`tests/PolyTerm.Mcp.Tests`** (12 tests, new this pass) — `PolyTerm.Mcp`'s first test coverage of
  any kind, ever: the actual client-facing MCP protocol layer (JSON-RPC framing, `tools/list` schema
  shape including the new `framebuffer_*` tools, `tools/call` dispatch, the image-vs-text
  content-block choice, notifications, parse errors). Needed a small refactor first — see §2.
- **`tests/PolyTerm.Cli.Tests`** (6 tests, new this pass) — `PolyTerm.Cli`'s first test coverage:
  the connect-failure crash fix (clean message + exit code 1, not an unhandled-exception dump) for
  both `connect`/`exec`, and `profiles list/add/delete` against a fake `IProfileStore`. Also needed a
  small refactor first — see §2.
- **`tests/PolyTerm.FlaUI.Tests`** (3 tests, new this pass) — real native-window automation against
  the actual, already-built `PolyTerm.exe`: launch + `AutomationId` inspection, and keyboard-input
  round-tripping through a real text box, both passing. A third test (mouse-click interaction) is
  present but `[Fact(Skip = ...)]`'d — a real, deeply-investigated gap, not an untried idea. See §2.
  Kept in its own project since launching a real app process per test class is far slower/heavier
  than the rest of this repo's suite, and needs `[assembly: CollectionBehavior(DisableTestParallelization
  = true)]` since two concurrently-launched `PolyTerm.exe` instances caused a real, intermittent
  `UIA3Automation` COM initialization race.

**Still zero committed automated tests** for `PolyTerm.App.Surfaces` and three protocol modules
(`LocalShell`, `Serial`, `Telnet`, `Web` — `Web` was verified via a throwaway scratchpad harness
earlier but never turned into a permanent test). `Rdp` now has real (if partial) coverage via
`Win32NativeWindowInputTests` — the Win32 capture/input-injection layer, not the ActiveX control
itself, which still needs a live RDP session to verify.

## 2. This pass's work, in detail

### VNC: found and fixed a real pixel-format bug

`docs/todos.md`'s VNC section flagged pixel byte order (RGBA vs BGRA) as "the one thing genuinely
unverified" after VNC first shipped. `VncPixelFormatTests` resolves this for real: a hand-rolled
minimal RFB 3.8 server (`FakeRfbServer.cs`, same fake-server technique as Telnet's — RFB's handshake
is plain length-prefixed messages, no cryptography, so hand-rolling it is practical) sends one
framebuffer update with known, distinct R/G/B values through the *real* `MarcusW.VncClient` library to
a real `VncSession`, and the test asserts on the actual decoded bytes.

**First run failed** — and it wasn't just a byte swap, it was a genuine 4-way permutation.
`VncFramebufferChannel` declared `PixelFormat.Plain` (the library's own pre-built format) to describe
its target buffer. Reading `MarcusW.VncClient`'s real source (`Color.ToPlainPixel()`:
`(R<<24)|(G<<16)|(B<<8)|255`) and confirming empirically against the test, "Plain"'s little-endian
byte layout is actually `[A,B,G,R]` in memory — not `[R,G,B,A]`, which is what `FramebufferSurface`
assumes when it blits into Avalonia's `PixelFormat.Rgba8888`. Colors would have rendered completely
scrambled, not just channel-swapped.

**Fixed** by constructing a custom `MarcusW.VncClient.PixelFormat` in `VncFramebufferChannel.cs`
(`redShift: 0, greenShift: 8, blueShift: 16, alphaShift: 24`) that actually produces `[R,G,B,A]` bytes
in little-endian memory, matching what `FramebufferSurface` expects. Re-ran the test against the fix:
passes. This is a real, shipped-and-now-fixed bug, not a documentation correction — anyone who'd
connected to a real VNC server before this fix would have seen garbled colors.

One thing discovered along the way, unrelated to the pixel bug: `MarcusW.VncClient`'s background
message-sender thread throws an *uncaught* `OperationCanceledException` on its own shutdown path when
its cancellation token fires — uncaught exceptions on any .NET thread crash the whole process, not
just fail a test. `VncPixelFormatTests` works around this by deliberately never cancelling or
disposing the session/connection (documented inline in the test) — a real, alpha-quality-library
limitation to be aware of if this code path is touched again, not something to route around by
suppressing exceptions in `VncFramebufferChannel` itself.

### SSH: a real ad-hoc OpenSSH server, not Docker and not hand-rolled

SSH-2 needs real Diffie-Hellman key exchange and host-key signing — hand-rolling it the way
`FakeRfbServer` hand-rolls RFB would be a large, high-risk undertaking on its own, and no Docker is
available in this environment (confirmed when it was needed for VNC too). Instead: Windows ships an
OpenSSH Server binary at `C:\Windows\System32\OpenSSH\sshd.exe`, confirmed (empirically, by trying it)
to run perfectly well **ad-hoc** — no service registration, no admin rights needed, just
`sshd.exe -f <config> -D` pointed at a throwaway config, host key, and port.

`FakeSshServer.cs` generates a throwaway ed25519 host key and client key pair per instance (via
`ssh-keygen.exe`, shipped alongside `sshd.exe`), binds a random loopback port, and starts the real
server as a child process. Three tests connect a real `SshSession` against it:

- **Connect + command execution** — sends `echo <marker>`, asserts the real shell's output round-trips.
- **Local tunnel actually forwards traffic** — opens a tunnel via `Tunnels!.OpenLocalAsync`, connects
  a plain TCP client to the forwarded local port, and confirms bytes round-trip through a separate
  echo listener standing in for "the remote side" — proving the forward is real, not a no-op that
  happens to compile.
- **Jump-host chain connects through a bastion to a target** — two independent `FakeSshServer`
  instances (different ports, different host keys), connected through `SshSession`'s own manual
  per-hop `ForwardedPortLocal` chaining (SSH.NET has no native ProxyJump support). If the chain logic
  were broken, this would simply fail to connect, since the target's port is only reachable in this
  test through the bastion's own forwarded port.

This directly resolves two of the three M3 punch-list items that were previously only click-testable
("Tunnels editor... confirm the forward actually works" and "Jump host / bastion chaining"). The
third — **auto-reconnect** — is deliberately not covered this pass: it needs restarting `sshd` on the
*exact same port* mid-session with precise timing around the 2s→30s backoff, which is real additional
fixture work (a `Stop()`/`RestartOnSamePortAsync()` pair, plus supplying `SessionContext.RefreshCredentialsAsync`
manually since these tests build `SessionContext` directly rather than through `SessionManager`). The
fixture built this pass makes that meaningfully cheaper to add later than it would have been before —
worth picking up if this area gets touched again, not attempted here given diminishing returns on an
already-long session.

Requires the Windows "OpenSSH Server" optional feature (Settings → Apps → Optional Features) to be
installed — `FakeSshServer.StartAsync` throws a clear, descriptive exception naming exactly that if
`sshd.exe` isn't found at the expected path, rather than failing confusingly.

### Avalonia.Headless: spiked, works, extended to real coverage

Added `Avalonia.Headless.XUnit` to `PolyTerm.App.Tests` (`TestAppBuilder.cs` — a plain `Application`,
deliberately not PolyTerm's own `App` class, to avoid pulling in the full DI container / MCP pipe
server just to test a dialog). First test in the repo using this package.

`ConfirmDialogTests` proves genuine interaction works, not just construction: `Button.RaiseEvent(new
RoutedEventArgs(Button.ClickEvent))` on the real `ActionButton`/Cancel buttons correctly drives
`ShowDialog<bool>`'s actual returned result (`true`/`false`), fully headless, no real window. This
validates the approach for the rest of the M3 UI punch list (Tunnels editor, Macros CRUD) — the
mechanics work; what's actually written this pass beyond the spike is `TunnelsViewModelTests`, which
turned out not to need Avalonia.Headless at all (see below).

**A real, small bug surfaced along the way — now fixed.** `ConfirmDialog`'s action button was
hardcoded to `Content="Delete"` with a red background in the `.axaml` (built originally for SFTP
delete confirmations) and reused as-is for the MCP approval-gate prompt, so an approval dialog showed
a red "Delete" button even when approving a non-destructive action like `terminal_execute`. Fixed by
adding optional `actionLabel`/`destructive` constructor parameters to `ConfirmDialog` (defaulting to
`"Delete"`/`true`, so `SftpBrowserView`'s existing call site needed zero changes) and having
`ApprovalGateService` pass an action-appropriate label per `McpAction` — "Connect"/"Run"/"Send"/
"Write"/"Rename" all non-destructive, "Delete" (still red) only for `SftpDelete`, the one MCP action
that actually is destructive. `ConfirmDialogTests` covers both the unchanged default behavior and the
new non-destructive styling (asserting the background is no longer the red delete color, not that it
matches some other specific value — headless controls outside a themed visual tree don't resolve a
style-derived default to compare against).

**`TunnelsViewModel` turned out to already be cleanly testable without any Avalonia involvement at
all**: it keeps the real edit dialog behind an injectable `EditTunnelAsync` delegate specifically to
avoid pulling `Window` into the view model (a pattern already used elsewhere in this codebase, e.g.
`NewConnectionViewModel.OpenTunnelsEditorAsync`) — which happens to make the actual add/edit/delete
list logic a plain, fast unit test. `TunnelsViewModelTests` covers add/edit/delete, cancellation, and
the "no editor wired up yet" no-op case — this is the logic the M3 punch list actually cares about
("does add/edit/delete work"), as distinct from "does clicking a button invoke a RelayCommand" (which
CommunityToolkit.Mvvm's source generator already handles reliably and doesn't need re-testing).

**`MacrosViewModel` — refactored to unlock the same testability, then covered.** Unlike
`TunnelsViewModel`, its constructor and every mutating command (`AddAsync`, `EditAsync`, `Delete`)
used to call `MacroStore.Load()`/`Save()` directly, hitting `%AppData%\PolyTerm\macros.json` — the
user's real saved macros — with no injection point at all. Rather than either touching that real file
(backup/restore around each test, with real risk of not restoring correctly if a test crashes
mid-run) or leaving the gap undone, the constructor now takes the initial macro list and a save
callback explicitly, mirroring `TunnelsViewModel`'s pattern exactly. The one real call site
(`MainWindow.axaml.cs`'s `Macros_Click`) now passes `MacroStore.Load()`/`MacroStore.Save` explicitly —
behaviorally identical to before, just no longer hardwired inside the view model.
`MacrosViewModelTests` covers add/edit/delete (including that the right thing gets persisted, via a
recording save callback — no real file touched, confirmed by checking `macros.json` doesn't exist on
disk after a full test run) plus `RunAsync`/`BroadcastMacroAsync`'s error-state handling when no
active session/broadcast target is available.

### FlaUI: now a permanent test project — real launch/inspection and keyboard input confirmed; mouse-click interaction is a real, deeply-investigated gap

`docs/testing.md`'s previous pass spiked FlaUI (a throwaway harness) and concluded physical clicking
was "confirmed to work." Building it into a permanent project (`tests/PolyTerm.FlaUI.Tests/`) this
pass surfaced a contradiction worth recording plainly: **physical mouse clicks do not reliably work
here**, despite the earlier spike's claim. Either something changed since, or that earlier
confirmation didn't hold up under the much more rigorous verification done this time. What's actually
confirmed now, with hard evidence at each step (not assumed):

- **Launching the real `PolyTerm.exe`, finding its main window, and locating elements by
  `AutomationId`: works reliably.** `MainWindow.axaml` now has explicit `AutomationProperties.AutomationId`
  on the key ribbon buttons and the session `TabControl`, so this no longer depends on Avalonia's
  patchier by-`Name`/`ControlType` fallback. Covered by a real, permanent, passing test
  (`MainWindowTests.Launch_ShowsTheRealMainWindow_WithExpectedTitleAndRibbon`).
- **Keyboard input via `SendInput` works completely reliably**: typing into the ribbon's
  `QuickConnectInput` text box and reading back `.Text` via UIA round-trips correctly every time.
  Covered by a real, permanent, passing test (`KeyboardDiagnosticTests`).
- **Mouse-click interaction does not work, and this was investigated thoroughly, not given up on
  quickly.** Confirmed directly, in order:
  - Windows' foreground-lock blocks a just-launched background process from stealing focus — fixed
    with the standard `AttachThreadInput` workaround (`Win32ForceForeground.Apply`), verified via
    `GetForegroundWindow()` matching PolyTerm's own HWND afterward.
  - The test host process (`testhost.exe`, not this project's own binary) isn't DPI-aware by default,
    so Windows was silently rescaling click coordinates — fixed via
    `SetProcessDpiAwarenessContext(PerMonitorAwareV2)` at runtime (an embedded `app.manifest` has no
    effect here, since the manifest that matters is the one on the actual host *process*). After this
    fix, the computed click point exactly matches the target button's own bounding-rect center.
  - `Mouse.Position` immediately before and after the click matches the intended point exactly (the
    click doesn't jump the cursor), and `WindowFromPoint` at that exact coordinate resolves to
    PolyTerm's own main-window HWND, not some other window — checked directly.
  - Three independent delivery mechanisms were tried: `SendInput`-based physical clicks (FlaUI's
    `Mouse` class), `PostMessage` posted directly to the window (bypassing the cursor/SendInput
    pipeline entirely), and `PostMessage` with an explicit `WM_MOUSEMOVE` first. All land on the
    verified-correct coordinate over the verified-correct window. None trigger the button's `Click`
    handler.
  - The UIA Invoke pattern (`Button.Invoke()`) reports `IsSupported`/enabled/on-screen for these
    buttons, but also doesn't fire `Click` — consistent with, and now a stronger version of, the
    earlier spike's inconclusive `MenuItem` finding.

**Bottom line**: every externally-observable fact about a synthetic mouse click here is correct
(cursor position, target window, foreground state, coordinates) across multiple independent delivery
mechanisms, and keyboard input over the identical `SendInput` path works fine — yet Avalonia's Win32
backend never dispatches the resulting `Click` event. Root-causing this fully would need message-level
tracing inside Avalonia's own Win32 window procedure (Spy++/WinDbg), which is out of scope for this
pass. The click-based test (`NewConnectionButton_PhysicalClick_OpensARealNewWindow_ThenCancelClosesIt`)
is committed but `[Fact(Skip = ...)]`'d with this full investigation in its doc comment, rather than
deleted or left silently red — a known, confirmed limitation, not an open question.

### MCP named-pipe hang: found, fixed, verified live, regression-tested

A later pass than the rest of this section (same day). `docs/todos.md`'s MCP section had flagged a
named-pipe round-trip hang as pre-existing and blocking — every MCP tool call, old and new, hung
indefinitely with no exception on either side. Root cause and fix:

- **The blind spot that stalled the earlier investigation**: `AppCompositionRoot.cs` only logged to
  `AddDebug()`, which is invisible outside an attached debugger — every earlier repro attempt was
  flying blind on what the *server* side was actually doing. Adding `AddConsole()` (kept permanently,
  not just for this debugging session — it costs nothing for a desktop app with no console attached,
  and it's what made this findable at all) immediately gave real visibility.
- **Root cause**: both `McpBridgeService` (server) and `PolyTermClient` (the real MCP client) built
  their `StreamWriter` with the shared `Encoding.UTF8` singleton, which carries a 3-byte BOM
  preamble. `StreamWriter` writes that preamble on its first `Flush()` — and `AutoFlush = true`, set
  in the same object-initializer line that constructs the writer, triggers that flush immediately,
  before any real data. That write to the pipe blocked forever, because nothing was yet actively
  reading from the other end. Confirmed precisely, not guessed: killing a hung client surfaced the
  *server's* own exception, an `IOException` with a stack trace pointing exactly at
  `StreamWriter.Flush`, called from `HandleConnectionAsync`'s `AutoFlush = true` line.
- **Fix**: `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` instead of `Encoding.UTF8` in
  both places — a one-line change per file, once the actual cause was known.
- **Verified against the real, unmodified `polyterm-mcp.exe`** (not a scratchpad harness) — piped raw
  JSON-RPC directly to the real binary and got real responses: `initialize`, `session_list`,
  `connection_list` (every real saved profile came back, "rump" among them — the exact host named in
  the user's own original example of what this feature should do), `connection_open` under a `Deny`
  policy (clean, correct rejection), and `connection_open` under `Allow` against a real profile — which
  correctly reached real `SessionManager.OpenAsync`/`SshSession.ConnectAsync` and failed with a real,
  unrelated SSH.NET error (that specific profile has no stored credential configured — confirmed via
  the app's own console log showing a clean gate-approve → open-attempt → fail → session-teardown
  sequence, no hang, no crash — a pre-existing data gap on that one profile, not a code bug).
- **Regression-tested**: `McpBridgeServicePipeTests.RoundTrip_CompletesWithoutHanging`
  (`PolyTerm.App.Tests`) required making the pipe name constructor-injectable (`McpBridgeService`
  hardcoded `"polyterm"` before) so a test can run a real instance on a private per-test pipe name
  without colliding with a live app. Verified the test actually catches the regression — reverted the
  fix, re-ran, watched it fail with a clear message (not a hang) after its own timeout, restored the
  fix, watched it pass — the same discipline used for the VNC pixel-format test's own regression check.

### MCP hardening pass: multi-client, rate limiting, audit logging, FramebufferTools

Closed all four remaining MCP spec §4.7 gaps in one pass (see `docs/todos.md`'s MCP section for the
full design writeup). Testing highlights, not repeated there:

- **Multi-client regression test caught its own first draft being wrong.** The first version of
  `McpBridgeServicePipeTests.MultipleClients_CanBeConnectedSimultaneously` only overlapped fast
  round trips end-to-end — and it *passed* even against the old one-accept-loop-at-a-time code, because
  a second client's connect-retry easily outraced the assertion window once the first client's
  short-lived connection freed up. Not a real regression check. Rewrote it to deliberately hold one
  client connected and idle while a second connects with a short timeout — that version correctly
  failed against the old code (temporarily reverted the fix, watched it time out, restored it) before
  being trusted. Documented inline in the test so it doesn't get "simplified" back into the first shape.
- **Rate limiter/audit logger**: unit tests for the sliding-window logic and JSON-line writer in
  isolation, plus integration tests wiring both into the real `McpBridgeService` request pipeline over
  a real pipe — confirms an over-limit call gets a clean JSON-RPC error (not a crash) and that real
  calls produce real audit-log entries.
- **A genuine `Dispatcher.UIThread` hang, found and fixed.** `framebuffer_screenshot`'s PNG encoding
  runs via `Dispatcher.UIThread.InvokeAsync` (matching this codebase's existing discipline of
  marshalling Avalonia bitmap work to the UI thread). The first test for it used plain `[Fact]` and
  hung the entire test run indefinitely — confirmed by checking the stuck process (`Get-Process`,
  still "Responding" but near-zero CPU for minutes) before killing it and diagnosing. No headless
  dispatcher pump means that `InvokeAsync` call waits forever for a UI thread that never processes
  queued work. Fix: `[AvaloniaFact]` instead of `[Fact]` for any test exercising `Dispatcher.UIThread`
  — a pattern worth remembering for any future test in this area.
- **A confirmed `Avalonia.Headless` platform limitation, not a product bug.** After fixing the hang,
  the screenshot test's `image_base64` came back as an empty string rather than real PNG bytes.
  Isolated with a dedicated diagnostic test (`WriteableBitmapHeadlessSaveDiagnosticTests`):
  `WriteableBitmap.Save()` produces zero bytes under `Avalonia.Headless`'s stub rendering backend (no
  real Skia surface backs a `WriteableBitmap` in headless mode) — the exact same production code runs
  fine under the real desktop app's actual Skia-backed renderer. `McpBridgeServiceFramebufferToolsTests`'
  screenshot test was adjusted to assert what headless *can* prove (the call succeeds, reaches
  encoding, reports correct dimensions) rather than real image bytes — same category as every other
  "no GUI automation tool available here" gap in this repo, not something to route around.
- **RDP's new Win32 interop (`CaptureSnapshot`/`SendPointerAsync`/`SendKeyAsync` on
  `INativeHostChannel`) was extracted into a standalone `Win32NativeWindowInput` helper specifically so
  it could be tested** — `RdpNativeHostChannel` itself needs a live MSTSC ActiveX control (a real RDP
  session), but the underlying Win32 mechanics (`PrintWindow`+`PW_RENDERFULLCONTENT` capture,
  `PostMessage` input injection) don't care what kind of window they're pointed at.
  `Win32NativeWindowInputTests` drives the extracted helper against a real, throwaway WinForms window
  — confirming capture dimensions genuinely match the real window's client size, not just that the
  code compiles and doesn't throw. This proves the interop layer is sound; it does not prove the MSTSC
  control itself reacts correctly to injected input.
- **VNC's new `CaptureSnapshot()`** (the framebuffer_screenshot pull path) is verified through the
  same real fake-RFB-server pipeline `VncPixelFormatTests` already used for the pixel-format
  regression — confirms it returns the same real, live-decoded bytes a `RegionUpdated` push just
  delivered, not a separate mock.

### PolyTerm.Mcp and PolyTerm.Cli: first test coverage, needed small refactors first

Both projects had zero test coverage of any kind before this pass, and both needed a small
testability refactor before real coverage was possible:

- **`PolyTerm.Mcp`**: `McpServer` took a concrete, `sealed` `PolyTermClient` (a real named-pipe
  client) as a constructor dependency — no way to substitute a fake without a real pipe. Extracted
  `IPolyTermClient` (one method, `CallAsync`); `PolyTermClient` now implements it, `McpServer` depends
  on the interface. `McpServerTests` then covers the whole JSON-RPC pipeline end-to-end over real
  `TextReader`/`TextWriter` streams (the same API `Program.cs` actually drives via stdin/stdout)
  against a `FakePolyTermClient` — no real pipe or PolyTerm.App instance needed.
- **`PolyTerm.Cli`**: `ConnectCommand`/`ExecCommand`'s connect-failure crash fix originally called
  `Environment.Exit(1)` directly — which would have killed the entire test process if exercised
  in-process, making it fundamentally untestable without spawning a real child process for every test.
  Refactored both to use `System.CommandLine`'s `SetHandler(Func<InvocationContext, Task>)` overload
  and set `InvocationContext.ExitCode` instead — confirmed this overload exists and `ExitCode` is
  settable via reflection over the actual (beta) `System.CommandLine.dll` before relying on it, not
  guessed from memory of the API. Re-verified the real CLI's behavior live after the refactor (bad
  profile id → exit 1 + clean message; real profile → exit 0) to confirm the change was behavior-
  preserving, not just "still compiles." `ConnectCommandTests`/`ExecCommandTests` then cover the fix
  in-process safely; `ProfilesCommandTests` covers `list`/`add`/`delete` against a fake `IProfileStore`.
  Verified both new CLI regression tests actually catch a reintroduced bug (commented out
  `context.ExitCode = 1`, watched both fail, restored it).

### SSH auto-reconnect: closed the last gap in the SSH fixture

`FakeSshServer` gained `StopAsync()`/`RestartOnSamePortAsync()` (kill the real `sshd.exe` process
without deleting its config/host key, then relaunch it identically) — the one piece missing from
§2's earlier SSH work. `AutoReconnect_RecoversAfterServerRestart_AndTheSessionWorksAgainAfterwards`
kills the server mid-session, restarts it on the same port, and confirms `SshSession`'s own backoff
loop (`ReconnectLoopAsync`) notices the drop, retries, and recovers **on its own** — passes in ~3
seconds real time (the first backoff attempt at 2s already finds the server back up). Doesn't stop
at `State == Connected`: re-runs a real `echo <marker>` round-trip afterward, proving the
post-reconnect terminal channel is a genuinely fresh, working one, not just a state flag flipping
back. `RefreshCredentialsAsync` is supplied manually (trivial here — key-file auth reads straight
from `ConnectionProfile.Settings`, not from `CredentialMaterial`) since this test builds
`SessionContext` directly rather than through `SessionManager`.

**Verified the test itself, not just the feature**: temporarily skipped the server restart and
shortened the wait — confirmed the test fails cleanly (a clear assertion message, not a hang) rather
than passing vacuously — before trusting it and restoring both changes.

## 3. Can't be automated in this environment — no tooling available for it

- **MSTSC ActiveX-hosted RDP surface** — `RdpHostSurface` reparents a real OS-native HWND (the
  ActiveX control's own window) into the Avalonia visual tree via raw Win32 `SetParent`. This isn't an
  Avalonia visual at all from `Avalonia.Headless`'s point of view — headless mode has no real window
  to reparent into. FlaUI (§2) could plausibly inspect/drive the *surrounding* Avalonia window (subject
  to the mouse-click gap noted there), but verifying the RDP session actually *shows the right desktop*
  is closer to true visual-regression testing than anything in this repo's toolbox.
- **True pixel-level rendering correctness** for anything drawn to screen (not decoded bytes, which
  `VncPixelFormatTests` now covers) — is a terminal glyph actually legible, is RDP actually
  letterboxed the way the code intends. Needs a dedicated visual-regression tool (screenshot
  pixel-diffing), a meaningfully bigger investment than anything else in this document.
- **Real PNG output from `WriteableBitmap.Save()`** — confirmed this pass (§2) that
  `Avalonia.Headless`'s stub renderer always produces zero bytes, regardless of what's tested. Only a
  real desktop app process (or a future headless-Skia mode, if Avalonia ever ships one) can verify
  actual screenshot image bytes; `Win32NativeWindowInputTests`/`VncPixelFormatTests`'s `CaptureSnapshot`
  coverage is as far as this environment can go — they prove the *pixels going in* are correct, not
  the *PNG bytes coming out*.
- **The MSTSC control's actual reaction to `framebuffer_click`/`type`/`key`'s injected Win32 input** —
  `Win32NativeWindowInputTests` proves the interop layer works against a real window; it can't prove
  the RDP control specifically responds to it correctly without a live RDP session.

## 4. Suggested priority for what's left

1. ~~Folder-inheritance tests, `PolyTerm.App.Tests`, VNC pixel test (+ the bug it found), SSH
   fixture + tunnels/jump-host tests, `Avalonia.Headless` spike + `ConfirmDialog`/`TunnelsViewModel`
   tests, FlaUI feasibility spike, the `ConfirmDialog` button-styling fix, the `MacrosViewModel`
   refactor + tests, the MCP named-pipe hang, MCP multi-client/rate-limiting/audit-
   logging/FramebufferTools, RDP's Win32NativeWindowInput extraction + tests, first-ever
   `PolyTerm.Mcp`/`PolyTerm.Cli` test coverage, SSH auto-reconnect, and the permanent FlaUI test
   project~~ — **all done**, see §1/§2.
2. Click-test the approval-gate `Prompt` dialog itself, the new Pause/Resume recording UI, the SFTP
   chmod menu item, and the new MCP Rate Limit menu item — blocked on the same mouse-click gap noted
   in §2's FlaUI section until/unless that's root-caused; keyboard-only interaction paths remain
   viable for FlaUI in the meantime.
