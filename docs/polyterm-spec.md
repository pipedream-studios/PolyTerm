# PolyTerm — Engineering Specification

**Version:** 0.1 (founding document)
**Status:** Draft for implementation
**License target:** GPL-2.0 or later (inherits spiritual lineage from mRemoteNG; confirm before first release)

---

## 1. Vision

PolyTerm is a cross-platform, tabbed, multi-protocol remote connections manager for Windows, macOS, and Linux — one client for SSH, RDP, VNC, telnet/serial, and HTTP/S sessions, with a hierarchical connection tree, an SFTP sidebar, tunnels, multi-exec broadcast, and X11 forwarding, built on a strict separation between:

- **Front of house** — `PolyTerm.App`, an Avalonia UI (Windows/macOS/Linux) responsible only for presentation and user interaction.
- **Back of house** — `PolyTerm.Engine`, a headless .NET class library responsible for protocols, sessions, connection storage, and credential/key management. The engine has **zero** UI framework references and must compile and run on all three platforms without a display.

The name: *poly* (many) + *term* (terminal/protocol endpoint) — one client, every protocol. Unlike the naming era when the project was called Farhold, PolyTerm deliberately does not "keep the keys" itself (§4.4) — the name reflects protocol breadth, not custody of secrets.

### Design principles

1. **The engine is headless.** Anything that needs a window handle, a render surface, or a UI thread lives in the App layer behind a surface abstraction. If a class in `PolyTerm.Engine` references Avalonia, that is a build error (enforced by project reference rules and a CI check).
2. **Protocols are modules.** Adding protocol N+1 means adding one project that implements the protocol contract and registers itself. No changes to the App, session manager, or storage layer.
3. **Capabilities, not assumptions.** The UI never asks "is this SSH?" It asks "does this session support file transfer / terminal / graphical surface / port forwarding?" via capability flags.
4. **In-process now, process-splittable later.** v1 runs the engine in-process with the App. All engine interfaces are async, event-driven, and serialization-friendly so a gRPC/named-pipe boundary (persistent daemon, session reattach) can be inserted in a future major version without redesign.
5. **Secrets never touch the UI layer in plaintext.** The App requests connections by profile ID; the engine resolves credentials internally at connect time.
6. **Import is a feature.** Day-one importers for mRemoteNG `confCons.xml` and MobaXterm sessions make adoption free.

## 2. Non-goals (v1)

- Embedded X server implementation (X11 *forwarding* is in scope; the server is platform-provided until Phase 5 — see roadmap).
- Mobile or web frontends.
- Acting as a remote-access *server* of any kind.
- Session sharing / team sync / cloud storage (design storage so it can arrive later).
- Scripting/plugin marketplace (design the protocol registry so third-party protocol assemblies are possible later, but do not build loader/sandboxing in v1).
- Windows-only conveniences with no cross-platform analogue (e.g. PuTTY registry session import beyond a best-effort mapping).

## 3. Solution layout

```
PolyTerm/
├── src/
│   ├── PolyTerm.App/                  # Avalonia UI (MVVM, CommunityToolkit.Mvvm)
│   ├── PolyTerm.App.Surfaces/         # Avalonia implementations of surface contracts
│   │   ├── Terminal/                 #   terminal control (renders ITerminalSession)
│   │   ├── Framebuffer/              #   WriteableBitmap surface for RDP/VNC
│   │   └── NativeHost/               #   NativeControlHost escape hatch (per-OS)
│   ├── PolyTerm.Engine/               # headless core: sessions, storage, credential providers, registry
│   ├── PolyTerm.Mcp/                  # MCP server — third consumer of the engine (§4.7)
│   ├── PolyTerm.Terminal.Emulation/   # VT/xterm state machine (headless, no rendering)
│   ├── PolyTerm.Protocols.Ssh/        # SSH.NET: shell, exec, SFTP, SCP, tunnels, X11 fwd
│   ├── PolyTerm.Protocols.Telnet/     # + raw TCP and rlogin variants
│   ├── PolyTerm.Protocols.Serial/     # System.IO.Ports
│   ├── PolyTerm.Protocols.LocalShell/ # ConPTY (Windows) / openpty (Unix)
│   ├── PolyTerm.Protocols.Rdp/        # FreeRDP native bindings
│   ├── PolyTerm.Protocols.Vnc/        # managed VNC client
│   ├── PolyTerm.Protocols.Web/        # HTTP/S sessions (external browser or webview)
│   ├── PolyTerm.Importers/            # mRemoteNG, MobaXterm, PuTTY (best effort)
│   └── PolyTerm.Cli/                  # `PolyTerm` — headless proof + scripting (thin)
├── native/                           # vendored native builds (FreeRDP) per RID
├── tests/
│   ├── PolyTerm.Engine.Tests/
│   ├── PolyTerm.Terminal.Tests/       # escape-sequence conformance corpus
│   └── PolyTerm.Protocols.IntegrationTests/  # docker-compose lab (see §12)
└── docs/
```

Target framework: **.NET 10 LTS** across all projects (migrated from .NET 8 LTS — every dependency in the graph, including the WinForms/MSTSC RDP interop and the real FlaUI GUI-automation suite, restored/built/ran clean with zero compatibility breaks; the only build fallout was a newer, stricter analyzer rule (`CA1873`) suppressed for the same reasoning `CA1848` already was). C# 14, nullable enabled, `TreatWarningsAsErrors`.

`PolyTerm.Cli` exists from M0 not as a product priority but as an architectural enforcement mechanism: if a feature can't be exercised from the CLI, the engine isn't actually headless.

## 4. Core contracts

These interfaces live in `PolyTerm.Engine.Abstractions` (single small assembly that protocol modules reference; nothing else leaks). Sketches below are normative in shape, not final in every signature.

### 4.1 Protocol contract

```csharp
public interface IProtocolDescriptor
{
    string Id { get; }                    // "ssh", "rdp", "vnc", "serial", ...
    string DisplayName { get; }
    int DefaultPort { get; }              // 0 = not applicable (serial, local)
    ProtocolCapabilities Capabilities { get; }
    SurfaceKind Surface { get; }          // what the App must provide to show it
    SettingsSchema Settings { get; }      // §4.5 — drives the auto-generated UI
    IProtocolSession CreateSession(SessionContext ctx);
}

[Flags]
public enum ProtocolCapabilities
{
    None            = 0,
    Terminal        = 1 << 0,   // produces a byte stream for a VT emulator
    FileTransfer    = 1 << 1,   // exposes IFileTransferChannel
    GraphicalDesktop= 1 << 2,   // produces framebuffer updates
    PortForwarding  = 1 << 3,   // local/remote/dynamic tunnels
    X11Forwarding   = 1 << 4,
    Exec            = 1 << 5,   // one-shot remote command
    Reconnect       = 1 << 6,   // supports transparent reconnection
    InputBroadcast  = 1 << 7,   // safe target for multi-exec
    Recording       = 1 << 8,   // session I/O can be captured to log
}

public enum SurfaceKind { Terminal, Framebuffer, NativeHost, External, None }
```

```csharp
public interface IProtocolSession : IAsyncDisposable
{
    Guid SessionId { get; }
    SessionState State { get; }           // Created → Connecting → Connected → Closing → Closed | Faulted
    event EventHandler<SessionStateChangedEventArgs> StateChanged;
    event EventHandler<SessionErrorEventArgs> ErrorOccurred;

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();

    // Capability accessors — return null when the capability flag is absent.
    ITerminalChannel? Terminal { get; }
    IFileTransferChannel? FileTransfer { get; }
    IFramebufferChannel? Framebuffer { get; }
    ITunnelManager? Tunnels { get; }
}
```

```csharp
public interface ITerminalChannel
{
    event EventHandler<ReadOnlyMemory<byte>> OutputReceived;  // raw bytes, engine thread
    ValueTask SendInputAsync(ReadOnlyMemory<byte> data);
    ValueTask ResizeAsync(int cols, int rows);                // e.g. SSH window-change
    string TerminalType { get; }                              // "xterm-256color"
}

public interface IFramebufferChannel
{
    FramebufferFormat Format { get; }                          // BGRA32 etc.
    event EventHandler<FramebufferRegionUpdated> RegionUpdated;
    ValueTask SendPointerAsync(PointerEvent e);
    ValueTask SendKeyAsync(KeyEvent e);
    ValueTask ResizeAsync(int width, int height);
    ValueTask SendClipboardAsync(ClipboardPayload payload);
    event EventHandler<ClipboardPayload> ClipboardReceived;
}

public interface IFileTransferChannel
{
    Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string path, CancellationToken ct);
    Task DownloadAsync(string remote, Stream local, IProgress<TransferProgress> p, CancellationToken ct);
    Task UploadAsync(Stream local, string remote, IProgress<TransferProgress> p, CancellationToken ct);
    Task DeleteAsync(string remote, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);
    Task MkDirAsync(string path, CancellationToken ct);
    // Permissions/chmod exposed where the protocol supports it (SFTP).
}
```

**Threading rule:** all engine events fire on engine threads. Surfaces in `PolyTerm.App.Surfaces` own the marshalling to the Avalonia UI thread, with batching (terminal output coalesced on a ~10–16 ms flush; framebuffer regions coalesced per frame). The engine never calls `Dispatcher`.

### 4.2 Session manager

```csharp
public interface ISessionManager
{
    Task<IProtocolSession> OpenAsync(Guid profileId, CancellationToken ct);
    IReadOnlyCollection<IProtocolSession> ActiveSessions { get; }
    event EventHandler<SessionEventArgs> SessionOpened;
    event EventHandler<SessionEventArgs> SessionClosed;
    Task BroadcastInputAsync(ReadOnlyMemory<byte> data, IEnumerable<Guid> sessionIds); // multi-exec
}
```

The session manager resolves the profile (with inheritance), resolves credentials via the active `ICredentialProvider` (§4.4), selects the protocol from the registry, and owns session lifetime. The App holds sessions only by interface.

### 4.3 Connection profiles & storage

```csharp
public sealed class ConnectionProfile
{
    public Guid Id { get; init; }
    public Guid? ParentId { get; init; }          // folders form a tree
    public bool IsFolder { get; init; }
    public string Name { get; set; }
    public string ProtocolId { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public CredentialReference? Credential { get; set; }  // {ProviderId, ItemRef} — never inline secrets
    public Dictionary<string, JsonElement> Settings { get; set; }   // schema-validated per protocol
    public InheritanceMask Inherit { get; set; }  // per-property "defer to parent"
}
```

- Storage backend v1: single **SQLite** database (`PolyTerm.db`) in the platform config dir (`%APPDATA%`, `~/Library/Application Support`, `~/.config`). SQLite over JSON for atomic writes, painless migrations, and a future sync story. A `PolyTerm export` / `import` round-trips the tree as JSON for portability and VCS-friendly backup.
- **Inheritance:** any property may defer to the parent folder, resolved at connect time. This is a first-class feature of the profile tree, not an importer shim.
- Profile `Settings` are validated against the protocol's `SettingsSchema`, so unknown protocols degrade gracefully instead of corrupting.

### 4.4 Credential providers: no owned vault

**Decision, reversing the original spec draft:** PolyTerm does not implement its own encrypted credential store. Secrets live in a password manager the user already runs — KeePass or Bitwarden — or, as a zero-dependency fallback, the OS secret store. PolyTerm never becomes a second place secrets are kept.

Rationale: an owned vault (AES-GCM blobs, Argon2id KDF, PPK conversion, agent-protocol implementation) was the single largest concentration of security-critical bespoke code in the original design, and duplicating a secret store most users already run is worse hygiene, not better. It also matters directly for §4.7 (MCP): if the engine never persists plaintext-adjacent secrets itself, credential material never has to cross the MCP boundary at all — an agent can be told "connect to profile X" without any tool existing that can hand back a password.

```csharp
public interface ICredentialProvider
{
    string ProviderId { get; }                 // "keepass", "bitwarden", "os-keychain"
    Task<bool> IsAvailableAsync();              // is the backing app running/unlocked?
    Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct);
    Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct); // metadata only, for the picker UI
}

public readonly record struct CredentialReference(string ProviderId, string ItemRef);

// Returned only to the engine's own auth-chain code — never serialized, never logged, never crosses the MCP boundary.
public sealed class CredentialMaterial : IDisposable
{
    public string? Username { get; init; }
    public ReadOnlyMemory<byte>? Secret { get; init; }        // password or passphrase, zeroed on Dispose
    public ReadOnlyMemory<byte>? PrivateKey { get; init; }     // OpenSSH-format key material, zeroed on Dispose
    public void Dispose() { /* zero all buffers */ }
}
```

- **`KeePassProvider`** — talks to a running KeePass instance (KeePassRPC/KPScript-style local IPC) rather than reading `.kdbx` directly, so PolyTerm never implements database unlock or the KDBX crypto format itself. If KeePass isn't running or the database is locked, `IsAvailableAsync()` returns false and the App prompts the user to unlock it — PolyTerm never caches a copy of the unlocked state.
- **`BitwardenProvider`** — talks to the Bitwarden CLI in `bw serve` mode (local REST API, session-key-gated) or the desktop app's native messaging host. Same availability/locked semantics.
- **`OsKeychainProvider`** — DPAPI / macOS Keychain / libsecret-kwallet, used for (a) people with no external vault, and (b) storing the session unlock key/token for a KeePass/Bitwarden provider if the user opts in to reduce re-prompting.
- **SSH key files and agents are unaffected by this change** — an SSH credential can still point at a key file on disk or resolve via an SSH agent (§5.1); the credential provider is only in the loop for passwords/passphrases, not for the agent protocol itself.
- **Host key store stays in PolyTerm's own SQLite** — it's a trust ledger, not a secret, so it doesn't belong in KeePass/Bitwarden. TOFU model via `IHostKeyPolicy`; fingerprints (SHA-256), first-seen, change-detection; the App supplies the accept/reject prompt; changed keys are never auto-accepted.
- **Memory hygiene unchanged in spirit:** `CredentialMaterial` buffers are `byte[]`/`Span<byte>`, zeroed on dispose, never placed in a `string`, never logged. The scrubbing filter on the logger stays a hard requirement (§11).
- **No provider available:** if KeePass/Bitwarden aren't running and OS-keychain has nothing stored, the App prompts inline for a one-off credential (not persisted) — PolyTerm remains usable with zero configured provider, just less convenient.

### 4.5 Settings schema (the extensibility keystone)

Each protocol publishes a `SettingsSchema`: a typed description of its options (name, type, default, group, validation, display hints, capability/OS conditions). The App renders the connection property grid *from the schema* — meaning a new protocol gets a complete settings UI without touching `PolyTerm.App`. The schema also drives serialization validation and importer mapping. This single decision is what makes "add new protocols easily" true rather than aspirational.

### 4.6 Protocol registry

```csharp
public interface IProtocolRegistry
{
    IReadOnlyList<IProtocolDescriptor> All { get; }
    IProtocolDescriptor? Get(string protocolId);
}
```

v1: protocols register via DI at startup (each module exposes `services.AddPolyTermSsh()` etc.). The registry is the future seam for out-of-tree protocol assemblies; do not build assembly scanning in v1.

### 4.7 MCP surface — third consumer of the engine

The GUI/engine split exists for this as much as for cross-platform reach: an MCP server is a **third consumer of `PolyTerm.Engine`**, architecturally a peer of `PolyTerm.App` and `PolyTerm.Cli`, not a feature bolted onto either. This is the direct answer to Conduit (§0 background): same "let an agent drive your infrastructure" pitch, but self-hosted, no daily call cap, no subscription, and a strictly narrower secret-exposure surface because of §4.4.

```
PolyTerm.Mcp/                # new project, references Engine.Abstractions + Engine only
├── McpServer.cs              # stdio or local-socket MCP transport
├── Tools/
│   ├── ConnectionTools.cs    # list/open/close sessions
│   ├── TerminalTools.cs      # execute, read-buffer, send-keys
│   ├── FramebufferTools.cs   # screenshot, click, type, key, resize (RDP/VNC)
│   └── TransferTools.cs      # sftp list/upload/download
└── Approval/
    └── IApprovalGate.cs      # human-in-the-loop for write/risky tools
```

**Design rules, tighter than Conduit's:**

1. **No credential tools, ever.** There is no `CredentialRead` MCP tool and there never will be — the engine resolves credentials internally inside `ISessionManager.OpenAsync`, using whatever `ICredentialProvider` the profile references. An agent can ask to open a connection; it cannot ask for what's inside it. This is strictly stronger than Conduit's "credential read requires approval" model, because there is no code path at all, not just a gate.
2. **Capability-scoped tools, not protocol-scoped.** Mirrors §1 design principle 3: `TerminalTools.Execute` works against any session whose `IProtocolSession.Terminal` is non-null (SSH, serial, telnet, local shell) rather than being SSH-specific. New protocols automatically gain MCP terminal/file-transfer tool support for free.
3. **Approval gate on anything state-changing.** `IApprovalGate` sits in front of session-open, terminal-send, framebuffer-input, and file-write tools; the App renders the approval prompt (risk-leveled, matching the industry pattern we saw in Conduit's dev-community writeup) when running with a UI, or the CLI prompts interactively, or — for unattended/automation use — a policy file (allow/deny patterns per tool, fail-closed) can pre-authorize, matching the `mcp-ssh-manager` restricted-mode pattern rather than inventing a new one.
4. **Rate limiting is a local policy knob, not a monetization lever.** Config-file `[mcp] max_calls_per_minute` defaulting to something generous; no artificial daily cap.
5. **Every tool call logged** (timestamp, tool, profile, result) to the same structured log pipeline as everything else, through the same secret-scrubbing filter (§11).
6. **Transport:** local-only in v1 (stdio or a Unix socket / named pipe, matching Conduit's approach) — no network-exposed MCP endpoint until there's an actual multi-machine use case and an auth story to match.

MCP lands at **M1**, immediately after the SSH module is solid (§13) — it is a first-class milestone deliverable, not a stretch goal.

## 5. Protocol modules — implementation strategy

| Protocol | Library / approach | Surface | Capabilities (key) | Phase | Risk |
|---|---|---|---|---|---|
| SSH | SSH.NET (Renci) | Terminal | Terminal, FileTransfer (SFTP/SCP), PortForwarding, X11Forwarding, Exec, InputBroadcast, Recording, Reconnect | M0 | Low |
| SFTP browser | same SSH.NET connection | (panel, no own surface) | FileTransfer | M1 | Low |
| Local shell | ConPTY (Win) / forkpty (Unix) | Terminal | Terminal, InputBroadcast, Recording | M1 | Low-Med |
| Telnet / raw / rlogin | own implementation (trivial protocols) | Terminal | Terminal, InputBroadcast, Recording | M2 | Low |
| Serial / COM | System.IO.Ports | Terminal | Terminal, Recording | M2 | Low |
| VNC | MarcusW.VncClient (managed, Avalonia-friendly) — validate; fallback: implement RFB core (well-documented protocol) | Framebuffer | GraphicalDesktop | M2 | Med |
| RDP | FreeRDP 3.x via custom thin P/Invoke bindings; vendored native builds per RID (win-x64, linux-x64, osx-arm64, osx-x64) | Framebuffer | GraphicalDesktop, Reconnect | M2–M3 | **High** |
| HTTP/S | external browser by default; optional embedded webview later | External | None | M2 | Low |
| X11 (forwarded apps) | SSH channel + platform X server (see §9) | External | X11Forwarding | M4 | Med |
| Mosh | native lib binding or managed implementation — investigate | Terminal | Terminal, Reconnect | M4+ | Med |

### 5.1 SSH module (flagship — sets the pattern)

- One `SshClient` per session; `ShellStream` for the terminal channel (`xterm-256color`, correct cols/rows at open, `window-change` on resize).
- SFTP and SCP channels multiplex over the same connection; the SFTP sidebar (§8) attaches automatically to any connected SSH session.
- Tunnels: local (`-L`), remote (`-R`), dynamic/SOCKS (`-D`) via `ITunnelManager`; profiles can declare tunnels that auto-start on connect.
- Auth chain per §4.4; keyboard-interactive supported; host key policy enforced before auth.
- Keepalive default 30 s; optional auto-reconnect with backoff (capability `Reconnect`).
- Jump hosts / bastion chaining (`ProxyJump` equivalent) — profile references another SSH profile as its hop; engine composes the chain. (M3)
- Known limitation to engineer around: SSH.NET has no built-in agent client — implement the agent protocol client in `PolyTerm.Protocols.Ssh` (it is small: SSH_AGENTC_REQUEST_IDENTITIES / SIGN_REQUEST over the socket/pipe).

### 5.2 RDP module (highest risk — contain it)

- Bind FreeRDP 3 (`libfreerdp`, `winpr`) with a deliberately *thin* C ABI shim (`native/PolyTerm-rdp-shim`) exposing: create/configure/connect, framebuffer callback (dirty rects into shared buffer), input injection, clipboard, resize, disconnect. Bind the shim, not FreeRDP's full API — insulates from upstream churn.
- CI builds FreeRDP + shim per RID; binaries vendored as NuGet-style runtime packs under `native/`.
- Feature ladder: connect/display/input → clipboard → dynamic resolution → NLA/CredSSP everywhere → audio → drive redirection. Ship each rung when stable.
- **Containment rule:** RDP is capability-gated and pack-optional. If a platform's native pack is missing or broken, PolyTerm runs with RDP greyed-out and says why. RDP slipping a milestone must never block a release (explicit product decision from project owner).

### 5.3 Adding protocol N+1 (the checklist that must stay short)

1. New project `PolyTerm.Protocols.X` referencing only `PolyTerm.Engine.Abstractions`.
2. Implement `IProtocolDescriptor` + `IProtocolSession` (+ relevant channel interfaces).
3. Declare `SettingsSchema` — the App's property grid renders itself.
4. `services.AddPolyTermX()` in composition root.
5. Integration test against a containerized server in the docker lab.

If step counts grow beyond this, the abstraction has failed — fix the abstraction, not the docs.

## 6. Terminal emulation (`PolyTerm.Terminal.Emulation`)

The single largest piece of owned engineering. Decision: **native managed emulator + native Avalonia renderer.** No WebView/xterm.js — a Chromium dependency per platform contradicts the lightweight cross-platform goal, and Avalonia's webview story is weak. This is the opposite call to the earlier mRemoteNG-fork plan (where WebView2 was free on Windows); different constraints, different answer.

- **Headless state machine** (`PolyTerm.Terminal.Emulation`): DEC-compatible parser (Paul Williams state machine), 2-D cell grid with scrollback, SGR incl. 256-color and truecolor, cursor addressing, alternate screen, tab stops, margins, origin/autowrap modes, bracketed paste, mouse reporting (X10/SGR), OSC title/clipboard (52, gated by setting), UTF-8 with wide-char (CJK) cells. Explicit conformance target: **vim, htop, tmux, mc, nano render correctly**; validated with vttest + esctest subsets and a recorded-session snapshot corpus in CI.
- **Renderer** (`PolyTerm.App.Surfaces.Terminal`): Avalonia custom control drawing the grid (glyph-run caching), selection & clipboard, URL detection, search, font/theme config, ligature support deferred.
- The emulator consumes `ITerminalChannel` and is protocol-agnostic: SSH, serial, telnet, local shells all share it.
- Survey existing managed emulators first (e.g. vtnetcore lineage) for a head start, but assume ownership: this component must be ours to maintain.

## 7. Engine services (cross-cutting)

- **Logging/recording:** per-session raw I/O capture (opt-in, path templates, secrets never logged pre-auth), plus structured app logs (no secret material — scrubbing filter mandatory).
- **Macros & snippets:** named text blocks with variables, send-to-session or broadcast. (M3)
- **Multi-exec:** broadcast bar targets all sessions flagged `InputBroadcast`; visual armed-state so nobody broadcasts `reboot` by accident. Confirmation on destructive-looking first use.
- **Import:** mRemoteNG `confCons.xml` (full tree, inheritance flags, encrypted passwords given the master password — its AES-GCM scheme is documented), MobaXterm sessions file (best-effort mapping), PuTTY sessions (registry/file, best-effort).
- **Quick connect:** parse `user@host:port`, protocol inferred or picked, ephemeral profile promotable to saved.

## 8. Application (front of house) — v1 shape

- Avalonia 11.x, MVVM via CommunityToolkit.Mvvm; docking/tabbed layout (evaluate Dock.Avalonia) with: session tabs (split-view capable), connection tree sidebar, **SFTP sidebar auto-attached to SSH sessions**, notifications panel, broadcast bar, quick connect.
- Property grid generated from `SettingsSchema` (§4.5); shared credential picker browses whichever `ICredentialProvider`s are available (§4.4); host-key and passphrase prompts are App-rendered dialogs driven by engine callbacks.
- Theming light/dark; keyboard-first navigation; every session action available via command palette (Ctrl+Shift+P) — cheap in MVVM, huge for power users.
- The App contains no protocol logic. Litmus test: `PolyTerm.Cli` can open an SSH session and pipe I/O with the App uninstalled.

## 9. X11 forwarding and bundled X servers

Phased honestly:

- **M4 — X11 forwarding:** SSH `X11Forwarding` capability implemented in the SSH module (channel handling, XAUTH cookies). Display served by platform X servers: Linux native; macOS XQuartz (detect, deep-link install instructions); Windows any running X server (VcXsrv et al.) — PolyTerm detects, configures `DISPLAY`, and documents.
- **M5 — bundled X server (Windows):** evaluate shipping a maintained VcXsrv fork as an optional component with lifecycle managed by PolyTerm (auto-start, port allocation). Writing an X server is out of scope permanently; bundling one is a packaging problem, which is winnable.

## 10. Packaging & CI

- GitHub Actions matrix: build/test on windows-latest, ubuntu-latest, macos-latest; native FreeRDP pack builds per RID; artifact per platform on every main merge.
- Distribution: Windows MSI + winget + portable zip; macOS notarized .dmg (universal or per-arch); Linux AppImage + .deb/.rpm, Flatpak candidate post-v1.
- Single-file publish where practical; trimming deferred until reflection surface (schema UI, DI) is audited.

## 11. Security posture (summary of hard rules)

1. **PolyTerm stores no secrets of its own.** No owned vault, no bespoke crypto for credential-at-rest storage (§4.4). Credentials live in KeePass, Bitwarden, or the OS keychain; PolyTerm holds only `{ProviderId, ItemRef}` pointers in its SQLite store, which are useless without the provider unlocked.
2. **`CredentialMaterial` is transient and zeroed.** Resolved secrets exist only as `byte[]`/`Span<byte>` for the lifetime of an auth handshake, are never written to disk, never logged, and are disposed (zeroed) immediately after use.
3. **No plaintext secrets in logs, exports, or crash reports.** Exports (`PolyTerm export`) carry `CredentialReference` pointers only — a shared export file is inert without the recipient's own KeePass/Bitwarden access.
4. **MCP never exposes a credential-read tool** (§4.7 rule 1) — this is a hard architectural constraint, not a configurable policy.
5. Host key changes are loud, blocking, and never auto-accepted.
6. Native interop (FreeRDP) treated as untrusted input surface: shim validates all buffer geometry; fuzz the framebuffer path.
7. Dependency policy: pin versions, Dependabot/Trivy in CI (owner's home turf), SBOM published per release.

## 12. Testing strategy

- **Unit:** engine logic, inheritance resolution, credential-provider resolution (mocked KeePass/Bitwarden/keychain backends), schema validation.
- **Terminal conformance:** vttest/esctest-derived corpus + snapshot tests of recorded byte streams → grid state (catches regressions cheaply).
- **Integration lab:** docker-compose with openssh-server, telnetd, xrdp, tigervnc, sftp fixtures; protocol tests run against the lab in CI (Linux) and on-demand locally. Serial tested via pty loopback + socat.
- **Cross-platform smoke:** headless CLI connect/exec/transfer on all three OS runners per merge.

## 13. Roadmap

| Milestone | Contents | Exit criterion |
|---|---|---|
| **M0 — Keel** | Solution skeleton, engine + abstractions, credential providers (OS-keychain + KeePass, host-key store), SQLite store + inheritance, SSH module (terminal, auth chain, keepalive), terminal emulator core, minimal App (tree, tabs, one working SSH session), CLI | Daily-drivable for SSH by the project owner |
| **M1 — Hold** | SFTP sidebar + transfers, local shell (ConPTY/pty), Bitwarden provider, mRemoteNG importer, quick connect, session logging, **MCP server (`PolyTerm.Mcp`) with terminal + connection + transfer tools and approval gate** | Daily-drivable for full SSH workflows; agent-drivable via MCP |
| **M2 — Gates** | Telnet/raw/serial, VNC, RDP first rung (connect/display/input, win+linux), HTTP/S launcher, MobaXterm importer | All core protocols usable |
| **M3 — Walls** | Tunnels UI + auto-start, jump hosts, multi-exec broadcast, macros/snippets, auto-reconnect | Daily-drivable for terminal-centric work |
| **M4 — Reach** | X11 forwarding (platform servers), Mosh investigation, remote-file edit-in-local-editor round-trip, command palette polish, RDP resize/NLA/audio/drive rungs | Feature-complete for terminal + graphical workflows, excl. bundled X server |
| **M5 — Beacon** | Bundled Windows X server (packaging), Flatpak, protocol SDK docs, perf/memory pass, v1.0 | Public release |

**RDP note (M2):** the Windows rung shipped via the OS's own MSTSC ActiveX control (`mstscax.dll`,
`SurfaceKind.NativeHost`), not the FreeRDP shim §5.2 originally described — no native build
toolchain was available to stand up FreeRDP at the time. Linux/macOS are capability-gated
("unavailable", reason shown) pending a FreeRDP-based cross-platform shim, tracked as its own
future milestone rather than blocking M2's Windows rung. See `docs/todos.md` for scope/rough edges.

**M2 status:** all five items shipped (Telnet/raw/rlogin own implementation; Serial via
`System.IO.Ports`; VNC via `MarcusW.VncClient`, the first real `SurfaceKind.Framebuffer` consumer;
HTTP/S launcher; MobaXterm `.mxtsessions` importer, best-effort against community-documented format).
RDP is Windows-only per the note above. Telnet/Serial/MobaXterm were verified against real or
purpose-built test harnesses (not real telnetd/rlogind/MobaXterm installs — none were available);
VNC was originally verified structurally only (reflection-checked API calls, clean build), but a
later pass added a real end-to-end test against a hand-rolled fake RFB server
(`docs/testing.md`) that **found and fixed a genuine pixel-format bug** — colors would have rendered
completely scrambled, not just channel-swapped. Still not click-tested against a *real* VNC server,
just the decode path proven correct. See `docs/todos.md` for the per-protocol detail on what's
confirmed vs. still open.

**M1 approval-gate note:** §4.7's `IApprovalGate` (design rule 3) is now built — not under
`PolyTerm.Mcp/Approval/` as originally sketched (all real tool logic lives in `PolyTerm.App`'s
`McpBridgeService`, not the thin stdio transport project, so that's where the gate lives too:
`src/PolyTerm.App/McpBridge/Approval/`). Gates `connection_open` (new — the session-open tool the
spec called for but never shipped) plus `terminal_execute`/`terminal_send_keys`/`sftp_write`/
`sftp_delete`/`sftp_rename`, per a policy resolved per-connection first, then a global default, then
`Prompt` if neither is set. Rate limiting and structured audit logging (rules 4-5) are not part of
this — see `docs/todos.md`. **Also found while verifying this**: the named-pipe MCP bridge itself
didn't complete a round trip at all, a pre-existing bug (reproduced on unmodified code) not
introduced by this work — **since fixed** (a BOM-preamble/`StreamWriter.Flush` deadlock, one line
per side) and confirmed live against the real `polyterm-mcp.exe`: `connection_list`,
`connection_open` under both `Deny` and `Allow` policies, reaching real session-connect logic. Full
detail in `docs/todos.md` and `docs/testing.md`.

**M3 status: done.** Tunnels, jump hosts, multi-exec broadcast, macros/snippets, and auto-reconnect
all shipped. Tunnels and jump-host chaining now have real automated end-to-end coverage against
actual ad-hoc SSH servers (`docs/testing.md`) — a real tunnel forwarding traffic, and a real
multi-hop bastion connection — though the *UI* for both (the New Connection dialog's Tunnels editor
and Jump Host picker) is still only click-testable, not automated. Macros/auto-reconnect remain
fully manual (see `docs/todos.md`'s manual-testing punch list). RDP "clipboard+resize+NLA breadth"
(the roadmap's original M3 wording) was **not M3 scope**: clipboard sync already works, confirmed by
user click-test — it comes from the MSTSC ActiveX control's own built-in virtual channel, not any
PolyTerm code. Resize/NLA/audio/drive depth work moved to M4's RDP line instead, since it didn't fit
M3's "terminal-centric daily-driver" exit criterion.

**M4 status: done.** RDP's remaining depth work landed incrementally. Done so far:
certificate-trust UI (`AuthenticationLevel = 2`, the control's own native accept/reject dialog — no
managed API exists to intercept the certificate or build a PolyTerm-styled prompt, confirmed via
reflection over the interop assembly); drive redirection first rung (`redirectDrives`, all-or-nothing
— no per-drive picker, since the typelib's `IMsRdpDriveCollection` isn't reachable through the
`AxHost` wrapper's exposed properties); audio redirection (`audioMode`: Local/Remote/Disabled,
plus a `redirectMicrophone` toggle); a tab-level indicator (`SessionTabViewModel.LiveResizeUnsupported`) now
surfaces when a server doesn't support live Dynamic Resolution Update, so the existing letterboxing
fallback is at least visible rather than silent. Resize itself is still the M2-level best-effort rung
(DRU where the server supports RDP 8.1+, letterboxing otherwise) — no new resize *mechanism*, just
visibility into which one is active. **RDP's M4 depth work is essentially done.**

**X11 forwarding** (`PolyTerm.Protocols.Ssh/X11/`) also landed this pass — the SSH module now sends
"x11-req" (before "shell", so `DISPLAY` auto-exports into the remote shell) and bridges inbound X11
channels to a local X server, gated behind a new `x11Forwarding` profile setting. This needed
reflection into SSH.NET internals (no public API exists for it — confirmed via reflection over the
real assembly), used only when X11 forwarding is explicitly enabled so it carries no regression risk
for ordinary SSH sessions. Confirmed live against a real ad-hoc `sshd.exe`: channel creation and the
full pty-req/x11-req/shell-req sequence work, and a real command's output round-trips correctly
through the reflected channel. The inbound-channel-to-local-X-server bridge and real xauth cookie
substitution are implemented but **not verified end to end** — no Linux/macOS sshd (X11 forwarding
needs server-side support Windows' own OpenSSH doesn't have), local X server, or X11 client app were
available in this environment. See `docs/todos.md`'s SSH X11 section for the full verification
breakdown, including a real, unresolved test-infrastructure gap (a happy-path test crashes `dotnet
test`'s test host process specifically, not the feature code itself, confirmed via a standalone
demonstration outside the test host).

**Mosh investigation is also done** (per the roadmap's own scoping — investigation, not
implementation): no .NET/C# Mosh implementation exists anywhere to build on, `mosh-server` must be
installed on the remote host outside PolyTerm's control regardless of what ships client-side, and no
native Windows Mosh client exists at all (the reference implementation is deeply POSIX-oriented; WSL/
Cygwin are the only ways to run it on Windows today). Recommendation: defer — a real managed
reimplementation of Mosh's UDP State Synchronization Protocol (including AES-128-OCB3, not a built-in
.NET cipher mode) is a dedicated project of its own, disproportionate to incremental M4 depth work;
shelling out to an already-installed external client is the only proportionate option if this is ever
actually prioritized. See `docs/todos.md`'s Mosh section for the full write-up.

**Remote-file edit-in-local-editor round-trip is also done**: the SFTP sidebar's new "Edit…" action
downloads a file to a temp copy, opens it with the OS's default handler for that file type, and
re-uploads it back to the remote path on every save (watched via a real `FileSystemWatcher`, debounced).
Matches WinSCP/FileZilla's own "Edit" workflow. Covered by 4 real tests (real temp-file I/O and a real
filesystem watcher, not mocked) — not click-tested against an actual external editor application, same
GUI-automation limitation as the rest of this file.

**Command palette is also done** — and was actually a from-scratch build, not "polish": despite the
roadmap's wording and §8's description of it, no command palette existed anywhere in the codebase
before this pass. Ctrl+Shift+P (or `View → Command Palette…`) now opens a fuzzy-searchable list of 18
app-level commands (new connection, quick connect, local shell, SFTP toggle, recording controls,
macros, both importers, themes, MCP policy/rate-limit, broadcast toggle, close tab), reusing the exact
same click handlers already wired to the menu/ribbon so it can't drift out of sync with what a menu
item actually does. 17 tests cover the pure fuzzy-match ranking and the search/selection/confirm state
machine. Not click-tested — same GUI-automation limitation as the rest of the App; confirmed the app
builds and launches cleanly with it wired in.

**M4 is now complete.** See `docs/todos.md` for full detail on what's confirmed live vs. structurally
implemented but unverified (the X11 inbound bridge and the command palette's actual open/search/run
flow both fall in the latter category, for the reasons detailed there).

**M5 status (started):** the GitHub Actions CI matrix (§10) is now authored (`.github/workflows/ci.yml`
— no `.github` directory existed at all before this). A `windows` job builds/tests the full solution;
a `portable` job (matrix: ubuntu-latest, macos-latest) builds/tests a new `PolyTerm.Portable.slnx`
covering everything except the four inherently-Windows-only projects (`PolyTerm.App`,
`PolyTerm.Protocols.Rdp`, and their respective test projects), proving the "`PolyTerm.Cli` works with
the App uninstalled" portability claim actually holds. Real bug found and fixed along the way:
`PolyTerm.slnx` was silently missing three test projects (`Cli.Tests`/`Mcp.Tests`/`FlaUI.Tests`) —
added in an earlier session but never wired into the solution file. Every command in the workflow was
run locally against this exact repo state and confirmed to pass (224 tests); the workflow itself has
not been run on real GitHub-hosted runners yet (this environment can't trigger or observe that without
pushing) — see `docs/todos.md`'s CI section for exactly what that means for the two untested legs.
**Windows portable-zip packaging is also done**: `packaging/windows/build-portable-zip.ps1` publishes
`PolyTerm.exe` and `cli/polyterm.exe` self-contained (win-x64), zips the result, and was verified for
real — not just built — by extracting the produced zip to a clean directory and running both
executables from there. Found a real bug in the process: publishing the App and CLI into the same
folder silently corrupts the App, since `PolyTerm.exe`/`polyterm.exe` collide on Windows'
case-insensitive filesystem (`AssemblyName`s `PolyTerm` vs `polyterm`) — fixed by publishing the CLI
into a `cli/` subfolder. `Directory.Build.props` now carries real version/company/product metadata
(`0.9.0`, a placeholder pre-1.0 development version) shared by every project. See `docs/todos.md`'s
packaging section for the full verification breakdown.

**MSI is also done**: `PolyTerm.wxs` + `build-msi.ps1` (WiX v5+ syntax, publishes App+CLI the same way
the portable zip does, harvests every file via a glob) build a real, working MSI — the WiX Toolset v7
"Open Source Maintenance Fee" EULA question was put to the user directly (asked, not auto-accepted;
$10/month, waived under $10k/yr revenue) after they asked whether the release pipeline could build the
RPM/DMG/MSI, and they chose to resolve it and wire MSI in. `wix build` compiled cleanly on the first
real attempt; `-SmokeTest` (administrative extract via `msiexec /a`, no real system-state changes)
confirms the payload launches. A real install/uninstall cycle was deliberately not run without
separate sign-off, since that modifies actual system state.

**`.github/workflows/release.yml` turns both packaging scripts into a real pipeline**, per the user's
explicit request: tag push (`v*.*.*`) reruns full CI, builds+smoke-tests both the portable zip and the
MSI in parallel, then creates a GitHub Release with both attached via `gh release create`.
`workflow_dispatch` allows a dry run that stops before publishing. Not run on a real GitHub-hosted
runner yet — no git remote is configured on this repo.

**RPM/DMG are not buildable yet, for a reason beyond the pipeline itself**: `PolyTerm.App` (the desktop
GUI) is currently hard-locked to `net10.0-windows` because it references `PolyTerm.Protocols.Rdp`
(needs WinForms/MSTSC ActiveX), and a plain `net10.0` project can't reference a `net10.0-windows` one.
This is a real but bounded fix (multi-target the App, gate the RDP reference to Windows-only —
`RdpProtocolDescriptor.IsAvailable` already gates it correctly at *runtime*, this is purely a
compile-time/project-file gap), not yet started. DMG additionally needs an Apple Developer Program
account, a Developer ID certificate, and notarization credentials as GitHub Secrets — external things
only the user can obtain.

Still open: winget manifest, making `PolyTerm.App` cross-platform buildable (unlocks real RPM/DMG
packaging), the bundled Windows X server, Flatpak, protocol SDK docs, and a perf/memory pass.

## 14. Open questions (decide before or during M0)

1. License: GPL-2.0+ assumed (FreeRDP is Apache-2.0 — compatible; SSH.NET MIT — compatible). Confirm appetite for GPL vs MIT/Apache for the whole.
2. Existing managed VT emulators: adopt-and-own vs clean-room — spike during M0, timebox 3 days.
3. VNC: validate MarcusW.VncClient maintenance status before committing; RFB clean-room is the fallback.
4. macOS RDP native pack: build complexity of FreeRDP on osx-arm64 — confirm early in M2, it is the least-trodden path.
5. Engine daemon mode (session reattach): explicitly deferred; revisit after v1.0 demand signals.
6. KeePass IPC mechanism: KeePassRPC plugin (mature, used by Kee browser extension) vs KPScript vs a lighter custom plugin — spike during M0 alongside the OS-keychain provider.
7. MCP transport default (stdio vs local Unix socket/named pipe): confirm against how Claude Code/C
odex prefer to attach before locking §4.7's transport choice.

---

*Spec ends. Feed to your coding agent with: "Read docs/polyterm-spec.md and scaffold M0."*