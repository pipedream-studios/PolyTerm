using System.Runtime.InteropServices;

namespace PolyTerm.App;

/// <summary>Shared per-OS app-data directory resolution, used by <see cref="AppSettings"/> and <see cref="Macros.MacroStore"/>.</summary>
internal static class AppPaths
{
    /// <summary>
    /// Per-OS app-data directory (%AppData%/PolyTerm on Windows, ~/Library/Application Support/PolyTerm
    /// on macOS, ~/.config/PolyTerm elsewhere). Creates the directory if it doesn't exist.
    /// </summary>
    public static string ConfigDirectory
    {
        get
        {
            var dir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PolyTerm")
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   "Library", "Application Support", "PolyTerm")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   ".config", "PolyTerm");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
