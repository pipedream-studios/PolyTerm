using System.Text.Json;

namespace PolyTerm.Engine;

/// <summary>
/// A saved (or transient) connection — either a leaf profile or a folder node in the connection
/// tree. Protocol-specific settings (SSH key path, tunnels, jump host, etc.) live opaquely in
/// <see cref="Settings"/> rather than as first-class properties, so new protocols never require
/// changes to this type.
/// </summary>
public sealed class ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The containing folder's id, or null at the root. Also the basis for property inheritance — see <see cref="Inherit"/>.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>True if this node is a folder (organizational only) rather than a connectable profile.</summary>
    public bool IsFolder { get; init; }
    public required string Name { get; set; }

    /// <summary>Protocol id matching an <see cref="IProtocolDescriptor.Id"/>, e.g. "ssh". Empty for folders.</summary>
    public string ProtocolId { get; set; } = string.Empty;
    public string? Host { get; set; }
    public int? Port { get; set; }

    /// <summary>Reference to the resolved secret, if any — never the secret itself.</summary>
    public CredentialReference? Credential { get; set; }

    /// <summary>Opaque, protocol-specific settings (e.g. SSH's "username"/"privateKeyPath"/"tunnels"/"jumpHostProfileId"), validated against the owning protocol's <see cref="SettingsSchema"/>.</summary>
    public Dictionary<string, JsonElement> Settings { get; set; } = [];

    /// <summary>Which properties this profile defers to its parent folder for, resolved at connect time by <see cref="IProfileStore.ResolveAsync"/>.</summary>
    public InheritanceMask Inherit { get; set; } = InheritanceMask.None;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Flags selecting which properties of a <see cref="ConnectionProfile"/> defer to its parent folder.</summary>
[Flags]
public enum InheritanceMask
{
    None       = 0,
    Host       = 1 << 0,
    Port       = 1 << 1,
    Credential = 1 << 2,
    Settings   = 1 << 3,
    All        = Host | Port | Credential | Settings,
}
