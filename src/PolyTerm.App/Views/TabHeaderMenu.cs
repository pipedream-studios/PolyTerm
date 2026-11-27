using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>
/// The right-click menu on a session tab's header: Rename…, Tab Colour (theme default plus every
/// <see cref="TabColors"/> entry, current one ticked), Copy Error (only while the tab has an error) and Close.
/// Built in code so the colour list comes from <see cref="TabColors.All"/> rather than being repeated in XAML.
/// </summary>
internal static class TabHeaderMenu
{
    public static ContextMenu Build(
        SessionTabViewModel tab, Action<SessionTabViewModel> rename, Action<SessionTabViewModel> copyError)
    {
        var renameItem = new MenuItem { Header = "Rename…" };
        renameItem.Click += (_, _) => rename(tab);

        var colour = new MenuItem { Header = "Tab Colour" };
        colour.Items.Add(ColourItem(tab, "Default", null, null));
        colour.Items.Add(new Separator());
        foreach (var (name, color) in TabColors.All)
            colour.Items.Add(ColourItem(tab, name, name, color));

        var copyErrorItem = new MenuItem { Header = "Copy Error" };
        copyErrorItem.Click += (_, _) => copyError(tab);

        var close = new MenuItem { Header = "Close", Command = tab.CloseCommand };

        var menu = new ContextMenu();
        menu.Items.Add(renameItem);
        menu.Items.Add(colour);
        menu.Items.Add(copyErrorItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);

        // The menu is built once per tab header but the error comes and goes, so check each time it opens.
        void ShowCopyErrorIfAny() => copyErrorItem.IsVisible = !string.IsNullOrEmpty(tab.LastErrorMessage);
        ShowCopyErrorIfAny();
        menu.Opening += (_, _) => ShowCopyErrorIfAny();
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
