using System.Text;
using System.Text.Json;
using PolyTerm.Engine;

namespace PolyTerm.App.Import;

/// <summary>
/// Imports connections from a MobaXterm <c>.mxtsessions</c> file — an INI-style format with no
/// official published spec; this parses the community-documented layout (folders as
/// <c>[Bookmarks]</c>/<c>[Bookmarks_N]</c> sections with a <c>SubRep</c> path, sessions as
/// <c>Name= #icon#type%host%port%username%...#terminalSettings#startMode#comment#tabColor</c>
/// lines) — best-effort, same spirit as <see cref="MRemoteNgImporter"/>. Passwords are never
/// imported (MobaXterm's credential storage isn't worth reverse-engineering for a best-effort
/// importer); users re-enter credentials post-import.
/// </summary>
internal sealed class MobaXtermImporter
{
    /// <summary>Imports from a <c>.mxtsessions</c> file at <paramref name="path"/>.</summary>
    public static ImportResult Import(string path)
    {
        using var stream = File.OpenRead(path);
        return Import(stream);
    }

    /// <summary>
    /// Imports from a <c>.mxtsessions</c> stream. Decoded as Latin-1 (a close approximation of the
    /// format's real Windows-1252 charset, without pulling in the System.Text.Encoding.CodePages
    /// package just for this) — identical to ASCII for the common case of plain host/session names;
    /// non-ASCII names may come through slightly mangled.
    /// </summary>
    public static ImportResult Import(Stream mxtsessions)
    {
        using var reader = new StreamReader(mxtsessions, Encoding.Latin1);

        var profiles   = new List<ConnectionProfile>();
        var skipped    = new List<string>();
        var folderIds  = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        var inBookmarksSection = false;
        string? currentSubRep = null;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var section = line[1..^1];
                inBookmarksSection = section.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase);
                currentSubRep = null;
                continue;
            }

            if (!inBookmarksSection) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key   = line[..eq];
            var value = line[(eq + 1)..];

            if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase))
            {
                currentSubRep = value;
                continue;
            }
            if (key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase))
                continue;

            // Anything else in a Bookmarks section is a session entry: key = session name.
            ParseAndAddSession(key, value, currentSubRep, folderIds, profiles, skipped);
        }

        return new ImportResult(profiles, skipped);
    }

    private static void ParseAndAddSession(string name, string value, string? subRep,
        Dictionary<string, Guid> folderIds, List<ConnectionProfile> profiles, List<string> skipped)
    {
        var parts = value.TrimStart().Split('#');
        if (parts.Length < 3)
        {
            skipped.Add($"{name} (unrecognized format)");
            return;
        }

        var (protocolId, host, port, username) = ParseSessionParams(parts[2]);
        if (protocolId is null)
        {
            skipped.Add($"{name} (unsupported session type)");
            return;
        }

        var parentId = string.IsNullOrEmpty(subRep) ? (Guid?)null : EnsureFolderChain(subRep, folderIds, profiles);

        var settings = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(username))
            settings["username"] = JsonSerializer.SerializeToElement(username);

        profiles.Add(new ConnectionProfile
        {
            Name       = name,
            ProtocolId = protocolId,
            Host       = host,
            Port       = port ?? DefaultPort(protocolId),
            ParentId   = parentId,
            Settings   = settings,
        });
    }

    /// <summary>
    /// <c>typeAndParams</c> is <c>type%param1%param2%...</c> — <c>%</c>-split index 0 is the numeric
    /// session type, index 1 is host, index 2 is port, and (SSH only — the one type this was
    /// confirmed against) index 3 is username. Other session types' parameter layouts are
    /// documented less precisely upstream, so username is deliberately left unset for them rather
    /// than risking a wrong field.
    /// </summary>
    private static (string? ProtocolId, string Host, int? Port, string? Username) ParseSessionParams(string typeAndParams)
    {
        var p = typeAndParams.Split('%');
        if (p.Length == 0) return (null, "", null, null);

        var protocolId = MapSessionType(p[0]);
        var host        = p.Length > 1 ? Unescape(p[1]) : "";
        var port        = p.Length > 2 && int.TryParse(p[2], out var pt) ? pt : (int?)null;
        var username    = protocolId == "ssh" && p.Length > 3 ? Unescape(p[3]) : null;
        return (protocolId, host, port, string.IsNullOrWhiteSpace(username) ? null : username);
    }

    /// <summary>
    /// MobaXterm's numeric session-type codes, mapped to this repo's <see cref="ConnectionProfile.ProtocolId"/>.
    /// Rsh (2) and Xdmcp (3) have no equivalent protocol module here and are skipped rather than
    /// mismapped. SFTP (7) bookmarks map to plain SSH — SFTP is an automatic panel on any connected
    /// SSH session in this app (see docs/storage.md), not a distinct connectable protocol.
    /// </summary>
    private static string? MapSessionType(string code) => code switch
    {
        "0"  => "ssh",
        "1"  => "telnet",
        "4"  => "rdp",
        "5"  => "vnc",
        "7"  => "ssh",
        "11" => "https",
        _    => null,
    };

    private static int DefaultPort(string protocolId) => protocolId switch
    {
        "ssh"    => 22,
        "telnet" => 23,
        "rdp"    => 3389,
        "vnc"    => 5900,
        "https"  => 443,
        _        => 0,
    };

    private static string Unescape(string s) => s
        .Replace("__PIPE__", "|")
        .Replace("__PTVIRG__", ";")
        .Replace("__DBLQUO__", "\"")
        .Replace("_CurrentDrive_", "C:");

    /// <summary>
    /// Creates (or reuses, keyed by full path) folder profiles for each segment of a <c>Folder1\Folder2</c>
    /// SubRep path, depth-first so parents are always added to <paramref name="profiles"/> before
    /// their children — required because <c>SaveAsync</c> uses ParentId as a foreign-key reference.
    /// </summary>
    private static Guid EnsureFolderChain(string subRep, Dictionary<string, Guid> folderIds, List<ConnectionProfile> profiles)
    {
        var segments  = subRep.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        Guid? parentId = null;
        var pathSoFar = "";

        foreach (var segment in segments)
        {
            pathSoFar = pathSoFar.Length == 0 ? segment : $"{pathSoFar}\\{segment}";
            if (!folderIds.TryGetValue(pathSoFar, out var id))
            {
                var folder = new ConnectionProfile { Name = segment, IsFolder = true, ParentId = parentId };
                profiles.Add(folder);
                id = folder.Id;
                folderIds[pathSoFar] = id;
            }
            parentId = id;
        }

        return parentId!.Value;
    }
}
