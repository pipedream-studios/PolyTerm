using System.Globalization;
using System.Text.Json;
using PolyTerm.Engine;

namespace PolyTerm.App.Macros;

/// <summary>
/// Expands the fixed built-in variable tokens in a macro body. Variables always resolve against
/// the active tab's profile/title — there is no per-session resolution for a broadcast macro,
/// since a broadcast has no single well-defined profile to draw from.
/// </summary>
internal static class MacroExpander
{
    public static string Expand(string template, ConnectionProfile? profile, string? sessionTitle)
    {
        if (profile is null)
            return template.Replace("{{sessionTitle}}", sessionTitle ?? "");

        return template
            .Replace("{{host}}", profile.Host ?? "")
            .Replace("{{port}}", profile.Port?.ToString(CultureInfo.InvariantCulture) ?? "")
            .Replace("{{name}}", profile.Name)
            .Replace("{{username}}", GetUsername(profile))
            .Replace("{{sessionTitle}}", sessionTitle ?? "");
    }

    // Mirrors SshSession.ConnectAsync's own username fallback, since SSH stores username in
    // Settings rather than as a top-level ConnectionProfile property.
    private static string GetUsername(ConnectionProfile profile) =>
        profile.Settings.TryGetValue("username", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? Environment.UserName
            : Environment.UserName;
}
