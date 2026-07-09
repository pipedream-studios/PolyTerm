using System.Text.Json;

namespace PolyTerm.Engine;

/// <summary>
/// Reads/writes the optional jump-host reference stored under a profile's
/// <c>Settings["jumpHostProfileId"]</c> key. A profile with a jump host connects through that
/// other SSH profile as a bastion; since the referenced profile may itself have a jump host,
/// following this link repeatedly composes an arbitrary-length chain (see
/// <c>SessionManager.ResolveJumpChainAsync</c>).
/// </summary>
public static class JumpHostSettingsExtensions
{
    private const string SettingsKey = "jumpHostProfileId";

    /// <summary>The configured jump-host profile id, or null if this profile connects directly.</summary>
    public static Guid? GetJumpHostProfileId(this Dictionary<string, JsonElement> settings) =>
        settings.TryGetValue(SettingsKey, out var v) && v.ValueKind == JsonValueKind.String
            && Guid.TryParse(v.GetString(), out var id)
            ? id : null;

    /// <summary>Sets or clears (when null) the jump-host profile reference.</summary>
    public static void SetJumpHostProfileId(this Dictionary<string, JsonElement> settings, Guid? profileId)
    {
        if (profileId is null)
        {
            settings.Remove(SettingsKey);
            return;
        }
        settings[SettingsKey] = JsonSerializer.SerializeToElement(profileId.Value.ToString());
    }
}
