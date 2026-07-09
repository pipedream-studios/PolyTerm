using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Rdp;

/// <summary>
/// Raw Win32 window capture and synthetic-input helpers, extracted out of <see cref="RdpNativeHostChannel"/>
/// so they can be exercised against any real window handle (not just the MSTSC ActiveX control's,
/// which needs a live RDP connection) — see <c>Win32NativeWindowInputTests</c> in
/// <c>PolyTerm.App.Tests</c>, which drives this against a real, throwaway WinForms window. That
/// verifies the actual Win32 mechanics (capture dimensions, message posting) genuinely work against a
/// real HWND; it does not (and cannot, without a live RDP session) prove the MSTSC control itself
/// reacts correctly to the injected input — see docs/todos.md.
/// </summary>
internal static class Win32NativeWindowInput
{
    // Same key-name strings as PolyTerm.App.Surfaces.Framebuffer.FramebufferSurface.ControlKeyMap
    // (VNC's X11-keysym-derived vocabulary), mapped to Windows virtual-key codes instead — one shared
    // naming convention across both protocols for the MCP framebuffer_key tool.
    public static readonly IReadOnlyDictionary<string, int> NamedKeyToVirtualKey = new Dictionary<string, int>
    {
        ["Return"] = 0x0D, ["BackSpace"] = 0x08, ["Escape"] = 0x1B, ["Tab"] = 0x09,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["Home"] = 0x24, ["End"] = 0x23, ["Prior"] = 0x21, ["Next"] = 0x22,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Shift_L"] = 0xA0, ["Shift_R"] = 0xA1,
        ["Control_L"] = 0xA2, ["Control_R"] = 0xA3,
        ["Alt_L"] = 0xA4, ["Alt_R"] = 0xA5,
        ["Super_L"] = 0x5B, ["Super_R"] = 0x5C,
        ["Caps_Lock"] = 0x14, ["Num_Lock"] = 0x90, ["Scroll_Lock"] = 0x91,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["Print"] = 0x2C, ["Pause"] = 0x13,
    };

    /// <summary>
    /// Captures <paramref name="handle"/>'s current contents via <c>PrintWindow</c> with
    /// <c>PW_RENDERFULLCONTENT</c> — plain <c>BitBlt</c>/<c>PrintWindow</c> without that flag
    /// reliably comes back blank for GPU-accelerated content on Windows 8.1+, which the RDP control's
    /// display almost certainly is. Returns top-down BGRA32 rows as GDI+ produces them from
    /// <see cref="Bitmap.LockBits"/>.
    /// </summary>
    public static (byte[] Data, int Width, int Height)? CaptureWindow(nint handle)
    {
        if (handle == 0 || !GetClientRect(handle, out var rect)) return null;

        var width  = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(handle, hdc, PW_RENDERFULLCONTENT))
                    return null;
            }
            finally { g.ReleaseHdc(hdc); }
        }

        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var data = new byte[bits.Stride * height];
            Marshal.Copy(bits.Scan0, data, 0, data.Length);
            return (data, width, height);
        }
        finally { bitmap.UnlockBits(bits); }
    }

    /// <summary>Posts mouse messages directly to <paramref name="handle"/> — delivered to its window procedure regardless of real OS focus/cursor position, unlike moving the actual system cursor.</summary>
    public static void SendPointer(nint handle, int x, int y, PointerButton button, PointerEventKind kind)
    {
        if (handle == 0) return;

        var lParam = MakeLParam(x, y);
        var (down, up, moveWParam) = button switch
        {
            PointerButton.Left   => (WM_LBUTTONDOWN, WM_LBUTTONUP, MK_LBUTTON),
            PointerButton.Right  => (WM_RBUTTONDOWN, WM_RBUTTONUP, MK_RBUTTON),
            PointerButton.Middle => (WM_MBUTTONDOWN, WM_MBUTTONUP, MK_MBUTTON),
            _                    => (0, 0, 0),
        };

        switch (kind)
        {
            case PointerEventKind.Press when down != 0:
                PostMessage(handle, down, (nint)moveWParam, lParam);
                break;
            case PointerEventKind.Release when up != 0:
                PostMessage(handle, up, nint.Zero, lParam);
                break;
            case PointerEventKind.Move:
                PostMessage(handle, WM_MOUSEMOVE, nint.Zero, lParam);
                break;
        }
    }

    /// <summary>
    /// A single literal character (<paramref name="keySymbol"/> of length 1) is posted as
    /// <c>WM_CHAR</c> — the standard way to inject literal typed text without needing a virtual-key/
    /// keyboard-layout mapping at all. A recognized named key (see <see cref="NamedKeyToVirtualKey"/>)
    /// is posted as <c>WM_KEYDOWN</c>/<c>WM_KEYUP</c> with the matching virtual-key code instead.
    /// Anything else (an unrecognized name) is silently ignored.
    /// </summary>
    public static void SendKey(nint handle, string keySymbol, KeyEventKind kind)
    {
        if (handle == 0) return;

        if (keySymbol.Length == 1)
        {
            if (kind == KeyEventKind.Press)
                PostMessage(handle, WM_CHAR, (nint)keySymbol[0], nint.Zero);
            return;
        }

        if (!NamedKeyToVirtualKey.TryGetValue(keySymbol, out var vk)) return;

        var message = kind == KeyEventKind.Press ? WM_KEYDOWN : WM_KEYUP;
        PostMessage(handle, message, (nint)vk, nint.Zero);
    }

    private static nint MakeLParam(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002, MK_MBUTTON = 0x0010;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);
}
