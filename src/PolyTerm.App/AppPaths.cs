using System.Runtime.InteropServices;

namespace PolyTerm.App;

/// <summary>Shared per-OS app-data directory resolution, used by <see cref="AppSettings"/> and <see cref="Macros.MacroStore"/>.</summary>
internal static class AppPaths
{
    /// <summary>
    /// Per-OS app-data directory (%AppData%/PolyTerm on Windows, ~/Library/Application Support/PolyTerm
    /// on macOS, ~/.config/PolyTerm elsewhere). Creates the directory if it doesn't exist.
    /// <c>POLYTERM_DATA_DIR</c>, when set, overrides this entirely — used by
    /// <c>PolyTerm.FlaUI.Tests</c> to point a test run at an isolated, disposable directory instead of
    /// the real user vault (this same directory backs settings, macros, and — via
    /// <see cref="AppCompositionRoot"/> passing it through as the engine's <c>DatabasePath</c> — the
    /// real connection-profile database too). Not meant for end users; there is no UI to set it.
    /// </summary>
    public static string ConfigDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("POLYTERM_DATA_DIR");
            if (!string.IsNullOrEmpty(overrideDir))
            {
                Directory.CreateDirectory(overrideDir);
                return overrideDir;
            }

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
