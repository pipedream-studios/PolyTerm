using Avalonia;
using Avalonia.Controls;

namespace PolyTerm.App.Views;

/// <summary>
/// A session tab's content grid: the session surface (row 0), the SFTP divider (row 1) and the SFTP panel
/// (row 2). Row 2 is a real pixel height driven by <see cref="SftpPanelOpen"/> through a
/// <see cref="CollapsibleRow"/>, so the panel fills it exactly as the divider is dragged and closing the
/// panel gives the surface the whole tab. Lives in a DataTemplate, hence a control rather than code-behind.
/// </summary>
public sealed class SessionContentGrid : Grid
{
    public const double DefaultSftpHeight = 250;
    public const double MinSftpHeight     = 150;
    public const double MinSurfaceHeight  = 100;

    public static readonly StyledProperty<bool> SftpPanelOpenProperty =
        AvaloniaProperty.Register<SessionContentGrid, bool>(nameof(SftpPanelOpen));

    private readonly CollapsibleRow _sftpRow;

    public SessionContentGrid()
    {
        RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = MinSurfaceHeight });
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        RowDefinitions.Add(new RowDefinition(0, GridUnitType.Pixel));
        _sftpRow = new CollapsibleRow(RowDefinitions[2], DefaultSftpHeight, MinSftpHeight);
    }

    /// <summary>Whether the SFTP panel row is open (bound to the tab's SftpPanelOpen).</summary>
    public bool SftpPanelOpen
    {
        get => GetValue(SftpPanelOpenProperty);
        set => SetValue(SftpPanelOpenProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SftpPanelOpenProperty)
            _sftpRow.SetVisible(change.GetNewValue<bool>());
    }
}
