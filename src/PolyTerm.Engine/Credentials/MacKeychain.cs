using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// Thin P/Invoke wrapper over Security.framework's SecItem API for generic-password items in the
/// user's login keychain - the macOS counterpart of <see cref="WindowsCredentialManager"/> and
/// <see cref="LinuxSecretService"/>.
/// <para>
/// One item per <c>service</c> (the provider's "target"), with the username as its account - the
/// same one-entry-per-target shape Windows Credential Manager has, so a second write to the same
/// target replaces the first instead of adding a duplicate.
/// </para>
/// <para>
/// Uses the file-based login keychain, not the data-protection keychain: the latter needs a
/// keychain-access-groups entitlement, which an ad-hoc signed build can't carry. The item's access
/// list trusts the app that created it by code signature, so after rebuilding an ad-hoc signed app
/// macOS asks again before the new build may read it ("Always Allow" sticks for that build). One read
/// can raise two prompts in a row, each asking for the login keychain password; it waits until both
/// are answered (seen when testing issue #379).
/// </para>
/// <para>
/// Calls can block while macOS shows such a prompt, so callers should run them off the UI thread.
/// </para>
/// </summary>
internal static class MacKeychain
{
    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const uint CFStringEncodingUtf8 = 0x08000100;

    private static readonly Lazy<Constants> s_constants = new(LoadConstants);

    /// <summary>Returns the stored password bytes for <paramref name="service"/>, or null if there's no such item.</summary>
    public static byte[]? Read(string service)
    {
        var c = s_constants.Value;
        using var cf = new CfScope(c);
        var query = cf.Dictionary(
            (c.Class, c.ClassGenericPassword),
            (c.AttrService, cf.String(service)),
            (c.ReturnData, c.BooleanTrue),
            (c.MatchLimit, c.MatchLimitOne));

        var status = SecItemCopyMatching(query, out var data);
        if (status == ErrSecItemNotFound) return null;
        ThrowOnError(status, "read", service);

        try
        {
            var length = (int)CFDataGetLength(data);
            if (length == 0) return null;
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            return bytes;
        }
        finally
        {
            CFRelease(data);
        }
    }

    /// <summary>Creates or replaces the item for <paramref name="service"/>.</summary>
    public static void Write(string service, string account, string password)
    {
        var c = s_constants.Value;
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var cf = new CfScope(c);
            var serviceRef = cf.String(service);
            var accountRef = cf.String(account);
            var dataRef = cf.Data(passwordBytes);
            var labelRef = cf.String($"PolyTerm: {account}@{service}");

            var query = cf.Dictionary((c.Class, c.ClassGenericPassword), (c.AttrService, serviceRef));
            var changes = cf.Dictionary((c.AttrAccount, accountRef), (c.ValueData, dataRef), (c.AttrLabel, labelRef));

            var status = SecItemUpdate(query, changes);
            if (status == ErrSecItemNotFound)
            {
                var item = cf.Dictionary(
                    (c.Class, c.ClassGenericPassword),
                    (c.AttrService, serviceRef),
                    (c.AttrAccount, accountRef),
                    (c.ValueData, dataRef),
                    (c.AttrLabel, labelRef));
                status = SecItemAdd(item, IntPtr.Zero);
            }
            ThrowOnError(status, "write", service);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>Deletes every item for <paramref name="service"/>. Idempotent - no item is not an error.</summary>
    public static void Delete(string service)
    {
        var c = s_constants.Value;
        using var cf = new CfScope(c);
        var query = cf.Dictionary((c.Class, c.ClassGenericPassword), (c.AttrService, cf.String(service)));

        var status = SecItemDelete(query);
        if (status is ErrSecSuccess or ErrSecItemNotFound) return;
        ThrowOnError(status, "delete", service);
    }

    private static void ThrowOnError(int status, string operation, string service)
    {
        if (status == ErrSecSuccess) return;
        var message = CopyErrorMessage(status) ?? "unknown error";
        throw new InvalidOperationException($"macOS keychain {operation} failed for '{service}' (OSStatus {status}): {message}");
    }

    private static string? CopyErrorMessage(int status)
    {
        var cfString = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (cfString == IntPtr.Zero) return null;
        try
        {
            var length = CFStringGetLength(cfString);
            var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
            try
            {
                CFStringGetCharacters(cfString, new CFRange { Location = 0, Length = length }, buffer);
                return Marshal.PtrToStringUni(buffer, (int)length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    /// <summary>The Security/CoreFoundation constants SecItem queries are built from - exported as data symbols, so read via <see cref="NativeLibrary"/>.</summary>
    private sealed record Constants(
        IntPtr Class, IntPtr ClassGenericPassword, IntPtr AttrService, IntPtr AttrAccount, IntPtr AttrLabel,
        IntPtr ValueData, IntPtr ReturnData, IntPtr MatchLimit, IntPtr MatchLimitOne,
        IntPtr BooleanTrue, IntPtr DictionaryKeyCallBacks, IntPtr DictionaryValueCallBacks);

    private static Constants LoadConstants()
    {
        var security = NativeLibrary.Load(SecurityLib);
        var coreFoundation = NativeLibrary.Load(CoreFoundationLib);

        // CFStringRef/CFBooleanRef constants are pointer-sized variables: read the value at the export.
        IntPtr Ref(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

        return new Constants(
            Class: Ref(security, "kSecClass"),
            ClassGenericPassword: Ref(security, "kSecClassGenericPassword"),
            AttrService: Ref(security, "kSecAttrService"),
            AttrAccount: Ref(security, "kSecAttrAccount"),
            AttrLabel: Ref(security, "kSecAttrLabel"),
            ValueData: Ref(security, "kSecValueData"),
            ReturnData: Ref(security, "kSecReturnData"),
            MatchLimit: Ref(security, "kSecMatchLimit"),
            MatchLimitOne: Ref(security, "kSecMatchLimitOne"),
            BooleanTrue: Ref(coreFoundation, "kCFBooleanTrue"),
            // The callback structs themselves are what CFDictionaryCreate wants a pointer to - the export's address.
            DictionaryKeyCallBacks: NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
            DictionaryValueCallBacks: NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks"));
    }

    /// <summary>Owns the CF objects created while building one query, releasing them all on dispose.</summary>
    private sealed class CfScope(Constants constants) : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Track(CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, CFStringEncodingUtf8, false));
        }

        public IntPtr Data(byte[] bytes) => Track(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] entries)
        {
            var keys = entries.Select(e => e.Key).ToArray();
            var values = entries.Select(e => e.Value).ToArray();
            return Track(CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length,
                constants.DictionaryKeyCallBacks, constants.DictionaryValueCallBacks));
        }

        private IntPtr Track(IntPtr cfObject)
        {
            if (cfObject == IntPtr.Zero)
                throw new InvalidOperationException("CoreFoundation object allocation failed.");
            _owned.Add(cfObject);
            return cfObject;
        }

        public void Dispose()
        {
            foreach (var cfObject in _owned)
                CFRelease(cfObject);
            _owned.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    [DllImport(SecurityLib)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityLib)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityLib)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SecurityLib)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(SecurityLib)]
    private static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint numBytes, uint encoding,
        [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundationLib)]
    private static extern void CFStringGetCharacters(IntPtr theString, CFRange range, IntPtr buffer);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFDataGetLength(IntPtr theData);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr theData);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);
}
