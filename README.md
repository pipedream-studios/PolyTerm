# PolyTerm

**One client for every remote connection.** PolyTerm is a cross-platform, tabbed, multi-protocol
remote connections manager for Windows, macOS, and Linux — SSH, RDP, VNC, Telnet, Serial, and
HTTP/S in one app, with a hierarchical connection tree, SFTP sidebar, tunnels, jump hosts,
multi-session broadcast, and a built-in MCP server so an AI agent can drive your infrastructure
alongside you.

The name: *poly* (many) + *term* (terminal/protocol endpoint) — one client, every protocol.

> **Status:** pre-1.0, actively developed. No tagged release yet — see
> [Building from source](#building-from-source) below. The desktop app, CLI, and MCP server build
> and run on Windows and Linux; macOS support is in progress (see
> [Platform support](#platform-support)).

## Why PolyTerm

- **No owned credential vault.** PolyTerm never implements its own encrypted secret store. Your
  passwords and keys live in KeePass, Bitwarden, or your OS's own keychain — PolyTerm holds only a
  pointer to them. There's no bespoke crypto to audit and no second place your secrets live.
- **Protocols are modules, not special cases.** The UI never asks "is this SSH?" — it asks "does
  this session support a terminal / file transfer / graphical surface / port forwarding?" via
  capability flags. Adding a new protocol means adding one project; nothing in the app, session
  manager, or storage layer has to change.
- **The engine is headless.** `PolyTerm.Engine` has zero UI framework references and runs the same
  whether it's driven by the desktop app, the CLI, or the MCP server — three peer front ends over
  one core, not a GUI with a scripting bolt-on.
- **AI-ready by design, not by afterthought.** The MCP server is architecturally a first-class
  consumer of the engine. There is no credential-read tool and never will be — an agent can ask to
  open a connection, but there is no code path that can hand back what's inside it.

## Features

**Protocols**
- SSH — terminal, SFTP/SCP, local/remote/dynamic tunnels, jump host chaining, X11 forwarding, SSH
  agent support, keyboard-interactive auth, auto-reconnect with backoff
- RDP — FreeRDP 3 via a small native shim, on every platform (certificate trust UI, drive
  redirection, audio redirection, clipboard, dynamic resolution where the server supports it)
- VNC — managed RFB client with real pixel-format-verified rendering
- Telnet, raw TCP, and rlogin
- Serial/COM — plus automatic detection of USB-attached COM ports, surfaced as one-click
  quick-connect entries at the bottom of the connection tree
- Local shell (ConPTY on Windows, pty on Unix)
- HTTP/S — embedded browser tab by default (Avalonia WebView / WebView2), with a per-profile
  opt-out to your system's default browser instead

**Terminal**
- From-scratch VT/ANSI emulator (DEC-compatible parser, 256-color and truecolor SGR, alternate
  screen, bracketed paste, mouse reporting, OSC title, wide-char/CJK cell support) — not a
  WebView/xterm.js wrapper, so no bundled-Chromium dependency
- Session recording to asciicast v2 or raw log, with pause/resume
- Multi-session broadcast — send the same input to every open terminal session at once

**Connection management**
- Hierarchical connection tree with folders and per-property inheritance (a profile can defer any
  field to its parent folder, resolved at connect time)
- Quick connect (`user@host:port` parsing, promotable to a saved profile)
- Command palette (Ctrl+Shift+P) — every app action, fuzzy-searchable
- Macros/snippets with variable expansion
- Import from mRemoteNG (`confCons.xml`, including encrypted passwords) and MobaXterm
  (`.mxtsessions`)
- SFTP sidebar auto-attached to any connected SSH session, including remote-file
  edit-in-local-editor round-tripping

**AI integration (MCP)**
- A local MCP server (`polyterm-mcp`) exposes terminal, SFTP, framebuffer (RDP/VNC
  screenshot/click/type/key), and embedded-webview tools, capability-scoped rather than
  protocol-scoped — any new protocol module gets terminal/file-transfer MCP support for free
- Human-in-the-loop approval gate in front of every state-changing tool, with a configurable
  per-connection or global policy (prompt / allow / deny) and rate limiting
- No network-exposed endpoint — local named pipe / stdio only

Every package ships the server in an `mcp/` folder next to the app:

| Platform | Package | `polyterm-mcp` path |
|---|---|---|
| Windows | MSI | `C:\Program Files\PolyTerm\mcp\polyterm-mcp.exe` |
| Windows | Portable zip | `<unzip dir>\mcp\polyterm-mcp.exe` |
| Linux | Tarball | `<extract dir>/mcp/polyterm-mcp` |
| macOS | `.dmg` | `/Applications/PolyTerm.app/Contents/MacOS/mcp/polyterm-mcp` |
| macOS | Tarball | `<extract dir>/mcp/polyterm-mcp` |

For Claude Code, register that path:

```bash
# Windows
claude mcp add polyterm -- "C:\Program Files\PolyTerm\mcp\polyterm-mcp.exe"
# Linux (example: tarball extracted to ~/.local/share/PolyTerm)
claude mcp add polyterm -- ~/.local/share/PolyTerm/mcp/polyterm-mcp
# macOS
claude mcp add polyterm -- /Applications/PolyTerm.app/Contents/MacOS/mcp/polyterm-mcp
```

For Claude Desktop, add the same path to `claude_desktop_config.json` (Settings → Developer → Edit
Config), then restart Claude Desktop:

```json
{
  "mcpServers": {
    "polyterm": { "command": "C:\\Program Files\\PolyTerm\\mcp\\polyterm-mcp.exe" }
  }
}
```

`polyterm-mcp` is a thin bridge to the desktop app, so **PolyTerm must be running** when a tool is
called. Start order doesn't matter: it connects to the app on the first tool call, and reconnects if
PolyTerm restarts.

## Platform support

| Platform | Status |
|---|---|
| Windows (x64) | Full support. MSI and portable zip. |
| Linux (x64) | Desktop app, CLI, and MCP server, shipped as a portable tarball. No `.deb`/`.rpm`/AppImage yet. |
| macOS (Intel, Apple Silicon) | In progress. Builds and runs, shipped as `.dmg` and tarball, ad-hoc signed (not notarized). CI tests on Intel only. |

Everything except the Windows-only UI-automation tests targets plain `net10.0` and builds
cross-platform, including the Avalonia desktop app. CI builds and tests on Windows, Linux, and
macOS (Intel) on every pull request; the macOS leg also publishes the app for Apple Silicon as a
compile check.

RDP uses a small native FreeRDP shim (`native/PolyTerm-rdp-shim`), built with CMake before
`dotnet build`; if it's missing, RDP shows as unavailable and everything else still works. On Linux
it links the system FreeRDP 3 (`apt install freerdp3-dev`). On Windows and macOS FreeRDP comes from
vcpkg; on macOS, `packaging/macos/build-native-shims.sh` builds it together with the PTY shim that
local-shell resize needs, both as universal binaries.

### Saving passwords on Linux

To save passwords in the OS keychain, PolyTerm needs:

- **`secret-tool`**, from the `libsecret-tools` package on Debian and Ubuntu
  (`sudo apt install libsecret-tools`). Some installs don't include it, such as Ubuntu 24.04 with
  `ubuntu-desktop-minimal`. **Remember this device** for Bitwarden needs it too.
- **A Secret Service keyring**, such as GNOME Keyring. PolyTerm saves to your default keyring.
  KWallet is untested.

On the first save, you may be asked to choose a password for a new keyring. This has been seen
signing in through xrdp on Ubuntu 24.04, where the sign-in wasn't set up to create or unlock a
keyring. The keyring belongs to your desktop, not to PolyTerm, and you may be asked to unlock it
again in later sessions. Don't leave its password blank: GNOME then stores the keyring unencrypted.

## Troubleshooting logs

Run PolyTerm from a terminal with `POLYTERM_LOG_LEVEL` set to `Trace`, `Debug`, `Information`,
`Warning`, `Error`, `Critical` or `None` to change what it logs. Release builds default to
`Information`.

At `Debug` and `Trace`, PolyTerm holds back some of the VNC library's output, which includes several
lines for every screen update and one for every key press:

| `POLYTERM_LOG_LEVEL` | What the VNC library logs |
|---|---|
| `Debug` | `Information` and above |
| `Trace` | Everything, except that its message sender is held at `Information` and above |

To debug the messages sent to the server, set `POLYTERM_LOG_VNC_KEYSTROKES=1` as well as
`POLYTERM_LOG_LEVEL=Trace`. **That log contains everything you type into a VNC session, including
passwords**, so check it before sharing it.

## Building from source

Requirements:

- [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 10.0.301 or later (pinned in
  `global.json`, which rolls forward to newer SDKs)
- CMake and a C compiler, for the native shims (optional: without the RDP shim, RDP shows as
  unavailable and everything else still works)
- Linux only, for the RDP shim: FreeRDP 3 dev packages. On Ubuntu/Debian:
  `sudo apt install pkg-config build-essential cmake freerdp3-dev libwinpr3-dev zlib1g-dev`

```bash
git clone https://github.com/pipedream-studios/PolyTerm.git
cd PolyTerm
```

On Windows, build the full solution:

```powershell
dotnet build PolyTerm.slnx -c Release
dotnet run --project src/PolyTerm.App      # launch the desktop app
```

On Linux and macOS, build the portable solution. It has everything except the Windows-only test
projects:

```bash
cmake -S native/PolyTerm-rdp-shim -B native/PolyTerm-rdp-shim/build && cmake --build native/PolyTerm-rdp-shim/build   # Linux RDP shim
# macOS: packaging/macos/build-native-shims.sh instead
dotnet build PolyTerm.Portable.slnx -c Release
dotnet run --project src/PolyTerm.App      # launch the desktop app
dotnet run --project src/PolyTerm.Cli -- --help
```

Packaging is scripted under `packaging/` and wired into `.github/workflows/release.yml` on a
version tag push: a portable zip and MSI for Windows, a tarball for Linux (x64), and tarballs and
`.dmg` installers for macOS (`osx-x64` and `osx-arm64`). The macOS builds are ad-hoc signed, not
notarized, so the first launch needs System Settings → Privacy & Security → **Open Anyway** (or
`xattr -dr com.apple.quarantine /Applications/PolyTerm.app`).

## Documentation

| Doc | Covers |
|---|---|
| [`docs/polyterm-spec.md`](docs/polyterm-spec.md) | Vision, design principles, full roadmap, and per-milestone status |
| [`docs/architecture.md`](docs/architecture.md) | How the layers fit together, project-by-project |
| [`docs/protocols.md`](docs/protocols.md) | How to add a new protocol module |
| [`docs/storage.md`](docs/storage.md) | Profile/settings/credential storage model |
| [`docs/testing.md`](docs/testing.md) | Testing strategy and what's verified vs. still open |

## Contributing

Issues and PRs are tracked on GitHub. If you're picking up an open issue, check
[`docs/polyterm-spec.md`](docs/polyterm-spec.md) for the design principles the codebase holds
itself to (capability-scoped dispatch, no owned credential vault, headless engine) before
proposing an approach that cuts across them.

## License

PolyTerm is free software: you can redistribute it and/or modify it under the terms of the GNU
General Public License as published by the Free Software Foundation, either version 2 of the
License ([full text](LICENSE)), or (at your option) any later version (GPLv3 text:
https://www.gnu.org/licenses/gpl-3.0.html).
