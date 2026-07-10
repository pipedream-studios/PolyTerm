using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PolyTerm.Protocols.Ssh.Tests")]
// PolyTerm.App's New Connection dialog needs X11DisplayEndpoint/X11ServerGuidance for its X11-server
// reachability check/install guidance — narrowly-scoped access rather than making SSH's X11 plumbing
// public API surface, same reasoning as the Tests grant above. Its AssemblyName is "PolyTerm", not
// "PolyTerm.App" (see PolyTerm.App.csproj — chosen to avoid a case-insensitive filename collision with
// PolyTerm.Cli's own AssemblyName "polyterm" when both are published to the same folder).
[assembly: InternalsVisibleTo("PolyTerm")]
