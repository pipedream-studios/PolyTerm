using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PolyTerm.Protocols.Serial;

/// <summary>
/// Thin IOKit/CoreFoundation binding behind <see cref="MacOsUsbComPortDetector"/> (issue #91): lists
/// every <c>IOSerialBSDClient</c> service and, for each, walks up the IOService plane looking for a USB
/// device ancestor to read its descriptor strings from. Every IOKit object and CF value it obtains is
/// released before returning; a failure on one port skips only that port.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacOsIoKitSerialEnumerator
{
    private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint KIOMainPortDefault = 0;      // MACH_PORT_NULL: IOKit picks the main port
    private const int KIOReturnSuccess = 0;
    private const uint KCFStringEncodingUtf8 = 0x08000100;
    private const string ServicePlane = "IOService";

    /// <summary>Guards the parent walk against a malformed registry; real serial ports sit well under ten levels below their USB device.</summary>
    private const int MaxAncestorDepth = 32;

    /// <summary>
    /// <c>IOUSBHostDevice</c> is the USB device class on macOS 10.11 and later; <c>IOUSBDevice</c> is the
    /// legacy family's class, still matched in case an old kext publishes it.
    /// </summary>
    private static readonly string[] UsbDeviceClasses = ["IOUSBHostDevice", "IOUSBDevice"];

    public static IReadOnlyList<MacSerialService> Enumerate()
    {
        var results = new List<MacSerialService>();

        // IOServiceGetMatchingServices consumes the matching dictionary's reference, so it's never released here.
        var matching = IOServiceMatching("IOSerialBSDClient");
        if (matching == IntPtr.Zero)
            return results;

        if (IOServiceGetMatchingServices(KIOMainPortDefault, matching, out var iterator) != KIOReturnSuccess)
            return results;

        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    var callout = GetStringProperty(service, "IOCalloutDevice");
                    if (string.IsNullOrEmpty(callout)) continue;

                    var usbDevice = FindUsbDeviceAncestor(service);
                    if (usbDevice == 0)
                    {
                        results.Add(new MacSerialService(callout, IsUsb: false, null, null));
                        continue;
                    }

                    try
                    {
                        results.Add(new MacSerialService(
                            callout,
                            IsUsb: true,
                            GetStringProperty(usbDevice, "USB Product Name"),
                            GetStringProperty(usbDevice, "USB Vendor Name")));
                    }
                    finally
                    {
                        _ = IOObjectRelease(usbDevice);
                    }
                }
                finally
                {
                    _ = IOObjectRelease(service);
                }
            }
        }
        finally
        {
            _ = IOObjectRelease(iterator);
        }

        return results;
    }

    /// <summary>
    /// Walks IOService-plane parents from <paramref name="entry"/> until one conforms to a USB device
    /// class. Returns that ancestor with a reference the caller must release, or 0 when there is none
    /// (a Bluetooth or built-in port). <paramref name="entry"/> itself is never released here.
    /// </summary>
    private static uint FindUsbDeviceAncestor(uint entry)
    {
        var current = entry;
        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            var kr = IORegistryEntryGetParentEntry(current, ServicePlane, out var parent);
            if (current != entry)
                _ = IOObjectRelease(current);
            if (kr != KIOReturnSuccess || parent == 0)
                return 0;

            foreach (var cls in UsbDeviceClasses)
            {
                if (IOObjectConformsTo(parent, cls))
                    return parent;
            }
            current = parent;
        }

        if (current != entry)
            _ = IOObjectRelease(current);
        return 0;
    }

    /// <summary>Reads a CFString property off a registry entry, or null when it's missing or not a string.</summary>
    private static string? GetStringProperty(uint entry, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, KCFStringEncodingUtf8);
        if (cfKey == IntPtr.Zero)
            return null;

        try
        {
            var value = IORegistryEntryCreateCFProperty(entry, cfKey, IntPtr.Zero, 0);
            if (value == IntPtr.Zero)
                return null;

            try
            {
                return CFGetTypeID(value) == CFStringGetTypeID() ? CFStringToString(value) : null;
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private static string? CFStringToString(IntPtr cfString)
    {
        var length = CFStringGetLength(cfString);
        var maxBytes = CFStringGetMaximumSizeForEncoding(length, KCFStringEncodingUtf8);
        if (maxBytes < 0)
            return null;

        var buffer = new byte[maxBytes + 1];
        if (!CFStringGetCString(cfString, buffer, buffer.Length, KCFStringEncodingUtf8))
            return null;

        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IOKitLib)]
    private static partial int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint existing);

    [LibraryImport(IOKitLib)]
    private static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKitLib)]
    private static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IOObjectConformsTo(uint obj, string className);

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int IORegistryEntryGetParentEntry(uint entry, string plane, out uint parent);

    [LibraryImport(IOKitLib)]
    private static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFGetTypeID(IntPtr cf);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFStringGetTypeID();

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFStringGetLength(IntPtr theString);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr theString, [Out] byte[] buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    private static partial void CFRelease(IntPtr cf);
}
