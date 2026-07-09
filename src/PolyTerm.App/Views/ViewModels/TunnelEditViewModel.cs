using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>Backs the single-tunnel add/edit dialog for a local, remote, or dynamic SSH port forward.</summary>
public sealed partial class TunnelEditViewModel : ViewModelBase
{
    private readonly Guid _id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostPortVisible))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private TunnelKind _kind;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _bindAddress = "127.0.0.1";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _bindPortText = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _destinationHost = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _destinationPortText = "";

    [ObservableProperty] private bool _autoStart = true;

    /// <summary>False for <see cref="TunnelKind.Dynamic"/> forwards, which have no destination host/port to configure.</summary>
    public bool IsHostPortVisible => Kind != TunnelKind.Dynamic;
    public string WindowTitle => _isEditing ? "Edit Tunnel" : "Add Tunnel";
    public IReadOnlyList<TunnelKind> Kinds { get; } = Enum.GetValues<TunnelKind>();

    /// <summary>The tunnel definition produced by <see cref="Save"/>; null until the user confirms.</summary>
    public TunnelDefinition? Result { get; private set; }
    public event EventHandler? RequestClose;

    private readonly bool _isEditing;

    /// <param name="existing">Pass an existing tunnel to open in edit mode; null creates a new one.</param>
    public TunnelEditViewModel(TunnelDefinition? existing)
    {
        _isEditing = existing is not null;
        _id = existing?.Id ?? Guid.NewGuid();

        if (existing is not null)
        {
            _kind                = existing.Kind;
            _name                = existing.Name ?? "";
            _bindAddress         = existing.BindAddress;
            _bindPortText        = existing.BindPort.ToString(CultureInfo.InvariantCulture);
            _destinationHost     = existing.DestinationHost ?? "";
            _destinationPortText = existing.DestinationPort?.ToString(CultureInfo.InvariantCulture) ?? "";
            _autoStart           = existing.AutoStart;
        }
    }

    /// <summary>Builds <see cref="Result"/> from the current form fields and requests the dialog close.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var bindPort = int.Parse(BindPortText, CultureInfo.InvariantCulture);

        Result = new TunnelDefinition
        {
            Id              = _id,
            Kind            = Kind,
            Name            = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            BindAddress     = string.IsNullOrWhiteSpace(BindAddress) ? "127.0.0.1" : BindAddress.Trim(),
            BindPort        = bindPort,
            DestinationHost = Kind == TunnelKind.Dynamic ? null : DestinationHost.Trim(),
            DestinationPort = Kind == TunnelKind.Dynamic ? null : int.Parse(DestinationPortText, CultureInfo.InvariantCulture),
            AutoStart       = AutoStart,
        };
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave()
    {
        if (!IsValidPort(BindPortText)) return false;
        if (Kind == TunnelKind.Dynamic) return true;
        return !string.IsNullOrWhiteSpace(DestinationHost) && IsValidPort(DestinationPortText);
    }

    private static bool IsValidPort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535;
}
