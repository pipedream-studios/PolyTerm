# PolyTerm

**One client for every remote connection.** PolyTerm is a cross-platform, tabbed, multi-protocol
remote connections manager for Windows, macOS, and Linux — SSH, RDP, VNC, Telnet, Serial, and
HTTP/S in one app, with a hierarchical connection tree, SFTP sidebar, tunnels, jump hosts,
multi-session broadcast, and a built-in MCP server so an AI agent can drive your infrastructure
alongside you.

The name: *poly* (many) + *term* (terminal/protocol endpoint) — one client, every protocol.

> **Status:** pre-1.0, actively developed. No tagged release yet — see
> [Building from source](#building-from-source) below. Windows is the only platform the desktop
> app currently builds on; the engine and CLI are cross-platform (see
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
- RDP — Windows' own MSTSC ActiveX control (certificate trust UI, drive redirection, audio
  redirection, clipboard, dynamic resolution where the server supports it)
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

## Platform support

The engine, CLI, and every protocol module target plain `net8.0` and build cross-platform today
(proved in CI on Windows, Linux, and macOS runners). The desktop app (`PolyTerm.App`) is currently
Windows-only, because its RDP module wraps the OS's own MSTSC ActiveX control. Making the desktop
app buildable on macOS/Linux (unlocking a real RPM/DMG) is tracked as open work — see
[`docs/polyterm-spec.md`](docs/polyterm-spec.md)'s roadmap section for the current state.

## Building from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/chris3081/PolyTerm.git
cd PolyTerm
dotnet build PolyTerm.slnx -c Release      # full solution (Windows)
dotnet run --project src/PolyTerm.App      # launch the desktop app
```

On Linux/macOS, build the cross-platform subset instead:

```bash
dotnet build PolyTerm.Portable.slnx -c Release
dotnet run --project src/PolyTerm.Cli -- --help
```

Windows packaging (portable zip and MSI) is scripted under `packaging/windows/` and wired into
`.github/workflows/release.yml` on a version tag push.

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
