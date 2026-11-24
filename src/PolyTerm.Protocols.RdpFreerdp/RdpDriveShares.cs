using System.Text.Json;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>One local folder shared with the remote session, as FreeRDP's <c>/drive:&lt;Name&gt;,&lt;Path&gt;</c>.</summary>
internal sealed record RdpDriveShare(string Name, string Path);

/// <summary>
/// Reads the drive shares an RDP profile asks for (issue #269). Two shapes are understood:
/// <list type="bullet">
///   <item><c>driveShares: [{ "name": ..., "path": ... }]</c> — named folder shares, the model the
///   connection dialog moves to in issue #270. Takes precedence when present.</item>
///   <item><c>redirectedDrives: ["C:\\", "/Volumes/Data", ...]</c> (with <c>redirectDrives: true</c>)
///   — what the current mstsc-style checklist saves: each entry is a local drive root, shared under a
///   name derived from it (<c>C:\</c> → <c>C</c>, <c>/Volumes/Data</c> → <c>Data</c>).</item>
/// </list>
/// </summary>
internal static class RdpDriveShares
{
    public const string DriveSharesKey = "driveShares";
    public const string RedirectDrivesKey = "redirectDrives";
    public const string RedirectedDrivesKey = "redirectedDrives";

    public static IReadOnlyList<RdpDriveShare> FromSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (settings.TryGetValue(DriveSharesKey, out var shares) && shares.ValueKind == JsonValueKind.Array)
            return Distinct(ReadDriveShares(shares));

        // The checklist writes redirectDrives=false with no list when nothing is ticked; an explicit
        // false wins over any stale list left behind.
        if (settings.TryGetValue(RedirectDrivesKey, out var enabled) && enabled.ValueKind == JsonValueKind.False)
            return [];

        if (settings.TryGetValue(RedirectedDrivesKey, out var drives) && drives.ValueKind == JsonValueKind.Array)
            return Distinct(ReadLegacyDrives(drives));

        return [];
    }

    private static IEnumerable<RdpDriveShare> ReadDriveShares(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var path = GetString(item, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            var name = GetString(item, "name");
            yield return new RdpDriveShare(
                string.IsNullOrWhiteSpace(name) ? NameFromPath(path) : name.Trim(), path);
        }
    }

    private static IEnumerable<RdpDriveShare> ReadLegacyDrives(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } path) continue;
            yield return new RdpDriveShare(NameFromPath(path), path);
        }
    }

    /// <summary>
    /// The share name the remote side shows (as "&lt;name&gt; on &lt;client&gt;"): a drive letter for a
    /// Windows root, the last path segment otherwise, and "root" for <c>/</c>. FreeRDP sanitizes it
    /// further (no slashes) before sending it.
    /// </summary>
    internal static string NameFromPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':'
            && trimmed[2..].Trim('\\', '/').Length == 0)
            return char.ToUpperInvariant(trimmed[0]).ToString();

        var withoutTrailing = trimmed.TrimEnd('\\', '/');
        if (withoutTrailing.Length == 0)
            return "root";

        var lastSeparator = withoutTrailing.LastIndexOfAny(['\\', '/']);
        return lastSeparator < 0 ? withoutTrailing : withoutTrailing[(lastSeparator + 1)..];
    }

    // Two shares with the same name would collide on the remote side; first one wins.
    private static List<RdpDriveShare> Distinct(IEnumerable<RdpDriveShare> shares) =>
        shares.DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
