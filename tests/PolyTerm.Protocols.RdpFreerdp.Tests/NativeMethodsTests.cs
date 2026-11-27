using AwesomeAssertions;
using Xunit;

namespace PolyTerm.Protocols.RdpFreerdp.Tests;

/// <summary>
/// Exercises the real P/Invoke boundary into the native shim, so marshalling mistakes surface here
/// rather than on the first real connect (a <c>string[]</c> + <c>LPUTF8Str</c> drive array once
/// threw <see cref="System.Runtime.InteropServices.MarshalDirectiveException"/> on every RDP connect).
/// </summary>
public sealed class NativeMethodsTests
{
    [Fact]
    public void Connect_WithDriveArrays_MarshalsAndReachesTheShim()
    {
        if (!NativeMethods.TryLoadLibrary())
            Assert.Skip("The native RDP shim isn't built for this platform (see native/PolyTerm-rdp-shim).");

        // A negative drive_count is rejected by the shim before it touches FreeRDP, Winsock or any
        // thread - so this returns NULL straight away, having proved the call (and both UTF-8 arrays,
        // non-ASCII included) marshalled.
        var handle = NativeMethods.Connect("127.0.0.1", 3389, "user", "pass", "", 1024, 768,
            ["Projects", "Données"], ["/tmp/projects", "/tmp/données"], -1,
            (_, _, _, _, _, _, _, _, _) => { }, (_, _, _) => { }, (_, _, _) => { }, 0);

        handle.Should().Be(0);
    }

    [Fact]
    public void SetClipboardText_MarshalsAndReachesTheShim()
    {
        if (!NativeMethods.TryLoadLibrary())
            Assert.Skip("The native RDP shim isn't built for this platform (see native/PolyTerm-rdp-shim).");

        // The shim ignores a null handle, so this only proves the entry point exists and the call marshals.
        var act = () => NativeMethods.prdp_set_clipboard_text(0, [0x41, 0, 0, 0], 4);

        act.Should().NotThrow();
    }
}
