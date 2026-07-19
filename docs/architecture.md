# Architecture

A map of how PolyTerm is put together. For the original design rationale and roadmap, see
[`polyterm-spec.md`](polyterm-spec.md); for known gaps see [`todos.md`](todos.md). For how to add
a new protocol module, see [`protocols.md`](protocols.md); for how profiles, settings, and
credentials are stored, see [`storage.md`](storage.md).

## Layering

```
PolyTerm.Cli   PolyTerm.Mcp   PolyTerm.App (Avalonia UI) + PolyTerm.App.Surfaces
     \              \              /       (Terminal / RdpHost / Framebuffer surfaces)
      \              \            /
       ----------  PolyTerm.Engine  ----------
                        |
              PolyTerm.Engine.Abstractions
                        |
   ---------------------------------------------------------------------------
   |          |        |        |          |                     |          |
  Ssh   LocalShell    Rdp      Vnc    Telnet/Raw/Rlogin        Serial    Web (http/https)
   |          |                          |                        |
   ---------------------------------------------------------------
                        |
                  Terminal surface:
             PolyTerm.Terminal.Emulation
             (Ssh/LocalShell/Telnet/Raw/Rlogin/Serial only —
              Rdp/Vnc render their own pixels, Web has no surface at all)
```

- **`PolyTerm.Engine.Abstractions`** — every contract and shared model (`IProtocolSession`,
  `ITerminalChannel`, `IFramebufferChannel`, `INativeHostChannel`, `IProfileStore`,
  `ICredentialProvider`, `ConnectionProfile`, `SessionContext`, `SessionState`,
  `ProtocolCapabilities`, ...). No implementation, no Avalonia reference. This is the vocabulary
  every other layer speaks.
- **`PolyTerm.Engine`** — the concrete, headless engine: `SessionManager`, `ProfileStore` (SQLite),
  `HostKeyStore` (SQLite), `TofuHostKeyPolicy`, `ProtocolRegistry`, and the built-in credential
  providers (`OsKeychainProvider`, `KeePassProvider`, `BitwardenProvider`). Registered via
  `EngineServiceCollectionExtensions.AddPolyTermEngine(...)`.
- **Protocol modules** — eight `IProtocolDescriptor` implementations across six projects today
  (`PolyTerm.Protocols.Ssh`, `.LocalShell`, `.RdpFreerdp`, `.Vnc`, `.Telnet` — telnet/raw/rlogin, three
  descriptors in one project — `.Serial`, `.Web` — http/https, two descriptors in one project).
  Each registers itself via its own `AddPolyTermXyz()` extension. Adding protocol N+1 means adding
  one new project (or descriptor, if it fits an existing project's theme); nothing in the engine,
  session manager, or storage layer changes. See [`protocols.md`](protocols.md) for the full guide,
  worked against real examples of every surface kind, not just `Terminal`.
- **`PolyTerm.Terminal.Emulation`** — a from-scratch VT/ANSI parser (Paul Williams state machine)
  and cell-grid terminal emulator, consumed by any protocol whose surface is `SurfaceKind.Terminal`
  (six of the eight descriptors — RDP and VNC render pixels instead, Web has no surface at all).
- **`PolyTerm.App` + `PolyTerm.App.Surfaces`** — the Avalonia desktop UI. `App.Surfaces` hosts one
  rendering control per `SurfaceKind`: `TerminalSurface` (`ITerminalChannel`, VT-aware) and
  `FramebufferSurface` (`IFramebufferChannel`, blits RDP's and VNC's decoded RGBA32 bytes into a
  `WriteableBitmap` — cross-platform, no native control embedding). `SurfaceKind.NativeHost`/
  `INativeHostChannel` still exist for a protocol that wants to reparent a real native window handle
  in (RDP used this via the Windows MSTSC ActiveX control before moving to FreeRDP/`Framebuffer`),
  but nothing currently implements them.
  `SessionSurfaceTemplateSelector` picks the right one off `SessionTabViewModel.Surface`, so the App
  never special-cases a protocol id to decide how to render it. `PolyTerm.App` hosts the windows,
  view models, the app's own DI composition root (`AppCompositionRoot`), and the MCP named-pipe
  bridge (`McpBridge/McpBridgeService.cs` — see below).
- **`PolyTerm.Cli` / `PolyTerm.Mcp`** — alternative front-ends over the same engine. The CLI exists
  partly as an architectural enforcement mechanism: if a feature can't be exercised without the
  GUI, the engine isn't actually headless. The MCP server (`polyterm-mcp.exe`) is a thin stdio
  JSON-RPC transport — it holds no engine state itself and talks to the running desktop app over a
  named pipe (`PolyTerm.App/McpBridge/McpBridgeService.cs`, where the real tool logic actually
  lives) rather than embedding the engine, so an AI agent can drive whatever session the user
  already has open, or open a new one by name via `connection_open` (see
  [MCP bridge & approval gate](#mcp-bridge--approval-gate) below).

## Core principles

1. **The engine is headless.** Nothing in `Engine`, `Engine.Abstractions`, or a protocol module
   references Avalonia. Anything that needs a window handle or render surface lives in the App
   layer behind `ITerminalChannel`/`IFramebufferChannel`.
2. **Capabilities, not assumptions.** The UI never asks "is this SSH?" — it asks "does this session
   support file transfer?" via `IProtocolSession.FileTransfer is not null`, or "does this protocol
   support port forwarding?" via `ProtocolCapabilities.PortForwarding`. See
   [Capability model](#capability-model) below.
3. **In-process now, boundary-ready later.** Every engine interface is async and event-driven so a
   future daemon/IPC boundary between the engine and the App could be inserted without redesigning
   the contracts (this is why sessions are handed around as interfaces, never concrete types).
4. **Secrets never touch the UI layer in plaintext.** The App asks for a profile by id; the engine
   resolves credentials internally and hands the protocol module only what it needs, when it needs
   it (see [`storage.md`](storage.md#credentials)).

## Session lifecycle

```
ConnectionProfile (SQLite)
        |
  IProfileStore.ResolveAsync(id)         — applies folder inheritance
        |
  ISessionManager.OpenAsync(profileId)   — or OpenDirectAsync(profile) for transient profiles
        |
        |-- resolve profile's own credential (ICredentialProvider)
        |-- resolve jump-host chain + each hop's credential (SSH only, via Settings["jumpHostProfileId"])
        |-- build SessionContext (Profile, Credential, JumpChain, HostKeyPolicy, RefreshCredentialsAsync)
        |
  IProtocolRegistry.Find(profile.ProtocolId).CreateSession(ctx)
        |
  IProtocolSession.ConnectAsync()
        |
        v
  SessionState.Connecting -> Connected
                                |
                    (unexpected disconnect)
                                |
              protocol declares Reconnect? --- no --> Faulted
                                |
                               yes
                                |
                                v
                        SessionState.Reconnecting
                     (backoff retry loop, re-resolving
                      fresh credentials each attempt)
                                |
                        success -> Connected
```

`SessionManager` resolves credentials exactly once per connect attempt, hands them to the protocol
module synchronously, and disposes them (zeroing the underlying buffers) in a `finally` regardless
of outcome — a protocol module must not cache `SessionContext.Credential` beyond the initial
`ConnectAsync` call. A protocol that supports `ProtocolCapabilities.Reconnect` (currently only SSH)
instead calls `SessionContext.RefreshCredentialsAsync` again for every reconnect attempt, since the
original material is already zeroed by the time a reconnect is needed.

`SessionManager.OnSessionStateChanged` only reacts to `SessionState.Closed`/`Faulted` — a session in
`Reconnecting` stays registered and its tab survives in the App with no special-casing required
there, which is why introducing the `Reconnecting` state required no changes to how the session
table or the UI's tab-close logic work.

## Capability model

`IProtocolDescriptor.Capabilities` is a `[Flags] ProtocolCapabilities` bitmask. `IProtocolSession`
exposes one nullable accessor per capability (`Terminal`, `FileTransfer`, `Framebuffer`, `Tunnels`,
`NativeHost`) that returns null when the underlying protocol doesn't support it — the App checks the
accessor, never the protocol id. Today's eight protocol descriptors:

| Protocol | Id | Surface | Capabilities |
|---|---|---|---|
| SSH | `ssh` | Terminal | Terminal, FileTransfer (SFTP), PortForwarding, X11Forwarding\*, Exec\*, InputBroadcast, Recording, Reconnect |
| Local shell | `local-shell` | Terminal | Terminal, InputBroadcast, Recording |
| RDP | `rdp` | Framebuffer | GraphicalDesktop |
| VNC | `vnc` | Framebuffer | GraphicalDesktop |
| Telnet | `telnet` | Terminal | Terminal, InputBroadcast, Recording |
| Raw TCP | `raw` | Terminal | Terminal, InputBroadcast, Recording |
| Rlogin | `rlogin` | Terminal | Terminal, InputBroadcast, Recording |
| Serial | `serial` | Terminal | Terminal, Recording (no InputBroadcast — a serial line usually addresses one physical device) |
| HTTP/HTTPS | `http`/`https` | External | None (opens the system browser; no engine-managed session state at all) |

\*SSH's X11Forwarding/Exec are declared but not yet implemented behind a working code path.

### Surface kinds

`IProtocolDescriptor.Surface` (`SurfaceKind`) tells the App which rendering control to build for a
session, independent of `Capabilities` (`GraphicalDesktop` covers both RDP and VNC, which now share
the same `Framebuffer` surface):

- **`Terminal`** — VT-aware, bound to `IProtocolSession.Terminal`. The overwhelming majority of
  protocols (six of eight) — anything that's fundamentally a byte stream.
- **`Framebuffer`** — the protocol streams decoded pixel bytes (`IFramebufferChannel.RegionUpdated`,
  fixed `FramebufferFormat.Rgba32`) and the App blits them into a `WriteableBitmap`. Cross-platform by
  construction. Both current consumers reimplement their client library's own rendering rather than
  embedding it: `VncFramebufferChannel` bridges `MarcusW.VncClient`'s own `IRenderTarget` callback
  into this (see [`todos.md`](todos.md)'s VNC section for a real pixel-format bug this abstraction
  surfaced and fixed), and `RdpFreerdpFramebufferChannel` decodes frames delivered by the
  `native/PolyTerm-rdp-shim` C shim around FreeRDP.
- **`NativeHost`** — the protocol module owns a real, opaque native window and the App just embeds
  it (Win32 `SetParent`-style reparenting) — "the OS or a native library renders it," not "PolyTerm
  renders it." RDP used this via the Windows MSTSC ActiveX control before moving to FreeRDP/
  `Framebuffer` for cross-platform support; the abstraction remains but currently has no consumer.
- **`External`** — no App-owned surface at all. HTTP/HTTPS: `ConnectAsync` just calls
  `Process.Start` against the system browser and the session immediately reports a state with every
  capability accessor null; `MainViewModel.OnSessionOpened` recognizes that shape and skips adding a
  tab for it rather than special-casing the `http`/`https` protocol ids directly.

## Threading model

Engine events (`StateChanged`, `ErrorOccurred`, `ITerminalChannel.OutputReceived`, etc.) fire on
engine threads — the engine never calls `Dispatcher`. Every App-layer subscriber marshals to the UI
thread itself (`Avalonia.Threading.Dispatcher.UIThread.Post(...)`), e.g.
`SessionTabViewModel.OnStateChanged`. `SshSession`'s reconnect loop runs on a background
`Task.Run` and calls `SetState`/raises events from there — this is why every subscriber must
dispatch rather than assume it's already on the UI thread.

## MCP bridge & approval gate

```
MCP client (e.g. Claude Code)
        | stdio, JSON-RPC 2024-11-05
  polyterm-mcp.exe (PolyTerm.Mcp — thin transport, no engine state of its own)
        | named pipe "polyterm"
  McpBridgeService (PolyTerm.App/McpBridge — real tool logic lives here, not in PolyTerm.Mcp)
        |-- read-only tools (session_list, connection_list, terminal_read, sftp_list/read) ----> ISessionManager / IProtocolSession, ungated
        |-- state-changing tools (connection_open, terminal_execute, terminal_send_keys,
        |   sftp_write/delete/rename) -----------------------------------------------------> IApprovalGate.CheckAsync first
        v
  ApprovalGateService: per-connection Settings["mcpPolicy"], else AppSettings.McpApprovalPolicy,
  else Prompt (blocks on a real ConfirmDialog on the UI thread)
```

`McpBridgeService` never called `ISessionManager.OpenAsync` at all until `connection_open` was
added — every tool before that only ever *observed* sessions the GUI had already opened, tracked in
its own `Dictionary<Guid, SessionEntry>` populated by subscribing to `SessionOpened`/`SessionClosed`.
`connection_open` resolves a saved profile by name (case-insensitive exact match wins, else an
unambiguous substring match, else a clear error listing candidates) or by exact `profile_id`, then
calls `OpenAsync` like the GUI does — the resulting session shows up as a real tab automatically,
since `MainViewModel` subscribes to the same `SessionOpened` event.

`IApprovalGate` (`PolyTerm.App/McpBridge/Approval/`) sits in front of every state-changing tool —
not under `PolyTerm.Mcp/Approval/` as `polyterm-spec.md` §4.7 originally sketched, because that's a
thin transport with no tool logic to gate; the gate lives next to the logic it actually protects.
Credential-read is not merely gated, it's structurally absent — there is no MCP tool that can return
a secret, matching `polyterm-spec.md` §4.7 rule 1's "no code path at all, not just a gate."

**Fixed, not architectural**: the named-pipe transport itself used to hang on every single call —
both `McpBridgeService` and `PolyTermClient` built their `StreamWriter` with the BOM-carrying
`Encoding.UTF8` singleton, and the preamble write on first `Flush()` (immediate, given `AutoFlush =
true`) blocked forever with nothing yet reading. Fixed with a BOM-less `UTF8Encoding`, verified
against the real `polyterm-mcp.exe` end to end, and regression-tested — see
[`todos.md`](todos.md)'s MCP section and [`testing.md`](testing.md) for the full story.
