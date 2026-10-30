using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>
/// The right-click menu on a session tab's header: Rename…, Tab Colour (theme default plus every
/// <see cref="TabColors"/> entry, current one ticked) and Close. Built in code so the colour list comes from
/// <see cref="TabColors.All"/> rather than being repeated in XAML.
/// </summary>
internal static class TabHeaderMenu
{
    public static ContextMenu Build(SessionTabViewModel tab, Action<SessionTabViewModel> rename)
    {
        var renameItem = new MenuItem { Header = "Rename…" };
        renameItem.Click += (_, _) => rename(tab);

        var colour = new MenuItem { Header = "Tab Colour" };
        colour.Items.Add(ColourItem(tab, "Default", null, null));
        colour.Items.Add(new Separator());
        foreach (var (name, color) in TabColors.All)
            colour.Items.Add(ColourItem(tab, name, name, color));

        var close = new MenuItem { Header = "Close", Command = tab.CloseCommand };

        var menu = new ContextMenu();
        menu.Items.Add(renameItem);
        menu.Items.Add(colour);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        return menu;
    }

    private static MenuItem ColourItem(SessionTabViewModel tab, string header, string? name, Color? color)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            GroupName = "TabColour",
            IsChecked = string.Equals(tab.TabColor, name, StringComparison.OrdinalIgnoreCase),
            Command = tab.SetTabColorCommand,
            CommandParameter = name,
        };
        if (color is { } c)
            item.Icon = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(c) };
        return item;
    }
}
