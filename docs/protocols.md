# Adding a protocol module

A protocol module is a small standalone project that implements two interfaces and registers
itself via DI. Nothing in `PolyTerm.Engine`, the session manager, or storage changes when you add
one — that's the point of the capability-based design (see [`architecture.md`](architecture.md)).
This doc walks through the contract using the existing modules as worked examples — eight
`IProtocolDescriptor`s across six projects now: `PolyTerm.Protocols.Ssh` (full-featured, the richest
example), `.LocalShell` (minimal terminal-only), `.Rdp` and `.Vnc` (the two protocols that *don't*
use `SurfaceKind.Terminal` — see [Surface kinds beyond Terminal](#surface-kinds-beyond-terminal)
below), `.Telnet` (three descriptors — telnet/raw/rlogin — sharing one project since they share
almost all their implementation), `.Serial`, and `.Web` (http/https, `SurfaceKind.External`, the
simplest of all — no terminal, no pixels, just `Process.Start` against the system browser).

## The two interfaces you implement

### `IProtocolDescriptor` — static facts about the protocol

```csharp
public interface IProtocolDescriptor
{
    string Id { get; }                       // e.g. "ssh" — stored in ConnectionProfile.ProtocolId
    string DisplayName { get; }              // e.g. "SSH"
    int DefaultPort { get; }                 // 0 when not applicable
    ProtocolCapabilities Capabilities { get; }
    SurfaceKind Surface { get; }             // what the App must render
    SettingsSchema Settings { get; }         // your protocol's configurable fields
    IProtocolSession CreateSession(SessionContext ctx);
}
```

One instance of your descriptor is registered as a DI singleton and lives for the app's lifetime;
`CreateSession` is called once per connection attempt. Compare the two ends of the spectrum, SSH
(full-featured) and local shell (minimal):

```csharp
// SshProtocolDescriptor.cs — full-featured
public ProtocolCapabilities Capabilities =>
    ProtocolCapabilities.Terminal | ProtocolCapabilities.FileTransfer | ProtocolCapabilities.PortForwarding |
    ProtocolCapabilities.X11Forwarding | ProtocolCapabilities.Exec | ProtocolCapabilities.InputBroadcast |
    ProtocolCapabilities.Recording | ProtocolCapabilities.Reconnect;

// LocalShellDescriptor.cs — terminal only; no file transfer/forwarding/reconnect because it's a local process, not a remote link
public ProtocolCapabilities Capabilities =>
    ProtocolCapabilities.Terminal | ProtocolCapabilities.InputBroadcast | ProtocolCapabilities.Recording;
```

Only declare a flag once the corresponding accessor on your `IProtocolSession` actually returns a
working implementation — the App relies on the flag/accessor pair being truthful (a protocol that
declares `PortForwarding` but returns `null` from `Tunnels` will just silently disable that UI, but
it's still a mismatch worth avoiding).

### `SettingsSchema` — your protocol's configurable fields

```csharp
private static SettingsSchema BuildSchema() => new()
{
    Fields =
    [
        new() { Key = "username", DisplayName = "Username", Type = SettingsFieldType.Text, Required = true, Group = "Connection" },
        new() { Key = "privateKeyPath", DisplayName = "Private Key File", Type = SettingsFieldType.FilePath, Group = "Auth" },
        // ...
    ]
};
```

**Known gap:** this schema is currently **not rendered anywhere in the App** — `NewConnectionViewModel`
hardcodes its own SSH-specific fields instead of driving off `SettingsSchema`. If you add a protocol
today, you'll need to hand-build a settings UI for it (following `NewConnectionViewModel`/
`NewConnectionWindow.axaml` as the template) rather than getting one for free from the schema. See
[`todos.md`](todos.md) for the broader context.

Regardless of UI rendering, every setting your protocol reads/writes lives in
`ConnectionProfile.Settings` (a `Dictionary<string, JsonElement>`) — see
[`storage.md`](storage.md#the-settings-dictionary-pattern) for the read/write convention to follow.

### `IProtocolSession` — one connection instance

```csharp
public interface IProtocolSession : IAsyncDisposable
{
    Guid SessionId { get; }
    ConnectionProfile Profile { get; }
    SessionState State { get; }
    event EventHandler<SessionStateChangedEventArgs> StateChanged;
    event EventHandler<SessionErrorEventArgs> ErrorOccurred;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    ITerminalChannel? Terminal { get; }
    IFileTransferChannel? FileTransfer { get; }
    IFramebufferChannel? Framebuffer { get; }
    ITunnelManager? Tunnels { get; }
    INativeHostChannel? NativeHost { get; }
}
```

A new session instance is created per connection attempt via `CreateSession(SessionContext ctx)`.
Your constructor should be cheap and do no I/O — all the real work happens in `ConnectAsync`. Drive
`SessionState` transitions yourself via a private `SetState` helper that raises `StateChanged`
(see `SshSession.SetState`/`LocalShellSession`'s equivalent for the pattern — it's ~6 lines and
not worth abstracting further). The capability accessors (`Terminal`, `FileTransfer`, etc.) should
return null until `ConnectAsync` has actually established that part of the connection, and forever
null for any capability your descriptor doesn't declare.

**What `SessionContext` gives you** (built by `SessionManager`, see
[`architecture.md`](architecture.md#session-lifecycle)):

| Member | Use |
|---|---|
| `Profile` | The resolved `ConnectionProfile` (host/port/settings, with folder inheritance already applied) |
| `Credential` | Resolved secret material for the initial connect — **do not cache past `ConnectAsync`**; `SessionManager` disposes it (zeroes the buffers) right after, whether you succeeded or threw |
| `JumpChain` | SSH-specific today, but generic in shape: an ordered list of resolved bastion hops, each with its own `Profile`+`Credential` |
| `HostKeyPolicy` | Call `EvaluateAsync` before trusting a host key, if your protocol has host keys |
| `RefreshCredentialsAsync` | Only meaningful if you declare `ProtocolCapabilities.Reconnect` — call this to get fresh, single-use credential material for each reconnect attempt (see below) |

## Registration

```csharp
// PolyTerm.Protocols.Ssh/ServiceCollectionExtensions.cs
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPolyTermSsh(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, SshProtocolDescriptor>();
        return services;
    }
}
```

Wired up alongside the other modules in `PolyTerm.App/AppCompositionRoot.cs`:

```csharp
services.AddPolyTermEngine();
services.AddPolyTermSsh();
services.AddPolyTermLocalShell();
```

`ProtocolRegistry` (in `PolyTerm.Engine`) collects every registered `IProtocolDescriptor` at
startup into a dictionary keyed by `Id` — that's the entire integration point. Add your
`AddPolyTermXyz()` call next to the others and your protocol is selectable by `ProtocolId`.

## Terminal-surfaced protocols: `ITerminalChannel`

If `Surface => SurfaceKind.Terminal`, implement `ITerminalChannel` and expose it via `Terminal`. Six
of the eight shipped descriptors take this path (SSH, local shell, Telnet, Raw, Rlogin, Serial), and
share a pattern worth reusing — `TcpSocketTerminalChannel` (`PolyTerm.Protocols.Telnet`) is the base
class Telnet/Raw/Rlogin all share, `SerialTerminalChannel` and `SshTerminalChannel` are the other two
independent implementations, all following the same shape:

- **Lazy read loop.** The background read loop only starts on the *first* `OutputReceived`
  subscription (`add { _outputReceived += value; _readTask ??= Task.Run(ReadLoop); }`), so the
  surface can attach before any bytes are lost, but nothing spins up until something is actually
  listening.
- **`RegisterOutputSink`** is a second, passive subscription mechanism (used by session recording
  and the MCP output buffer) that does *not* trigger the lazy read loop — safe to call before the
  terminal surface has attached.
- Feed output from whatever thread your protocol library delivers it on; `ITerminalChannel.OutputReceived`
  is documented as firing on an engine thread, and the App is responsible for marshalling to the UI
  thread, not you.

## Surface kinds beyond Terminal

The other two protocols with a real, working App-side surface — RDP (`NativeHost`) and VNC
(`Framebuffer`) — are worth reading before building a new graphical-desktop-style protocol, since
they made genuinely different tradeoffs for the same `ProtocolCapabilities.GraphicalDesktop` flag:

- **`NativeHost`** (RDP) — your protocol module owns a real native window and just hands the App a
  raw HWND (`INativeHostChannel.Handle`) to reparent. This is the *lower*-effort path when a
  first-party native control already exists for your protocol (RDP wraps Windows' own
  `mstscax.dll`/MSTSC ActiveX control directly — see `PolyTerm.Protocols.Rdp/RdpSession.cs`) since
  the control renders and handles input entirely on its own; PolyTerm writes almost no
  rendering/input code at all. The real cost is portability — this is inherently OS-specific (RDP is
  Windows-only today, `IProtocolDescriptor.IsAvailable` returns false with a reason on Linux/macOS)
  and the App-side surface (`RdpHostSurface`) has to do real Win32 interop (`SetParent`, DPI-scaled
  `SetWindowPos`) that a cross-platform Avalonia control never needs.
- **`Framebuffer`** (VNC) — your protocol module decodes pixels itself and raises
  `IFramebufferChannel.RegionUpdated` with raw `FramebufferFormat.Rgba32` bytes; the App
  (`FramebufferSurface`) just blits them into a `WriteableBitmap`. Higher effort than `NativeHost`
  when (as for VNC) the underlying client library ships its *own* ready-made rendering control,
  since you're deliberately not using it — `PolyTerm.Protocols.Vnc` reimplements
  `MarcusW.VncClient.Rendering.IRenderTarget` itself rather than pulling in
  `MarcusW.VncClient.Avalonia`'s `VncView`, specifically so `PolyTerm.Protocols.Vnc` stays
  Avalonia-free like every terminal-surfaced protocol, and so `IFramebufferChannel` is a real,
  reusable abstraction rather than a dead pass-through for one library's own control. The payoff is
  genuine cross-platform portability, unlike `NativeHost`. **Get the pixel format's byte order right
  by testing it, not by reading two APIs' documentation and assuming similarly-named formats agree**
  — see [`todos.md`](todos.md)'s VNC section for a real, shipped bug this exact mistake caused.
- **`External`** (HTTP/HTTPS) — no App-owned surface at all; the simplest possible
  `IProtocolDescriptor`. `ConnectAsync` just calls `Process.Start(new ProcessStartInfo(url) {
  UseShellExecute = true })` against the system default browser and transitions straight to
  `Connected` — every capability accessor stays null forever. The one thing to know: the App
  recognizes "every capability accessor is null" as the signal to skip adding a tab for this session
  at all (`MainViewModel.OnSessionOpened`), rather than checking `Surface == External` or the
  protocol id directly — so an `External`-surfaced protocol you add gets this for free as long as it
  genuinely has nothing for any capability accessor to return.

## Optional: auto-reconnect (`ProtocolCapabilities.Reconnect`)

Only SSH implements this today, and it's the most involved part of `SshSession` — read
`SshSession.cs`'s `OnClientError`/`ReconnectLoopAsync`/`ConnectCoreAsync` together before attempting
this for a new protocol. The shape:

1. Factor your real connect logic into a `ConnectCoreAsync(credential, ..., ct)` that takes
   credential material as a **parameter**, not read from `_ctx.Credential` directly — this lets the
   same method serve both the initial connect and every reconnect attempt.
2. On an unexpected disconnect (however your protocol library signals that), guard against
   re-entrancy (only act if `State == Connected`), transition to `SessionState.Reconnecting`, and
   kick off a background retry loop with backoff.
3. Each retry calls `SessionContext.RefreshCredentialsAsync` (if non-null) to get fresh,
   single-use `CredentialMaterial`, calls your `ConnectCoreAsync` again, and disposes the fresh
   credential material itself in a `finally` (the original `SessionManager`-provided disposal
   already happened long before this point).
4. On success, transition back to `Connected`. `SessionManager` never removes a `Reconnecting`
   session from its table and never fires `SessionClosed` for it — no changes needed on the
   consumer side for this to "just work," but the App-side channel-reference caching problem below
   is real and needs handling.
5. If your session exposes a UI-bound channel reference (like `SessionTabViewModel.Terminal`), be
   aware that reconnect typically produces a **brand-new** channel instance (new socket/stream
   underneath) — the consumer needs an explicit change notification (see
   `SessionTabViewModel.OnStateChanged`'s handling of the `Reconnecting`→`Connected` transition,
   which re-raises `PropertyChanged` for `Terminal`/`CanOpenSftp` so the bound `TerminalSurface`
   picks up the new channel) rather than assuming the channel reference never changes for a tab's
   lifetime.

## Checklist for a new protocol module

- [ ] New project, references `PolyTerm.Engine.Abstractions` only (not `PolyTerm.Engine` or any App project)
- [ ] `IProtocolDescriptor` implementation with an accurate `Capabilities` flag set
- [ ] `IProtocolSession` implementation driving `SessionState` transitions and raising `StateChanged`/`ErrorOccurred`
- [ ] Settings read/write via the `Dictionary<string, JsonElement>` extension-method pattern (see [`storage.md`](storage.md))
- [ ] `AddPolyTermXyz()` DI registration extension, wired into `AppCompositionRoot`
- [ ] If terminal-surfaced: `ITerminalChannel` with the lazy-read-loop + `RegisterOutputSink` pattern
- [ ] If native-host/framebuffer-surfaced: read [Surface kinds beyond Terminal](#surface-kinds-beyond-terminal)
      first — these two take genuinely different tradeoffs, not just "the same pattern with different types"
- [ ] Manual verification via the CLI (`polyterm connect`/`exec`) before wiring up any App-side UI — if it can't be exercised headlessly, something's leaking into the App layer that shouldn't be (doesn't apply to `NativeHost`/`Framebuffer`/`External` protocols, which have no headless CLI story by nature — see [`testing.md`](testing.md) for how RDP/VNC/Web got verified instead)
