using System.Reflection;
using System.Runtime.InteropServices;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// P/Invoke surface for <c>native/PolyTerm-rdp-shim</c> — the thin C ABI over FreeRDP 3's client API
/// (issue #67). Bind the shim, not FreeRDP's own API, so upstream FreeRDP churn never reaches C#.
/// </summary>
internal static partial class NativeMethods
{
    private const string LibraryName = "polyterm_rdp_shim";

    /// <summary>Matches the shim's <c>prdp_framebuffer_cb</c> — fired on every completed frame, on the shim's own background thread. <paramref name="buffer"/> is only valid for the duration of the call.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FramebufferCallback(nint userData, nint buffer, int width, int height, int stride);

    /// <summary>Matches the shim's <c>prdp_state_cb</c> — state 1 = connected, 0 = disconnected (message non-null on error).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StateCallback(nint userData, int state, nint message);

    // Classic [DllImport], not [LibraryImport], specifically for this one: LibraryImport's
    // source-generated marshaller doesn't reliably handle delegate-typed parameters the same
    // well-established way DllImport's runtime marshaller does - not worth the risk on the one
    // function that has to pass native callbacks across the boundary.
    //
    // CA2101 is suppressed below: its analyzer predates UnmanagedType.LPUTF8Str (added later in
    // .NET Core) and doesn't recognize it as "explicit" marshaling, so it false-positives here even
    // though LPUTF8Str is the exact, correct marshaling for the shim's UTF-8 `const char*` params.
#pragma warning disable CA2101
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint prdp_connect(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host, int port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string domain, int width, int height,
        FramebufferCallback onFramebuffer, StateCallback onState, nint userData);
#pragma warning restore CA2101

    [LibraryImport(LibraryName)]
    public static partial void prdp_send_mouse(nint handle, ushort flags, int x, int y);

    [LibraryImport(LibraryName)]
    public static partial void prdp_send_key(nint handle, int down, uint rdpScancode);

    [LibraryImport(LibraryName)]
    public static partial void prdp_disconnect(nint handle);

    [LibraryImport(LibraryName)]
    public static partial void prdp_free(nint handle);

    /// <summary>
    /// Probes whether the native shim can actually be loaded on this platform/RID — used by
    /// <see cref="RdpFreerdpProtocolDescriptor.IsAvailable"/> for capability gating (never throws).
    /// Deliberately the 3-arg <see cref="NativeLibrary.TryLoad(string, Assembly, DllImportSearchPath?, out IntPtr)"/>
    /// overload, not the bare 2-arg one: only this overload applies the OS naming convention
    /// (lib-prefix/.so-suffix on Linux) and searches the assembly's own directory - the bare 2-arg
    /// overload does a literal dlopen("polyterm_rdp_shim") with no variations and no app-dir probing,
    /// so it always reports "not found" even when the exact same P/Invoke calls below resolve fine
    /// (confirmed by tracing the actual dlopen candidates via LD_DEBUG=libs on the real test server).
    /// </summary>
    public static bool TryLoadLibrary() =>
        NativeLibrary.TryLoad(LibraryName, typeof(NativeMethods).Assembly, null, out _);
}
