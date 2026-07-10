using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;
using PolyTerm.Protocols.Ssh.X11;

namespace PolyTerm.App.ViewModels;

/// <summary>A folder choice in the "Save to folder" picker; <paramref name="Id"/> is null for the root/no-folder option.</summary>
public sealed record FolderItem(Guid? Id, string DisplayName);

/// <summary>A jump-host choice in the "Connect via" picker; <paramref name="Id"/> is null for the no-jump-host option.</summary>
public sealed record JumpHostItem(Guid? Id, string DisplayName);

/// <summary>A protocol choice in the "Protocol" picker at the top of the dialog.</summary>
public sealed record ProtocolItem(string Id, string DisplayName, int DefaultPort, bool IsAvailable, string? UnavailableReason);

/// <summary>Backs the New/Edit Connection dialog: edits a <see cref="ConnectionProfile"/>'s fields and persists it via <see cref="IProfileStore"/>. Field visibility and <see cref="SaveAsync"/>'s settings branch on <see cref="SelectedProtocol"/>.</summary>
public sealed partial class NewConnectionViewModel : ViewModelBase
{
    private readonly IProfileStore _store;
    private readonly ConnectionTreeViewModel _tree;
    private readonly ConnectionProfile? _existing;
    private readonly ICredentialWriter? _credWriter;

    [ObservableProperty] private IReadOnlyList<ProtocolItem> _protocols = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(IsSshSelected))]
    [NotifyPropertyChangedFor(nameof(IsRdpSelected))]
    [NotifyPropertyChangedFor(nameof(IsWebSelected))]
    [NotifyPropertyChangedFor(nameof(IsSerialSelected))]
    [NotifyPropertyChangedFor(nameof(IsVncSelected))]
    [NotifyPropertyChangedFor(nameof(ShowCredentialWarning))]
    [NotifyPropertyChangedFor(nameof(ShowX11ServerWarning))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private ProtocolItem? _selectedProtocol;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _host = "";

    /// <summary>Raw text for the port field; parsed (falling back to the selected protocol's default if invalid) in <see cref="SaveAsync"/>.</summary>
    [ObservableProperty] private string _portText = "22";
    [ObservableProperty] private string _username = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCredentialWarning))]
    private string _privateKeyPath = "";

    /// <summary>RDP-only: the Windows domain to authenticate against, if any.</summary>
    [ObservableProperty] private string _domain = "";

    /// <summary>Plaintext password entered by the user; only ever held in memory here and written to the OS keychain by <see cref="SaveAsync"/>, never persisted directly on the profile.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCredentialWarning))]
    private string _password = "";

    // Serial-only fields — a serial connection addresses a COM port + line settings, not a Host/Port.
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _serialPortName = "";
    [ObservableProperty] private string _baudRateText = "9600";
    [ObservableProperty] private string _dataBitsText = "8";
    [ObservableProperty] private string _selectedParity = "None";
    [ObservableProperty] private string _selectedStopBits = "One";

    public IReadOnlyList<string> ParityOptions { get; } = ["None", "Odd", "Even", "Mark", "Space"];
    public IReadOnlyList<string> StopBitsOptions { get; } = ["One", "OnePointFive", "Two"];

    /// <summary>MCP approval-policy override for this connection — "Inherit" defers to the global default (see the app's MCP menu). Universal, not protocol-gated: every connection has a policy, whether or not it sets one explicitly.</summary>
    [ObservableProperty] private string _selectedMcpPolicy = "Inherit";
    public IReadOnlyList<string> McpPolicyOptions { get; } = ["Inherit", "Prompt", "Allow", "Deny"];

    [ObservableProperty] private IReadOnlyList<FolderItem> _folders = [];
    [ObservableProperty] private FolderItem? _selectedFolder;

    [ObservableProperty] private IReadOnlyList<JumpHostItem> _jumpHosts = [];
    [ObservableProperty] private JumpHostItem? _selectedJumpHost;

    private List<TunnelDefinition> _tunnels = [];

    /// <summary>SSH-only: forwards X11 so remote graphical apps display on the local X server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowX11ServerWarning))]
    private bool _x11Forwarding;

    /// <summary>
    /// Set asynchronously by <see cref="CheckX11ServerReachabilityAsync"/> — never computed inline in a
    /// property getter, since the reachability check is a real (if brief) socket connect and shouldn't
    /// block the UI thread just to read a property.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowX11ServerWarning))]
    private bool _x11ServerUnreachable;

    public bool ShowX11ServerWarning => IsSshSelected && X11Forwarding && X11ServerUnreachable;

    public string X11ServerWarningMessage { get; } = X11ServerGuidance.BuildUnreachableMessage(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows), RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

    // Set by NewConnectionWindow.OnDataContextChanged to avoid pulling Window into the VM
    /// <summary>Opens the tunnel list editor for the current tunnel set and returns the edited list, or null if cancelled.</summary>
    public Func<List<TunnelDefinition>, Task<List<TunnelDefinition>?>>? OpenTunnelsEditorAsync { get; set; }

    public string WindowTitle =>
        (_existing is null ? "New " : "Edit ") + (SelectedProtocol?.DisplayName ?? "Connection");

    public bool IsSshSelected => SelectedProtocol?.Id == "ssh";
    public bool IsRdpSelected => SelectedProtocol?.Id == "rdp";
    public bool IsWebSelected => SelectedProtocol?.Id is "http" or "https";
    public bool IsSerialSelected => SelectedProtocol?.Id == "serial";
    public bool IsVncSelected => SelectedProtocol?.Id == "vnc";

    public bool HasExistingCredential => _existing?.Credential is not null;

    /// <summary>True when no password, key path, or existing stored credential is present — prompts the user that the connection will require manual auth. Never shown for protocols with no credential concept in this dialog (Serial).</summary>
    public bool ShowCredentialWarning =>
        !IsSerialSelected &&
        string.IsNullOrWhiteSpace(Password) &&
        string.IsNullOrWhiteSpace(PrivateKeyPath) &&
        !HasExistingCredential;

    /// <summary>True once <see cref="SaveAsync"/> has committed the profile; checked by the view's close handler.</summary>
    public bool Saved { get; private set; }

    public event EventHandler? RequestClose;

    /// <param name="existing">Pass an existing profile to open in edit mode; null creates a new one.</param>
    /// <param name="credWriter">Optional writer for persisting passwords to the OS keychain.</param>
    public NewConnectionViewModel(IProfileStore store, ConnectionTreeViewModel tree, IProtocolRegistry registry,
                                   ConnectionProfile? existing = null, ICredentialWriter? credWriter = null)
    {
        _store      = store;
        _tree       = tree;
        _existing   = existing;
        _credWriter = credWriter;

        // Only protocols with their own connection-profile shape belong in this picker —
        // local shell is opened directly (no host/port/credential) via its own toolbar action.
        _protocols = registry.All
            .Where(d => d.Id != "local-shell")
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new ProtocolItem(d.Id, d.DisplayName, d.DefaultPort, d.IsAvailable(out var reason), reason))
            .ToList();

        if (existing is not null)
        {
            _name           = existing.Name;
            _host           = existing.Host ?? "";
            _portText       = (existing.Port ?? 22).ToString(CultureInfo.InvariantCulture);
            _username       = GetSetting(existing.Settings, "username") ?? "";
            _privateKeyPath = GetSetting(existing.Settings, "privateKeyPath") ?? "";
            _domain         = GetSetting(existing.Settings, "domain") ?? "";
            _tunnels        = existing.Settings.GetTunnels();
            _serialPortName = GetSetting(existing.Settings, "portName") ?? existing.Host ?? "";
            _baudRateText   = GetNumberSetting(existing.Settings, "baudRate")?.ToString(CultureInfo.InvariantCulture) ?? "9600";
            _dataBitsText   = GetNumberSetting(existing.Settings, "dataBits")?.ToString(CultureInfo.InvariantCulture) ?? "8";
            _selectedParity = GetSetting(existing.Settings, "parity") ?? "None";
            _selectedStopBits = GetSetting(existing.Settings, "stopBits") ?? "One";
            _selectedMcpPolicy = GetSetting(existing.Settings, "mcpPolicy") ?? "Inherit";
            _x11Forwarding  = GetBoolSetting(existing.Settings, "x11Forwarding");
            _selectedProtocol = _protocols.FirstOrDefault(p => p.Id == existing.ProtocolId) ?? FirstProtocolOrDefault();
        }
        else
        {
            _selectedProtocol = _protocols.FirstOrDefault(p => p.Id == "ssh") ?? FirstProtocolOrDefault();
            if (_selectedProtocol is { DefaultPort: > 0 } proto)
                _portText = proto.DefaultPort.ToString(CultureInfo.InvariantCulture);
        }

        _ = LoadFoldersAsync();
        if (_x11Forwarding)
            _ = CheckX11ServerReachabilityAsync();
    }

    /// <summary>Re-checked whenever the user toggles X11 forwarding on, so the warning (if any) appears immediately rather than only at connect time.</summary>
    partial void OnX11ForwardingChanged(bool value)
    {
        if (value) _ = CheckX11ServerReachabilityAsync();
        else X11ServerUnreachable = false;
    }

    /// <summary>
    /// A plain TCP/Unix-socket connect attempt against the resolved DISPLAY, off the UI thread — see
    /// <see cref="X11DisplayEndpoint.IsReachable"/>. Best-effort: any resolution failure (a malformed
    /// DISPLAY value, say) is treated the same as "unreachable" rather than surfaced as a separate
    /// error, since the point of this check is just deciding whether to show install guidance.
    /// </summary>
    private async Task CheckX11ServerReachabilityAsync()
    {
        var reachable = await Task.Run(() =>
        {
            try { return X11DisplayEndpoint.ResolveFromEnvironment().IsReachable(TimeSpan.FromMilliseconds(300)); }
            catch { return false; }
        }).ConfigureAwait(true);
        X11ServerUnreachable = !reachable;
    }

    private ProtocolItem? FirstProtocolOrDefault() => Protocols.Count > 0 ? Protocols[0] : null;

    /// <summary>Prefills the port field with the newly selected protocol's default — only for a new (unsaved) connection, so switching protocols while editing an existing one never clobbers its stored port.</summary>
    partial void OnSelectedProtocolChanged(ProtocolItem? value)
    {
        if (_existing is null && value is { DefaultPort: > 0 })
            PortText = value.DefaultPort.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Opens the tunnel list editor via <see cref="OpenTunnelsEditorAsync"/> and replaces the in-memory tunnel set if confirmed.</summary>
    [RelayCommand]
    private async Task EditTunnelsAsync()
    {
        if (OpenTunnelsEditorAsync is null) return;
        var result = await OpenTunnelsEditorAsync(_tunnels).ConfigureAwait(true);
        if (result is not null) _tunnels = result;
    }

    /// <summary>Loads all profiles from the store to populate the folder and jump-host pickers, preselecting the existing profile's folder/jump-host when editing.</summary>
    private async Task LoadFoldersAsync()
    {
        var all = await _store.GetAllAsync().ConfigureAwait(false);
        var byId = all.ToDictionary(p => p.Id);

        var root = new FolderItem(null, "— Root —");
        var items = new List<FolderItem> { root };

        items.AddRange(
            all.Where(p => p.IsFolder)
               .OrderBy(p => BuildFolderPath(p, byId))
               .Select(p => new FolderItem(p.Id, BuildFolderPath(p, byId))));

        var preselect = _existing?.ParentId is { } pid
            ? items.FirstOrDefault(f => f.Id == pid) ?? root
            : root;

        var noneJumpHost = new JumpHostItem(null, "— None —");
        var jumpItems = new List<JumpHostItem> { noneJumpHost };
        jumpItems.AddRange(
            all.Where(p => !p.IsFolder && p.ProtocolId == "ssh" && p.Id != _existing?.Id)
               .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
               .Select(p => new JumpHostItem(p.Id, p.Name)));

        var jumpPreselect = _existing?.Settings.GetJumpHostProfileId() is { } jhId
            ? jumpItems.FirstOrDefault(j => j.Id == jhId) ?? noneJumpHost
            : noneJumpHost;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Folders          = items;
            SelectedFolder   = preselect;
            JumpHosts        = jumpItems;
            SelectedJumpHost = jumpPreselect;
        });
    }

    /// <summary>
    /// Builds a <see cref="ConnectionProfile"/> from the form fields, writes a new password to the
    /// keychain via <see cref="ICredentialWriter"/> if one was entered (otherwise keeps the existing
    /// stored credential when editing), persists the profile, and reloads the connection tree.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var protocolId = SelectedProtocol?.Id ?? "ssh";
        var defaultPort = SelectedProtocol?.DefaultPort is > 0 ? SelectedProtocol.DefaultPort : 22;
        var port = int.TryParse(PortText, out var p) && p is > 0 and <= 65535 ? p : defaultPort;

        var settings = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(Username) && !IsWebSelected && !IsSerialSelected && !IsVncSelected)
            settings["username"] = JsonSerializer.SerializeToElement(Username.Trim());

        if (IsRdpSelected)
        {
            if (!string.IsNullOrWhiteSpace(Domain))
                settings["domain"] = JsonSerializer.SerializeToElement(Domain.Trim());
        }
        else if (IsSshSelected)
        {
            if (!string.IsNullOrWhiteSpace(PrivateKeyPath))
                settings["privateKeyPath"] = JsonSerializer.SerializeToElement(PrivateKeyPath.Trim());
            settings.SetTunnels(_tunnels);
            settings.SetJumpHostProfileId(SelectedJumpHost?.Id);
            if (X11Forwarding)
                settings["x11Forwarding"] = JsonSerializer.SerializeToElement(true);
        }
        else if (IsSerialSelected)
        {
            settings["portName"] = JsonSerializer.SerializeToElement(SerialPortName.Trim());
            if (int.TryParse(BaudRateText, out var baud) && baud > 0)
                settings["baudRate"] = JsonSerializer.SerializeToElement(baud);
            if (int.TryParse(DataBitsText, out var dataBits) && dataBits is > 0 and <= 8)
                settings["dataBits"] = JsonSerializer.SerializeToElement(dataBits);
            settings["parity"]   = JsonSerializer.SerializeToElement(SelectedParity);
            settings["stopBits"] = JsonSerializer.SerializeToElement(SelectedStopBits);
        }
        // Web and Telnet/raw/rlogin (nothing beyond Host/Port/Username, already set above) fall
        // through here with no extra protocol-specific settings.

        // Universal, not protocol-gated — every connection can override the global MCP policy.
        // "Inherit" means no override, so it's simply omitted rather than stored as a marker value.
        if (SelectedMcpPolicy != "Inherit")
            settings["mcpPolicy"] = JsonSerializer.SerializeToElement(SelectedMcpPolicy);

        var profile = _existing ?? new ConnectionProfile { Name = "", ProtocolId = protocolId };
        profile.ProtocolId = protocolId;
        profile.Name     = Name.Trim();
        profile.Host     = IsSerialSelected ? SerialPortName.Trim() : Host.Trim();
        profile.Port     = IsSerialSelected ? null : port;
        profile.Settings = settings;
        profile.ParentId = SelectedFolder?.Id;

        if (!string.IsNullOrEmpty(Password) && _credWriter is not null)
        {
            var key = $"PolyTerm/{profile.Id}";
            profile.Credential = await _credWriter.WriteAsync(key, Username.Trim(), Password).ConfigureAwait(false);
        }
        else if (_existing?.Credential is not null && string.IsNullOrEmpty(Password))
        {
            profile.Credential = _existing.Credential;
        }

        await _store.SaveAsync(profile).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            async () => await _tree.LoadCommand.ExecuteAsync(null));

        Saved = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave() =>
        !string.IsNullOrWhiteSpace(Name) &&
        (IsSerialSelected ? !string.IsNullOrWhiteSpace(SerialPortName) : !string.IsNullOrWhiteSpace(Host)) &&
        SelectedProtocol is { IsAvailable: true };

    private static string? GetSetting(Dictionary<string, JsonElement> s, string key) =>
        s.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetNumberSetting(Dictionary<string, JsonElement> s, string key) =>
        s.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool GetBoolSetting(Dictionary<string, JsonElement> s, string key) =>
        s.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static string BuildFolderPath(ConnectionProfile folder,
                                          IReadOnlyDictionary<Guid, ConnectionProfile> byId)
    {
        var parts = new List<string>();
        var current = folder;
        while (current is not null)
        {
            parts.Insert(0, current.Name);
            current = current.ParentId.HasValue && byId.TryGetValue(current.ParentId.Value, out var p) ? p : null;
        }
        return string.Join(" / ", parts);
    }
}
