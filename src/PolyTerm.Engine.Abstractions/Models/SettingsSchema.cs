using System.Text.Json;

namespace PolyTerm.Engine;

/// <summary>
/// Describes the configurable options a protocol exposes.
/// The App renders the property grid from this at runtime — no App changes needed per new protocol.
/// </summary>
public sealed class SettingsSchema
{
    public IReadOnlyList<SettingsField> Fields { get; init; } = [];

    /// <summary>Shared instance for protocols with no configurable settings.</summary>
    public static SettingsSchema Empty { get; } = new();
}

/// <summary>Describes a single configurable setting, stored under <see cref="ConnectionProfile.Settings"/>[<see cref="Key"/>].</summary>
public sealed class SettingsField
{
    /// <summary>The key this field is stored under in <see cref="ConnectionProfile.Settings"/>.</summary>
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required SettingsFieldType Type { get; init; }

    /// <summary>Optional grouping label for organizing fields in the settings UI.</summary>
    public string? Group { get; init; }
    public JsonElement? Default { get; init; }
    public bool Required { get; init; }

    /// <summary>Optional regex the value must match, for client-side validation.</summary>
    public string? ValidationPattern { get; init; }
    /// <summary>Capability flag(s) that must be present for this field to be shown/applicable.</summary>
    public ProtocolCapabilities RequiredCapabilities { get; init; } = ProtocolCapabilities.None;
}

/// <summary>Input widget a <see cref="SettingsField"/> should be rendered as.</summary>
public enum SettingsFieldType
{
    Text,
    Number,
    Boolean,
    /// <summary>A closed set of choices — the field's <see cref="SettingsField.ValidationPattern"/> or a protocol-specific convention supplies the choices.</summary>
    Enum,
    /// <summary>A local filesystem path, with a file/folder picker in the UI.</summary>
    FilePath,
    /// <summary>A secret value — rendered masked, never logged.</summary>
    Password,
    /// <summary>A repeatable list of text values.</summary>
    TextList,
}
