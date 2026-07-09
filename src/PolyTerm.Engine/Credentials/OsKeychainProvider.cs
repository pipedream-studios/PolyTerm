using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// Credential provider backed by the OS secret store:
/// DPAPI / Credential Manager on Windows, Keychain on macOS, libsecret/kwallet on Linux.
/// </summary>
internal sealed class OsKeychainProvider : ICredentialProvider, ICredentialWriter
{
    /// <summary>Provider identifier used in <see cref="CredentialReference.ProviderId"/> values (<c>"os-keychain"</c>).</summary>
    public const string Id = "os-keychain";

    private readonly ILogger<OsKeychainProvider> _logger;

    /// <summary>Creates the provider. No I/O occurs until a resolve/write call is made.</summary>
    public OsKeychainProvider(ILogger<OsKeychainProvider> logger) => _logger = logger;

    /// <summary>Provider identifier; same value as <see cref="Id"/>.</summary>
    public string ProviderId => Id;

    /// <summary>User-facing name shown when selecting this provider.</summary>
    public string DisplayName => "OS Keychain";

    /// <summary>Always returns true — the OS keychain has no separate lock/unlock state to check.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(true); // OS keychain is always available

    /// <summary>
    /// Always returns an empty list. The OS keychain has no enumeration API in this provider,
    /// so entries can only be resolved by a known <see cref="CredentialReference"/>, not browsed.
    /// </summary>
    public Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("OS keychain browse is not enumerable; returning empty list.");
        return Task.FromResult<IReadOnlyList<CredentialSummary>>([]);
    }

    /// <summary>
    /// Resolves a credential from the current OS's secret store, dispatching by platform.
    /// </summary>
    /// <exception cref="NotSupportedException">The host OS is macOS or Linux (not yet implemented).</exception>
    public Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ResolveWindowsAsync(reference, ct);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ResolveMacOsAsync(reference, ct);
        return ResolveLinuxAsync(reference, ct);
    }

    /// <summary>
    /// Writes a password credential to the OS keychain. Windows-only for M0 — other platforms
    /// throw <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public Task<CredentialReference> WriteAsync(string key, string username, string password, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("OS keychain write is only supported on Windows for M0.");
        WindowsCredentialManager.Write(key, username, password);
        return Task.FromResult(new CredentialReference(Id, $"{key}|{username}"));
    }

    private static Task<CredentialMaterial> ResolveWindowsAsync(CredentialReference reference, CancellationToken ct)
    {
        // ItemRef format: "target|username"
        var parts = reference.ItemRef.Split('|', 2);
        var target = parts[0];
        var username = parts.Length > 1 ? parts[1] : null;

        var secret = WindowsCredentialManager.Read(target);
        return Task.FromResult(new CredentialMaterial
        {
            Username = username,
            Secret = secret is not null ? Encoding.UTF8.GetBytes(secret) : null,
        });
    }

    private static Task<CredentialMaterial> ResolveMacOsAsync(CredentialReference reference, CancellationToken _)
    {
        // Placeholder: real implementation calls Security.framework via P/Invoke
        throw new NotSupportedException("macOS keychain not yet implemented; use KeePass or Bitwarden provider.");
    }

    private static Task<CredentialMaterial> ResolveLinuxAsync(CredentialReference reference, CancellationToken _)
    {
        // Placeholder: real implementation calls libsecret or kwallet via D-Bus
        throw new NotSupportedException("Linux keychain not yet implemented; use KeePass or Bitwarden provider.");
    }
}

/// <summary>Thin P/Invoke wrapper for Windows Credential Manager (CredRead/CredWrite).</summary>
internal static class WindowsCredentialManager
{
    public static string? Read(string target)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        if (!CredRead(target, 1 /*GENERIC*/, 0, out var credPtr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(credPtr);
            if (cred.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2));
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void Write(string target, string username, string password)
    {
        var blob = System.Text.Encoding.Unicode.GetBytes(password);
        var credPtr = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var cred = new NativeCredential
            {
                Type               = 1,
                TargetName         = target,
                UserName           = username,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob     = credPtr,
                Persist            = 3, // CRED_PERSIST_ENTERPRISE — works for local and domain
            };
            if (!CredWrite(ref cred, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    $"CredWrite failed for target '{target}'.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(credPtr);
        }
    }

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32", SetLastError = true)]
    private static extern bool CredFree(IntPtr buffer);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
