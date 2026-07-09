using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.App.Surfaces.Rdp;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>
/// Picks the surface control for a <see cref="SessionTabViewModel"/> based on its
/// <see cref="SessionTabViewModel.Surface"/> kind — <see cref="TerminalSurface"/> for
/// <see cref="SurfaceKind.Terminal"/>, <see cref="RdpHostSurface"/> for <see cref="SurfaceKind.NativeHost"/>,
/// <see cref="FramebufferSurface"/> for <see cref="SurfaceKind.Framebuffer"/>.
/// Used as <c>TabControl.ContentTemplate</c>'s inner surface template so new surface kinds only mean
/// adding one more arm here, not touching the tab's outer layout (SFTP panel, splitter, etc.).
/// </summary>
public sealed class SessionSurfaceTemplateSelector : IDataTemplate
{
    public bool Match(object? data) => data is SessionTabViewModel;

    public Control Build(object? param)
    {
        if (param is SessionTabViewModel { Surface: SurfaceKind.NativeHost } && OperatingSystem.IsWindows())
            return BuildRdpHostSurface();

        if (param is SessionTabViewModel { Surface: SurfaceKind.Framebuffer })
            return BuildFramebufferSurface();

        return new TerminalSurface
        {
            [!TerminalSurface.ChannelProperty] = new Binding(nameof(SessionTabViewModel.Terminal)),
            [!TerminalSurface.EmulatorTitleProperty] = new Binding(nameof(SessionTabViewModel.OscTitle))
            {
                Mode = BindingMode.OneWayToSource,
            },
        };
    }

    [SupportedOSPlatform("windows")]
    private static Control BuildRdpHostSurface() => new RdpHostSurface
    {
        [!RdpHostSurface.ChannelProperty] = new Binding(nameof(SessionTabViewModel.NativeHost)),
    };

    private static Control BuildFramebufferSurface() => new FramebufferSurface
    {
        [!FramebufferSurface.ChannelProperty] = new Binding(nameof(SessionTabViewModel.Framebuffer)),
    };
}
