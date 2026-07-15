using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyTerm.App;

/// <summary>User-level app settings, persisted as JSON in a per-OS app-data directory.</summary>
internal sealed class AppSettings
{
    /// <summary>Requested theme variant: "Light", "Dark", or "Default" (system).</summary>
    [JsonPropertyName("themeVariant")]
    public string ThemeVariant { get; set; } = "Dark";

    /// <summary>
    /// Default MCP approval policy for state-changing tools (connection_open, terminal_execute,
    /// terminal_send_keys, sftp_write/delete/rename): "Prompt", "Allow", or "Deny". Individual
    /// connections can override this via their own "mcpPolicy" setting. Defaults to "Prompt" —
    /// nothing is silently auto-approved until explicitly configured otherwise.
    /// </summary>
    [JsonPropertyName("mcpApprovalPolicy")]
    public string McpApprovalPolicy { get; set; } = "Prompt";

    /// <summary>
    /// Per-connection MCP call rate limit (spec §4.7 design rule 4: "a local policy knob, not a
    /// monetization lever" — generous by default, no artificial daily cap). Each pipe connection
    /// gets its own budget; one busy MCP client can't starve another's. 0 or less disables the limit.
    /// </summary>
    [JsonPropertyName("mcpMaxCallsPerMinute")]
    public int McpMaxCallsPerMinute { get; set; } = 120;

    /// <summary>
    /// Which external credential vault provider to enable, if any: "None" (default — only the OS
    /// keychain is available), "KeePass", or "Bitwarden". Both providers exist and work in
    /// <c>PolyTerm.Engine</c> (see <c>KeePassProvider</c>/<c>BitwardenProvider</c>) but were
    /// previously unreachable from the shipped app at all — <see cref="AppCompositionRoot"/> never
    /// set <c>EngineOptions.KeePass</c>/<c>.Bitwarden</c>, so there was no config path, GUI or
    /// otherwise, to turn either on (issue #73).
    /// </summary>
    [JsonPropertyName("vaultProvider")]
    public string VaultProvider { get; set; } = "None";

    /// <summary>Local TCP port KeePassRPC's JSON-RPC endpoint listens on. Only used when <see cref="VaultProvider"/> is "KeePass".</summary>
    [JsonPropertyName("keePassRpcPort")]
    public int KeePassRpcPort { get; set; } = 19455;

    /// <summary>Base URL of the <c>bw serve</c> daemon. Only used when <see cref="VaultProvider"/> is "Bitwarden".</summary>
    [JsonPropertyName("bitwardenBaseUrl")]
    public string BitwardenBaseUrl { get; set; } = "http://localhost:8087";

    /// <summary>
    /// Path to the Bitwarden CLI executable used for the in-app login/unlock flow (issue #78).
    /// Defaults to "bw", resolved via PATH. Only used when <see cref="VaultProvider"/> is "Bitwarden".
    /// </summary>
    [JsonPropertyName("bitwardenCliPath")]
    public string BitwardenCliPath { get; set; } = "bw";

    private static string SettingsPath => Path.Combine(AppPaths.ConfigDirectory, "settings.json");

    /// <summary>Loads settings from disk, falling back to defaults if the file is missing or invalid.</summary>
    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>Persists settings to disk. Failures are swallowed — settings are best-effort.</summary>
    public void Save()
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this)); }
        catch { }
    }
}
