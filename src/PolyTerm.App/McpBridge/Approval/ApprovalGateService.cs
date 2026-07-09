using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using PolyTerm.App.Views;
using PolyTerm.Engine;

namespace PolyTerm.App.McpBridge.Approval;

/// <summary>
/// Resolves an MCP action's approval policy — per-connection override (<c>Settings["mcpPolicy"]</c>)
/// first, else the global default (<see cref="AppSettings.McpApprovalPolicy"/>) — and, when the
/// resolved policy is <c>"Prompt"</c> (the default when nothing is explicitly configured on either),
/// blocks on a real modal confirmation shown on the UI thread, reusing the same <see cref="ConfirmDialog"/>
/// <c>SftpBrowserView</c> uses for delete confirmations — with an action-appropriate button label and
/// styling per <see cref="McpAction"/> (e.g. "Delete"/red for <see cref="McpAction.SftpDelete"/>, but
/// a plain "Run"/"Connect"/etc. for non-destructive actions) rather than SFTP's hardcoded "Delete".
/// </summary>
internal sealed class ApprovalGateService : IApprovalGate
{
    private readonly ILogger<ApprovalGateService> _logger;

    public ApprovalGateService(ILogger<ApprovalGateService> logger) => _logger = logger;

    public async Task<bool> CheckAsync(McpAction action, ConnectionProfile profile, string detail, CancellationToken ct = default)
    {
        var policy = ResolvePolicy(profile);

        var decision = policy switch
        {
            "Allow" => true,
            "Deny"  => false,
            _       => await PromptAsync(action, profile, detail, ct).ConfigureAwait(false), // "Prompt" or anything unrecognized
        };

        _logger.LogInformation("MCP approval: {Action} on '{Profile}' -> {Decision} (policy={Policy}): {Detail}",
            action, profile.Name, decision ? "allowed" : "denied", policy, detail);

        return decision;
    }

    /// <summary>
    /// Per-connection <c>Settings["mcpPolicy"]</c> wins if it's explicitly "Allow"/"Deny"/"Prompt".
    /// Otherwise falls back to the global default. Note: unlike Host/Port/Credential, profile Settings
    /// don't currently cascade through folders (<c>InheritanceMask.Settings</c> is declared but not
    /// implemented by <c>ProfileStore.ApplyInheritanceAsync</c>) — this only ever looks at the
    /// resolved profile's own settings, not an ancestor folder's.
    /// Internal (not private) so <c>PolyTerm.App.Tests</c> can exercise it directly.
    /// </summary>
    internal static string ResolvePolicy(ConnectionProfile profile)
    {
        if (profile.Settings.TryGetValue("mcpPolicy", out var element) && element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (value is "Allow" or "Deny" or "Prompt")
                return value;
        }

        return App.Settings.McpApprovalPolicy;
    }

    private static async Task<bool> PromptAsync(McpAction action, ConnectionProfile profile, string detail, CancellationToken ct)
    {
        var title   = "MCP approval required";
        var message = $"An AI agent wants to {Describe(action)} on \"{profile.Name}\":\n\n{detail}";

        var (actionLabel, destructive) = ButtonStyleFor(action);

        // Avalonia's Dispatcher.InvokeAsync<Task<TResult>> flattens the inner task automatically —
        // this already awaits the dialog closing, not just the callback being dispatched.
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            return owner is null
                ? Task.FromResult(false) // no window to own the dialog — fail closed rather than block forever
                : new ConfirmDialog(title, message, actionLabel, destructive).ShowDialog<bool>(owner);
        }).ConfigureAwait(false);
    }

    private static string Describe(McpAction action) => action switch
    {
        McpAction.ConnectionOpen    => "open a new connection",
        McpAction.TerminalExecute   => "run a command",
        McpAction.TerminalSendKeys  => "send keystrokes",
        McpAction.SftpWrite         => "write a file",
        McpAction.SftpDelete        => "delete a file",
        McpAction.SftpRename        => "rename/move a file",
        McpAction.FramebufferClick  => "click on the remote desktop",
        McpAction.FramebufferType   => "type text into the remote desktop",
        McpAction.FramebufferKey    => "send a key to the remote desktop",
        _                           => "perform an action",
    };

    /// <summary>Only <see cref="McpAction.SftpDelete"/> is actually destructive — everything else gets a plain button instead of <see cref="ConfirmDialog"/>'s default "Delete"/red styling.</summary>
    private static (string ActionLabel, bool Destructive) ButtonStyleFor(McpAction action) => action switch
    {
        McpAction.ConnectionOpen    => ("Connect", false),
        McpAction.TerminalExecute   => ("Run", false),
        McpAction.TerminalSendKeys  => ("Send", false),
        McpAction.SftpWrite         => ("Write", false),
        McpAction.SftpDelete        => ("Delete", true),
        McpAction.SftpRename        => ("Rename", false),
        McpAction.FramebufferClick  => ("Click", false),
        McpAction.FramebufferType   => ("Type", false),
        McpAction.FramebufferKey    => ("Send", false),
        _                           => ("Approve", false),
    };
}
