using System.Text.Json;

namespace PolyTerm.App.Macros;

/// <summary>Loads/saves the app-global macro list as JSON in <see cref="AppPaths.ConfigDirectory"/>.</summary>
internal static class MacroStore
{
    private static string FilePath => Path.Combine(AppPaths.ConfigDirectory, "macros.json");

    /// <summary>Loads the saved macro list, falling back to an empty list if the file is missing or invalid.</summary>
    public static List<MacroDefinition> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<MacroDefinition>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Persists the macro list to disk. Failures are swallowed — macros are best-effort, like <see cref="AppSettings"/>.</summary>
    public static void Save(IReadOnlyList<MacroDefinition> macros)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(macros)); }
        catch { }
    }
}
