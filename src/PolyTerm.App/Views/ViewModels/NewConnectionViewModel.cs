using System.Globalization;
using System.IO;
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

/// <summary>Selected entry from the credential vault picker: the raw provider metadata plus the provider's display name, so the caller doesn't need to re-look-up the provider just to label its choice.</summary>
public sealed record CredentialPickResult(CredentialSummary Summary, string ProviderDisplayName);

/// <summary>One checkbox in the RDP "Redirect Local Drives" list — the way <c>mstsc.exe</c>'s own "Local Devices and Resources" dialog lets you pick specific drives instead of all-or-nothing.</summary>
public sealed partial class RdpDriveOption : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;

    public RdpDriveOption(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }
}

/// <summary>Backs the New/Edit Connection dialog: edits a <see cref="ConnectionProfile"/>'s fields and persists it via <see cref="IProfileStore"/>. Field visibility and <see cref="SaveAsync"/>'s settings branch on <see cref="SelectedProtocol"/>.</summary>
public sealed partial class NewConnectionViewModel : ViewModelBase
{
    private readonly IProfileStore _store;
    private readonly ConnectionTreeViewModel _tree;
    private readonly ConnectionProfile? _existing;
    private readonly ICredentialWriter? _credWriter;

    /// <summary>A credential explicitly picked via <see cref="BrowseCredentialAsync"/> — takes priority over <see cref="Password"/> and any existing stored credential in <see cref="SaveAsync"/>.</summary>
    private CredentialReference? _pickedCredential;

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

    /// <summary>HTTP/S-only: opens the connection in the OS default browser instead of an embedded WebView tab.</summary>
    [ObservableProperty] private bool _openExternally;

    /// <summary>Plaintext password entered by the user; only ever held in memory here and written to the OS keychain by <see cref="SaveAsync"/>, never persisted directly on the profile.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCredentialWarning))]
    private string _password = "";

    /// <summary>Label for whatever <see cref="_pickedCredential"/> currently holds (e.g. "Bitwarden: prod-server (root)"), or null if nothing's been picked via Browse yet.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCredentialWarning))]
    private string? _selectedCredentialLabel;

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

    /// <summary>RDP-only: which local drives to redirect, the way mstsc.exe's own UI lets you pick specific drives instead of all-or-nothing. Populated from the local machine's actual drives (real, not a static list).</summary>
    [ObservableProperty] private IReadOnlyList<RdpDriveOption> _rdpDriveOptions = [];

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

    /// <summary>Opens the credential vault picker dialog and returns the chosen entry, or null if cancelled.</summary>
    public Func<Task<CredentialPickResult?>>? OpenCredentialPickerAsync { get; set; }

    public string WindowTitle =>
        (_existing is null ? "New " : "Edit ") + (SelectedProtocol?.DisplayName ?? "Connection");

    public bool IsSshSelected => SelectedProtocol?.Id == "ssh";
    public bool IsRdpSelected => SelectedProtocol?.Id == "rdp";
    public bool IsWebSelected => SelectedProtocol?.Id is "http" or "https";
    public bool IsSerialSelected => SelectedProtocol?.Id == "serial";
    public bool IsVncSelected => SelectedProtocol?.Id == "vnc";

    public bool HasExistingCredential => _existing?.Credential is not null;

    /// <summary>Whether at least one credential provider is registered (always true — the OS keychain is always registered — but kept explicit so the Browse button can be disabled if that ever changes).</summary>
    public bool HasCredentialProviders => CredentialProviders.Count > 0;

    /// <summary>Credential providers available to <see cref="BrowseCredentialAsync"/>, resolved once at construction time.</summary>
    public IReadOnlyList<ICredentialProvider> CredentialProviders { get; }

    /// <summary>True when no password, key path, existing stored credential, or vault-browsed credential is present — prompts the user that the connection will require manual auth. Never shown for protocols with no credential concept in this dialog (Serial).</summary>
    public bool ShowCredentialWarning =>
        !IsSerialSelected &&
        string.IsNullOrWhiteSpace(Password) &&
        string.IsNullOrWhiteSpace(PrivateKeyPath) &&
        !HasExistingCredential &&
        _pickedCredential is null;

    /// <summary>True once <see cref="SaveAsync"/> has committed the profile; checked by the view's close handler.</summary>
    public bool Saved { get; private set; }

    public event EventHandler? RequestClose;

    /// <param name="existing">Pass an existing profile to open in edit mode; null creates a new one.</param>
    /// <param name="credWriter">Optional writer for persisting passwords to the OS keychain.</param>
    /// <param name="initialProtocolId">Pre-selects this protocol for a brand-new connection (e.g. from File &gt; New Connection &gt; RDP); ignored when <paramref name="existing"/> is set, and falls back to the default "ssh" selection if the id doesn't match any registered protocol.</param>
    /// <param name="credentialProviders">Providers available to the "Browse Vault…" picker (issue #73) — the OS keychain, plus KeePass/Bitwarden if enabled. Defaults to none.</param>
    public NewConnectionViewModel(IProfileStore store, ConnectionTreeViewModel tree, IProtocolRegistry registry,
                                   ConnectionProfile? existing = null, ICredentialWriter? credWriter = null,
                                   string? initialProtocolId = null,
                                   IReadOnlyList<ICredentialProvider>? credentialProviders = null)
    {
        _store      = store;
        _tree       = tree;
        _existing   = existing;
        _credWriter = credWriter;
        CredentialProviders = credentialProviders ?? [];

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
            _openExternally = GetBoolSetting(existing.Settings, "openExternally");
            _rdpDriveOptions = BuildRdpDriveOptions(GetStringArraySetting(existing.Settings, "redirectedDrives"));
            _selectedProtocol = _protocols.FirstOrDefault(p => p.Id == existing.ProtocolId) ?? FirstProtocolOrDefault();
        }
        else
        {
            _rdpDriveOptions = BuildRdpDriveOptions([]);
            // Deliberately "ssh" specifically, not "whatever's first in Protocols" (that list is sorted
            // alphabetically by DisplayName, so index 0 is actually "HTTP") - SSH is the protocol this
            // app manages most, so a brand-new connection should default to it rather than to wherever
            // it happens to sort. Worth knowing before assuming the picker starts at the top of its own
            // dropdown - see issue #26, filed after this exact assumption silently overshot the intended
            // selection in a test. initialProtocolId overrides this when the caller already knows which
            // protocol the user wants (e.g. File > New Connection > RDP), so that path doesn't require an
            // extra manual ComboBox switch after opening.
            _selectedProtocol = _protocols.FirstOrDefault(p => p.Id == initialProtocolId)
                ?? _protocols.FirstOrDefault(p => p.Id == "ssh")
                ?? FirstProtocolOrDefault();
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

    /// <summary>
    /// Opens the credential vault picker via <see cref="OpenCredentialPickerAsync"/> and, if the user
    /// selected an entry, records it as <see cref="_pickedCredential"/> — takes priority over
    /// <see cref="Password"/> in <see cref="SaveAsync"/>, and pre-fills <see cref="Username"/> from the
    /// entry if that field is still empty.
    /// </summary>
    [RelayCommand]
    private async Task BrowseCredentialAsync()
    {
        if (OpenCredentialPickerAsync is null) return;
        var result = await OpenCredentialPickerAsync().ConfigureAwait(true);
        if (result is null) return;

        _pickedCredential = new CredentialReference(result.Summary.ProviderId, result.Summary.ItemRef);
        SelectedCredentialLabel = string.IsNullOrEmpty(result.Summary.Username)
            ? $"{result.ProviderDisplayName}: {result.Summary.DisplayName}"
            : $"{result.ProviderDisplayName}: {result.Summary.DisplayName} ({result.Summary.Username})";

        if (string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(result.Summary.Username))
            Username = result.Summary.Username;
    }

    /// <summary>Discards a credential picked via <see cref="BrowseCredentialAsync"/>, reverting to whatever's typed in <see cref="Password"/> (or the existing stored credential, if editing).</summary>
    [RelayCommand]
    private void ClearCredential()
    {
        _pickedCredential = null;
        SelectedCredentialLabel = null;
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

            // Selecting one or more drives here is what turns redirection on at all — no separate
            // master checkbox in this dialog, matching how RdpDriveOptions is presented as a single
            // mstsc.exe-style checklist rather than a checklist gated behind its own toggle.
            var selectedDrives = RdpDriveOptions.Where(d => d.IsSelected).Select(d => d.Name).ToList();
            settings["redirectDrives"] = JsonSerializer.SerializeToElement(selectedDrives.Count > 0);
            if (selectedDrives.Count > 0)
                settings["redirectedDrives"] = JsonSerializer.SerializeToElement(selectedDrives);
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
        else if (IsWebSelected)
        {
            if (OpenExternally)
                settings["openExternally"] = JsonSerializer.SerializeToElement(true);
        }
        // Telnet/raw/rlogin (nothing beyond Host/Port/Username, already set above) fall through
        // here with no extra protocol-specific settings.

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

        if (_pickedCredential is not null)
        {
            profile.Credential = _pickedCredential;
        }
        else if (!string.IsNullOrEmpty(Password) && _credWriter is not null)
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

    /// <summary>The local machine's actual drives (real, not a static/guessed list), pre-selecting whichever ones <paramref name="selectedNames"/> (the profile's persisted choice, if editing) already named.</summary>
    private static IReadOnlyList<RdpDriveOption> BuildRdpDriveOptions(IReadOnlyList<string> selectedNames)
    {
        string[] driveNames;
        try { driveNames = DriveInfo.GetDrives().Select(d => d.Name).ToArray(); }
        catch { driveNames = []; }

        return driveNames
            .Select(name => new RdpDriveOption(name, selectedNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string? GetSetting(Dictionary<string, JsonElement> s, string key) =>
        s.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> GetStringArraySetting(Dictionary<string, JsonElement> s, string key)
    {
        if (!s.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        var result = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } str)
                result.Add(str);
        return result;
    }

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
