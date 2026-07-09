using System.Text.Json;
using System.Xml.Linq;
using PolyTerm.Engine;

namespace PolyTerm.App.Import;

/// <summary>
/// Imports connections from an mRemoteNG confCons.xml file.
/// Preserves folder hierarchy, host, port, username, and protocol.
/// Attempts to decrypt each connection's "Password" attribute (mRemoteNG ≥ 1.75's AES-256-GCM scheme,
/// via <see cref="MRemoteNgPasswordCrypto"/>) using <paramref name="masterPassword"/> — mRemoteNG's
/// built-in default (<see cref="MRemoteNgPasswordCrypto.DefaultMasterPassword"/>) if none is given.
/// Decrypted plaintexts are returned via <see cref="ImportResult.DecryptedPasswords"/>, keyed by the
/// profile's new <see cref="ConnectionProfile.Id"/> — never attached to the profile itself, since
/// writing a real <see cref="ConnectionProfile.Credential"/> means storing a secret in the OS
/// keychain, an async I/O side effect this pure XML-parsing importer deliberately doesn't own (the
/// caller, which already has an <c>ICredentialWriter</c>, does that — mirrors
/// <c>NewConnectionViewModel</c>'s own credential-write path).
/// Legacy pre-1.75 (Rijndael/CBC) encrypted files aren't supported: their passwords simply fail to
/// decrypt (counted in <see cref="ImportResult.PasswordDecryptFailedCount"/>) rather than crashing.
/// Supported protocols: SSH2 → ssh, SSH1 → ssh, RDP → rdp, VNC → vnc.
/// </summary>
internal sealed class MRemoteNgImporter
{
    /// <summary>Imports from a confCons.xml file at <paramref name="path"/>.</summary>
    public static ImportResult Import(string path, string? masterPassword = null)
    {
        using var stream = File.OpenRead(path);
        return Import(stream, masterPassword);
    }

    /// <summary>Imports from a confCons.xml stream, walking the node tree depth-first so folders precede their children.</summary>
    public static ImportResult Import(Stream xml, string? masterPassword = null)
    {
        XDocument doc;
        try { doc = XDocument.Load(xml); }
        catch (Exception ex) { throw new FormatException("File is not valid XML.", ex); }

        var root = doc.Root
            ?? throw new FormatException("Empty XML document.");

        if (root.Name.LocalName != "Connections")
            throw new FormatException(
                $"Expected root element 'Connections', found '{root.Name.LocalName}'.");

        var profiles           = new List<ConnectionProfile>();
        var skipped            = new List<string>();
        var decryptedPasswords = new Dictionary<Guid, string>();
        var failedCount        = 0;

        WalkNodes(root, parentId: null, masterPassword ?? MRemoteNgPasswordCrypto.DefaultMasterPassword,
            profiles, skipped, decryptedPasswords, ref failedCount);

        return new ImportResult(profiles, skipped, decryptedPasswords, failedCount);
    }

    // Depth-first so parents are always added before their children —
    // required because SaveAsync uses ParentId as a foreign-key reference.
    private static void WalkNodes(XElement parent, Guid? parentId, string masterPassword,
        List<ConnectionProfile> profiles, List<string> skipped,
        Dictionary<Guid, string> decryptedPasswords, ref int failedCount)
    {
        foreach (var node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
        {
            var name = node.Attribute("Name")?.Value ?? "Unnamed";
            var type = node.Attribute("Type")?.Value;

            switch (type)
            {
                case "Container":
                {
                    var folder = new ConnectionProfile
                    {
                        Name     = name,
                        IsFolder = true,
                        ParentId = parentId,
                    };
                    profiles.Add(folder);
                    WalkNodes(node, folder.Id, masterPassword, profiles, skipped, decryptedPasswords, ref failedCount);
                    break;
                }

                case "Connection":
                {
                    var mrProtocol = node.Attribute("Protocol")?.Value ?? "";
                    var protocol   = MapProtocol(mrProtocol);

                    if (protocol is null)
                    {
                        skipped.Add($"{name} (protocol: {mrProtocol})");
                        break;
                    }

                    var host     = node.Attribute("Hostname")?.Value ?? "";
                    var portStr  = node.Attribute("Port")?.Value;
                    var port     = int.TryParse(portStr, out var p) ? p : DefaultPort(protocol);
                    var username = node.Attribute("Username")?.Value ?? "";

                    var settings = new Dictionary<string, JsonElement>();
                    if (!string.IsNullOrWhiteSpace(username))
                        settings["username"] = JsonSerializer.SerializeToElement(username);

                    var profile = new ConnectionProfile
                    {
                        Name       = name,
                        ProtocolId = protocol,
                        Host       = host,
                        Port       = port,
                        ParentId   = parentId,
                        Settings   = settings,
                    };
                    profiles.Add(profile);

                    var encryptedPassword = node.Attribute("Password")?.Value;
                    if (!string.IsNullOrEmpty(encryptedPassword))
                    {
                        var plainText = MRemoteNgPasswordCrypto.TryDecrypt(encryptedPassword, masterPassword);
                        if (plainText is { Length: > 0 })
                            decryptedPasswords[profile.Id] = plainText;
                        else if (plainText is null)
                            failedCount++;
                    }
                    break;
                }

                // Unknown / unsupported types (e.g. PuttySession) — skip silently
            }
        }
    }

    private static string? MapProtocol(string mrn) => mrn switch
    {
        "SSH2"   => "ssh",
        "SSH1"   => "ssh", // obsolete but import as SSH2
        "RDP"    => "rdp",
        "VNC"    => "vnc",
        _ => null,
    };

    private static int DefaultPort(string protocol) => protocol switch
    {
        "ssh" => 22,
        "rdp" => 3389,
        "vnc" => 5900,
        _ => 0,
    };
}

/// <summary>
/// Result of an import: successfully mapped profiles (folders before children), a list of skipped
/// entries with reasons, and (mRemoteNG only — always empty/zero for other importers) any passwords
/// that were decrypted and how many present-but-undecryptable ones were skipped.
/// </summary>
internal sealed record ImportResult(
    IReadOnlyList<ConnectionProfile> Profiles,
    IReadOnlyList<string> Skipped,
    IReadOnlyDictionary<Guid, string>? DecryptedPasswords = null,
    int PasswordDecryptFailedCount = 0)
{
    /// <summary>Profile id → decrypted plaintext password. Never populated by non-mRemoteNG importers.</summary>
    public IReadOnlyDictionary<Guid, string> DecryptedPasswords { get; } = DecryptedPasswords ?? new Dictionary<Guid, string>();
}
