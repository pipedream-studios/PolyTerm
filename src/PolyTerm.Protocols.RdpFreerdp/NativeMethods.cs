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

    /// <summary>
    /// Matches the shim's <c>prdp_framebuffer_cb</c> — fired once per frame that actually changed
    /// something, on the shim's own background thread. <paramref name="buffer"/> points at just the
    /// changed <paramref name="regionX"/>/<paramref name="regionY"/>/<paramref name="regionWidth"/>/
    /// <paramref name="regionHeight"/> sub-rectangle (not the whole frame), valid only for the
    /// duration of the call; <paramref name="stride"/> is the *full frame's* row stride, since the
    /// region is a view into the full backbuffer, not a tightly-packed copy.
    /// <paramref name="fullWidth"/>/<paramref name="fullHeight"/> are the overall frame's dimensions.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FramebufferCallback(nint userData, nint buffer, int fullWidth, int fullHeight,
        int regionX, int regionY, int regionWidth, int regionHeight, int stride);

    /// <summary>Matches the shim's <c>prdp_state_cb</c> — state 1 = connected, 0 = disconnected (message non-null on error).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StateCallback(nint userData, int state, nint message);

    /// <summary>
    /// Matches the shim's <c>prdp_clipboard_cb</c> — text was copied on the remote. <paramref name="data"/>
    /// is <paramref name="length"/> bytes of raw, unvalidated <c>CF_UNICODETEXT</c>, valid only for the
    /// duration of the call. Windows reports each copy more than once.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ClipboardCallback(nint userData, nint data, int length);

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
        nint[] driveNames, nint[] drivePaths, int driveCount,
        FramebufferCallback onFramebuffer, StateCallback onState, ClipboardCallback onClipboard, nint userData);
#pragma warning restore CA2101

    /// <summary>
    /// Calls <see cref="prdp_connect(string, int, string, string, string, int, int, nint[], nint[], int, FramebufferCallback, StateCallback, ClipboardCallback, nint)"/>
    /// with the drive shares as the shim's <c>const char* const*</c> UTF-8 arrays. Marshalled by hand:
    /// the runtime marshaller rejects <c>string[]</c> with <c>ArraySubType = LPUTF8Str</c> at call time
    /// ("String[] must be paired with an ArraySubType of LPStr, LPWStr, or LPTStr"), and LPStr would
    /// be the ANSI code page on Windows, not UTF-8. The shim copies the strings into FreeRDP's own
    /// settings before returning, so they're freed straight after the call.
    /// </summary>
    public static nint Connect(string host, int port, string username, string password, string domain,
        int width, int height, IReadOnlyList<string> driveNames, IReadOnlyList<string> drivePaths, int driveCount,
        FramebufferCallback onFramebuffer, StateCallback onState, ClipboardCallback onClipboard, nint userData)
    {
        var names = new nint[driveNames.Count];
        var paths = new nint[drivePaths.Count];
        try
        {
            for (var i = 0; i < names.Length; i++) names[i] = Marshal.StringToCoTaskMemUTF8(driveNames[i]);
            for (var i = 0; i < paths.Length; i++) paths[i] = Marshal.StringToCoTaskMemUTF8(drivePaths[i]);
            return prdp_connect(host, port, username, password, domain, width, height, names, paths, driveCount,
                onFramebuffer, onState, onClipboard, userData);
        }
        finally
        {
            foreach (var ptr in names) Marshal.FreeCoTaskMem(ptr);
            foreach (var ptr in paths) Marshal.FreeCoTaskMem(ptr);
        }
    }

    [LibraryImport(LibraryName)]
    public static partial void prdp_send_mouse(nint handle, ushort flags, int x, int y);

    [LibraryImport(LibraryName)]
    public static partial void prdp_send_key(nint handle, int down, uint rdpScancode);

    [LibraryImport(LibraryName)]
    public static partial void prdp_send_unicode_key(nint handle, int down, ushort unicodeCodePoint);

    [LibraryImport(LibraryName)]
    public static partial void prdp_set_clipboard_text(nint handle, byte[] data, int length);

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
