using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Backs the Ctrl+Shift+K quick-connect dialog: parses free-form input like
/// <c>[user@]host[:port]</c> or <c>ssh://...</c> into an ad-hoc, unsaved <see cref="ConnectionProfile"/>.
/// </summary>
public sealed partial class QuickConnectViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _input = "";

    /// <summary>Set when the user confirms; caller opens the session.</summary>
    public ConnectionProfile? ParsedProfile { get; private set; }
    public bool Confirmed { get; private set; }

    public event EventHandler? RequestClose;

    /// <summary>Parses <see cref="Input"/> and, if valid, stores the result in <see cref="ParsedProfile"/> and closes the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        var profile = ParseInput(Input.Trim());
        if (profile is null) return;
        ParsedProfile = profile;
        Confirmed     = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool CanConnect() => ParseInput(Input.Trim()) is not null;

    /// <summary>
    /// Parses <c>[user@]host[:port]</c> or <c>ssh://[user@]host[:port]</c>.
    /// Returns null for empty or unparseable input.
    /// </summary>
    internal static ConnectionProfile? ParseInput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        var s = raw;
        if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            s = s[6..];

        string? user = null;
        string host;
        var port = 22;

        var atIdx = s.IndexOf('@');
        if (atIdx > 0)
        {
            user = s[..atIdx];
            s    = s[(atIdx + 1)..];
        }

        var colonIdx = s.LastIndexOf(':');
        if (colonIdx > 0
            && int.TryParse(s[(colonIdx + 1)..], out var p)
            && p is > 0 and <= 65535)
        {
            host = s[..colonIdx];
            port = p;
        }
        else
        {
            host = s;
        }

        if (string.IsNullOrWhiteSpace(host)) return null;

        var settings = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrEmpty(user))
            settings["username"] = JsonSerializer.SerializeToElement(user);

        var label = user is not null ? $"{user}@{host}" : host;
        if (port != 22) label += $":{port}";

        return new ConnectionProfile
        {
            Name       = label,
            ProtocolId = "ssh",
            Host       = host,
            Port       = port,
            Settings   = settings,
        };
    }
}
