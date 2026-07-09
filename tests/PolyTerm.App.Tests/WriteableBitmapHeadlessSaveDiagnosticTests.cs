using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAssertions;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Documents a real, confirmed environment limitation discovered while testing
/// <c>McpBridgeService.EncodePngBase64Async</c> (the framebuffer_screenshot MCP tool's PNG encoder):
/// <c>WriteableBitmap.Save()</c> produces zero bytes under <c>Avalonia.Headless</c>'s stub rendering
/// backend (no real Skia surface backs it in headless mode), even though the exact same code runs
/// under the real desktop app's actual Skia-backed renderer. Confirmed via this isolated test — not
/// assumed — before concluding <c>McpBridgeServiceFramebufferToolsTests</c>' screenshot test couldn't
/// assert on real non-empty PNG bytes here. This is a test-environment gap, not a product bug: there
/// is no way to click-test/headless-test real PNG output in this environment (same category as every
/// other "no GUI automation tool available" gap in docs/todos.md).
/// </summary>
public sealed class WriteableBitmapHeadlessSaveDiagnosticTests
{
    [AvaloniaFact]
    public void WriteableBitmap_Save_ProducesZeroBytes_UnderTheHeadlessTestPlatform()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        ms.Length.Should().Be(0, "Avalonia.Headless's stub renderer doesn't implement real PNG encoding — this is the documented, confirmed limitation, not the desired behavior");
    }
}
