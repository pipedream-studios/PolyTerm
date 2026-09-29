# Storage: profiles, settings, and credentials

## Connection profiles

`ConnectionProfile` (`PolyTerm.Engine.Abstractions/Models/ConnectionProfile.cs`) is both a leaf
connection and a folder node in the same tree — `IsFolder` distinguishes them, `ParentId` links a
node to its containing folder (or null at the root); property inheritance from parent folders is
a first-class feature of this tree, not an importer shim.

Storage backend is a single **SQLite** database (`PolyTerm.db` under the platform config dir —
`%APPDATA%\PolyTerm`, `~/Library/Application Support/PolyTerm`, or `~/.config/PolyTerm`), owned by
`ProfileStore` (`PolyTerm.Engine/Storage/ProfileStore.cs`). Every call opens and closes its own
connection (`Pooling=False` in the connection string) specifically so a single `ProfileStore`
instance is safe to share across concurrent callers without a connection-pool/locking story to
reason about — the tradeoff is a fresh connection per call, which is fine at this scale (a local
desktop app's profile tree, not a high-throughput service).

### Folder inheritance

A profile can defer `Host`, `Port`, and/or `Credential` to its ancestor folders via
`ConnectionProfile.Inherit` (an `InheritanceMask` flags enum). `IProfileStore.ResolveAsync` applies
this at read time — `GetAllAsync`/`GetByIdAsync`/`GetChildrenAsync` never apply inheritance, only
`ResolveAsync` does (and only `SessionManager.OpenAsync` calls `ResolveAsync`; direct/transient
profiles via `OpenDirectAsync` skip it entirely since there's no stored tree to inherit from).

**Non-obvious behavior, verified against `ProfileStore.ApplyInheritanceAsync`:** ancestors are
walked from the immediate parent up to the root, then applied in the *opposite* order — root/outermost
first, descending toward the immediate parent — and a field is only filled if it's still null at
that point. The practical effect: **if more than one ancestor in the chain has a value set for the
same inherited field, the outermost ancestor wins, not the nearest parent.** This is the reverse of
what "inheritance" might suggest at a glance (most languages/config systems resolve nearest-wins),
so don't assume nearest-parent-wins when reasoning about a multi-level folder tree with inheritance
enabled at more than one level.

`Inherit` currently only drives `Host`/`Port`/`Credential`. `InheritanceMask.Settings` exists as a
flag but `ApplyInheritanceAsync` does not act on it — the `Settings` dictionary is never inherited
from a parent folder today, regardless of whether that flag is set.

## The `Settings` dictionary pattern

`ConnectionProfile.Settings` is a `Dictionary<string, JsonElement>` — opaque, protocol-specific
data that the storage layer never interprets. This is how every protocol-specific setting is
stored, from a single scalar (SSH's `"username"`) to a structured list (`"tunnels"`) to a single
reference (`"jumpHostProfileId"`).

**The established convention** is a pair of extension methods per feature, living in
`PolyTerm.Engine.Abstractions/Models/`, so both the engine (read-only) and the App's editor UI
(read+write) share the exact same (de)serialization logic instead of duplicating
`TryGetValue`/`JsonSerializer` boilerplate at each call site:

```csharp
// TunnelDefinition.cs — a structured list
public static class TunnelSettingsExtensions
{
    public static List<TunnelDefinition> GetTunnels(this Dictionary<string, JsonElement> settings) => ...
    public static void SetTunnels(this Dictionary<string, JsonElement> settings, IReadOnlyList<TunnelDefinition> tunnels) => ...
}

// JumpHostSettings.cs — a single optional reference
public static class JumpHostSettingsExtensions
{
    public static Guid? GetJumpHostProfileId(this Dictionary<string, JsonElement> settings) => ...
    public static void SetJumpHostProfileId(this Dictionary<string, JsonElement> settings, Guid? profileId) => ...
}
```

`SetXyz` removing the key entirely when the value is empty/null (rather than writing e.g.
`"tunnels": []`) is the convention followed by both — keeps most profiles free of stray empty
keys. Follow this same two-method-extension-class shape for any new structured setting rather than
inlining `Dictionary<string, JsonElement>` access at call sites.

For simple scalars that don't warrant their own file (SSH's `"username"`/`"privateKeyPath"`/
`"terminalType"`/`"keepAliveSeconds"`), both `SshSession` and `NewConnectionViewModel` currently
duplicate small private `GetSetting`/`GetIntSetting` helpers rather than sharing an extension class
— acceptable for scalars this simple, but if a third consumer needs the same key, promoting it to a
shared extension method (matching the tunnels/jump-host pattern) is the better long-term shape.

A newer example of the same scalar-in-`Settings` pattern, this time genuinely protocol-agnostic
rather than SSH-specific: the MCP approval gate's per-connection policy override lives at
`Settings["mcpPolicy"]` (`"Allow"`/`"Deny"`/`"Prompt"`, checked by
`ApprovalGateService.ResolvePolicy` — see `docs/architecture.md`'s MCP section). Every protocol's
`ConnectionProfile` can carry this key regardless of what protocol-specific settings it also has,
which is exactly the point of keeping `Settings` opaque to the storage layer — a cross-cutting,
non-protocol-specific setting fits the same dictionary with zero schema changes anywhere.

**Known gap:** `SettingsSchema`/`SettingsField` (declared per-protocol, meant to drive an
auto-generated settings UI) is not rendered anywhere in the App today — `NewConnectionViewModel`
hardcodes its own SSH-specific fields instead. See [`protocols.md`](protocols.md#settingsschema--your-protocols-configurable-fields)
for what this means if you're adding a new protocol.

## Credentials

**Decision:** PolyTerm does not implement its own encrypted credential vault. Secrets live in a
password manager the user already runs (KeePass, Bitwarden) or, as a zero-dependency fallback, the
OS's own secret store. An owned vault (AES-GCM blobs, KDF, agent-protocol implementation) would
have been the single largest concentration of security-critical bespoke code in the project, and
duplicating a secret store most users already run is worse hygiene, not better.

Two distinct types, deliberately kept apart:

- **`CredentialReference`** (`ProviderId` + `ItemRef`) — what's actually persisted on a
  `ConnectionProfile`. No secret material, safe to store in the SQLite database and safe to cross
  the MCP boundary (an agent can be told "connect to profile X" without any tool that could hand
  back a password).
- **`CredentialMaterial`** — the resolved secret (`Username`, `Secret`, `PrivateKey`,
  `PrivateKeyPath`), obtained via `ICredentialProvider.ResolveAsync(CredentialReference)`. Never
  serialized, never logged, never crosses the MCP boundary. `Dispose()` zeroes `Secret`/`PrivateKey`
  in place.

### Lifecycle — read this before touching `SshSession` or any future protocol module

`SessionManager.OpenCoreAsync` resolves `CredentialMaterial` for the main profile (and, for SSH,
every jump-host hop) exactly once per connect attempt, hands it to the protocol module via
`SessionContext.Credential`/`JumpChain` for synchronous consumption during `ConnectAsync`, and
disposes every one of them in a `finally` — unconditionally, whether the connect succeeded or
threw. **A protocol module must not retain a reference to `SessionContext.Credential` past the
initial connect** — by the time anything later runs, the buffers are zeroed (still non-null,
still the same `Length`, but the bytes are gone).

This is exactly why SSH's auto-reconnect needed `SessionContext.RefreshCredentialsAsync`: a
reconnect attempt happens long after the original credential was disposed, so it must go back
through `SessionManager`'s `ResolveCredentialAsync` (via the closure captured in `OpenCoreAsync`)
to get fresh, single-use material for that specific attempt — and the protocol module (not
`SessionManager`) is responsible for disposing whatever `RefreshCredentialsAsync` returns, since
`SessionManager` isn't in the call path for that invocation.

### Providers

| Provider | Mechanism |
|---|---|
| `OsKeychainProvider` (always registered) | Windows Credential Manager via P/Invoke (`WindowsCredentialManager`); implements both `ICredentialProvider` and `ICredentialWriter` (the only provider that can *write* a new credential, used when saving a password typed into the New Connection dialog) |
| `KeePassProvider` (opt-in via `EngineOptions.KeePass`) | Talks to a running KeePass2+KeePassRPC instance over its real WebSocket protocol (`KeePassRpcClient`) — SRP-6 pairing (in-app via `KeePassPairingDialog`, persisted in `KeePassPairingStore`) then encrypted JSON-RPC; read-only (`ICredentialProvider`, not `ICredentialWriter`) |
| `BitwardenProvider` (opt-in via `EngineOptions.Bitwarden`) | Talks directly to a Bitwarden/Vaultwarden server's identity + vault API via `BitwardenApiClient` (no `bw` CLI dependency); `IsAvailableAsync` checks the vault is unlocked; read-only (`ICredentialProvider`, not `ICredentialWriter`) |

All three are registered as DI singletons in `EngineServiceCollectionExtensions.AddPolyTermEngine`;
KeePass/Bitwarden are only added when their respective `EngineOptions` field is non-null.

## Host keys

A second, independent SQLite-backed store — `HostKeyStore` (`PolyTerm.Engine/Storage/HostKeyStore.cs`),
same `Pooling=False` per-call-connection approach as `ProfileStore`, keyed by `(host, port, algorithm)`.
Backs `TofuHostKeyPolicy` (`IHostKeyPolicy`): unknown hosts are trusted and recorded automatically
(trust-on-first-use), a host whose recorded fingerprint still matches is silently re-trusted (and
its `last_seen` timestamp bumped), and a **changed** fingerprint returns `HostKeyDecision.Prompt` —
which `SshSession.WireHostKeyVerification` currently treats identically to `Reject`
(`e.CanTrust = decision == HostKeyDecision.Trust`). **Known gap:** there is no actual interactive
prompt UI wired up yet — a changed host key blocks the connection outright rather than asking the
user to confirm a legitimate key rotation.
