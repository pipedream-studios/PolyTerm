using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PolyTerm.App.Views;

/// <summary>
/// Builds a <see cref="NativeMenu"/> (the macOS global menu bar) from an in-window <see cref="Menu"/>,
/// so the XAML menu stays the single definition of PolyTerm's menu structure (issue #307).
/// </summary>
/// <remarks>
/// Each <see cref="NativeMenuItem"/> forwards its click to the source <see cref="MenuItem"/> (raising its
/// <see cref="MenuItem.ClickEvent"/>, so the existing code-behind handlers run with the MenuItem as
/// sender and can still read its <see cref="Control.Tag"/>, then running its <see cref="MenuItem.Command"/>
/// if no handler did). The source MenuItem's enabled state, visibility, header, tooltip, toggle type and
/// checked state are tracked live, so bindings on the hidden in-window menu keep driving the native one.
/// A forwarded click doesn't toggle the source's checked state; its handler has to set it.
/// </remarks>
internal static class NativeMenuMirror
{
    /// <summary>Mirrors <paramref name="menu"/>'s top-level items into a new <see cref="NativeMenu"/>.</summary>
    /// <param name="menu">The in-window menu to mirror. It may be hidden; its bindings still update.</param>
    /// <param name="exclude">Items left out of the native menu (for example ones the macOS app menu provides instead).
    /// Separators left leading, trailing or doubled by an exclusion are dropped, as are submenus left empty.</param>
    /// <param name="mapGesture">Converts each item's <see cref="MenuItem.InputGesture"/> into the native item's
    /// key equivalent, for example Ctrl → Cmd on macOS. <c>null</c> keeps gestures unchanged.</param>
    public static NativeMenu Build(Menu menu, Func<MenuItem, bool>? exclude = null, Func<KeyGesture, KeyGesture>? mapGesture = null)
    {
        var native = new NativeMenu();
        AddItems(native, menu.Items, exclude, mapGesture);
        return native;
    }

    /// <summary>
    /// Maps a Windows/Linux style gesture to its macOS equivalent: Ctrl becomes Cmd (<see cref="KeyModifiers.Meta"/>).
    /// </summary>
    public static KeyGesture ToMacGesture(KeyGesture gesture)
    {
        var modifiers = gesture.KeyModifiers;
        if (modifiers.HasFlag(KeyModifiers.Control))
            modifiers = (modifiers & ~KeyModifiers.Control) | KeyModifiers.Meta;
        return new KeyGesture(gesture.Key, modifiers);
    }

    /// <summary>Removes access-key underscores from a menu header: "_File" → "File", "Se_rial…" → "Serial…", "a__b" → "a_b".</summary>
    public static string StripAccessKey(string header)
    {
        var sb = new StringBuilder(header.Length);
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != '_')
                sb.Append(header[i]);
            else if (i + 1 < header.Length && header[i + 1] == '_')
                sb.Append(header[++i]);
        }
        return sb.ToString();
    }

    private static void AddItems(NativeMenu target, IEnumerable<object?> source,
        Func<MenuItem, bool>? exclude, Func<KeyGesture, KeyGesture>? mapGesture)
    {
        foreach (var child in source)
        {
            switch (child)
            {
                case Separator:
                    target.Items.Add(new NativeMenuItemSeparator());
                    break;
                case MenuItem item when exclude?.Invoke(item) != true:
                    if (CreateItem(item, exclude, mapGesture) is { } nativeItem)
                        target.Items.Add(nativeItem);
                    break;
            }
        }

        TidySeparators(target);
    }

    private static NativeMenuItem? CreateItem(MenuItem item, Func<MenuItem, bool>? exclude, Func<KeyGesture, KeyGesture>? mapGesture)
    {
        var native = new NativeMenuItem();

        if (item.ItemCount > 0)
        {
            var submenu = new NativeMenu();
            AddItems(submenu, item.Items, exclude, mapGesture);
            if (submenu.Items.Count == 0) return null;
            native.Menu = submenu;
        }
        else
        {
            native.Click += (_, _) => Invoke(item);
        }

        if (item.InputGesture is { } gesture)
            native.Gesture = mapGesture is null ? gesture : mapGesture(gesture);

        Sync(item, native);
        item.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.HeaderProperty
                || e.Property == InputElement.IsEffectivelyEnabledProperty
                || e.Property == Visual.IsVisibleProperty
                || e.Property == ToolTip.TipProperty
                || e.Property == MenuItem.ToggleTypeProperty
                || e.Property == MenuItem.IsCheckedProperty)
                Sync(item, native);
        };

        return native;
    }

    private static void Sync(MenuItem item, NativeMenuItem native)
    {
        native.Header    = StripAccessKey(item.Header?.ToString() ?? string.Empty);
        native.IsEnabled = item.IsEffectivelyEnabled;
        native.IsVisible = item.IsVisible;
        native.ToolTip   = ToolTip.GetTip(item) as string;
        native.ToggleType = item.ToggleType;
        native.IsChecked  = item.IsChecked;
    }

    /// <summary>Runs the MenuItem's Click handlers, then its Command if nothing handled the click.</summary>
    private static void Invoke(MenuItem item)
    {
        if (!item.IsEffectivelyEnabled) return;

        var args = new RoutedEventArgs(MenuItem.ClickEvent, item);
        item.RaiseEvent(args);

        if (!args.Handled && item.Command is { } command && command.CanExecute(item.CommandParameter))
            command.Execute(item.CommandParameter);
    }

    /// <summary>Drops leading, trailing and consecutive separators.</summary>
    private static void TidySeparators(NativeMenu menu)
    {
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not NativeMenuItemSeparator) continue;

            var isEdge = i == 0 || i == menu.Items.Count - 1;
            var nextIsSeparator = i + 1 < menu.Items.Count && menu.Items[i + 1] is NativeMenuItemSeparator;
            if (isEdge || nextIsSeparator)
                menu.Items.RemoveAt(i);
        }
    }
}
