using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.App.Surfaces.Web;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>
/// Picks the surface control for a <see cref="SessionTabViewModel"/> based on its
/// <see cref="SessionTabViewModel.Surface"/> kind — <see cref="TerminalSurface"/> for
/// <see cref="SurfaceKind.Terminal"/>, <see cref="FramebufferSurface"/> for
/// <see cref="SurfaceKind.Framebuffer"/> (VNC and RDP both use this), <see cref="WebSurface"/>
/// for <see cref="SurfaceKind.WebView"/>.
/// Used as <c>TabControl.ContentTemplate</c>'s inner surface template so new surface kinds only mean
/// adding one more arm here, not touching the tab's outer layout (SFTP panel, splitter, etc.).
/// </summary>
public sealed class SessionSurfaceTemplateSelector : IDataTemplate
{
    public bool Match(object? data) => data is SessionTabViewModel;

    public Control Build(object? param)
    {
        if (param is SessionTabViewModel { Surface: SurfaceKind.Framebuffer })
            return BuildFramebufferSurface();

        if (param is SessionTabViewModel { Surface: SurfaceKind.WebView })
            return BuildWebSurface();

        var surface = new TerminalSurface
        {
            [!TerminalSurface.ChannelProperty] = new Binding(nameof(SessionTabViewModel.Terminal)),
            [!TerminalSurface.EmulatorTitleProperty] = new Binding(nameof(SessionTabViewModel.OscTitle))
            {
                Mode = BindingMode.OneWayToSource,
            },
        };
        AutomationProperties.SetAutomationId(surface, "TerminalSurface");
        AutomationProperties.SetAccessibilityView(surface, AccessibilityView.Content);

        // Follow the saved terminal font, live. Subscribed only while in the visual tree so a closed tab's
        // surface isn't kept alive by App's static event. On re-attach (switching back to the tab) it's only
        // re-applied if the setting changed while detached, so a tab's own Ctrl+wheel zoom survives.
        var appliedVersion = -1;
        void Apply()
        {
            if (appliedVersion == App.TerminalFontVersion) return;
            appliedVersion = App.TerminalFontVersion;
            ApplyTerminalFont(surface);
        }
        void OnChanged(object? s, EventArgs e) => Apply();
        Apply();
        surface.AttachedToVisualTree   += (_, _) => { Apply(); App.TerminalFontChanged += OnChanged; };
        surface.DetachedFromVisualTree += (_, _) => App.TerminalFontChanged -= OnChanged;
        return surface;
    }

    private static void ApplyTerminalFont(TerminalSurface surface)
    {
        surface.TerminalFont = TerminalFonts.Resolve(App.Settings.TerminalFontFamily);
        surface.FontSize     = App.Settings.TerminalFontSize;
    }

    private static Control BuildFramebufferSurface() => new FramebufferSurface
    {
        [!FramebufferSurface.ChannelProperty] = new Binding(nameof(SessionTabViewModel.Framebuffer)),
    };

    private static Control BuildWebSurface() => new WebSurface
    {
        [!WebSurface.ChannelProperty] = new Binding(nameof(SessionTabViewModel.Web)),
        [!WebSurface.PageTitleProperty] = new Binding(nameof(SessionTabViewModel.OscTitle))
        {
            Mode = BindingMode.OneWayToSource,
        },
    };
}
