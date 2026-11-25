using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// Credential provider backed by the OS secret store:
/// DPAPI / Credential Manager on Windows, Keychain on macOS, libsecret on Linux (KWallet is untested).
/// </summary>
public sealed class OsKeychainProvider : ICredentialProvider, ICredentialWriter
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
    /// Resolves a credential from the current OS's secret store, dispatching by platform. A missing
    /// entry resolves to a <see cref="CredentialMaterial"/> with a null <c>Secret</c>, not an error.
    /// </summary>
    public Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ResolveWindowsAsync(reference, ct);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ResolveMacOsAsync(reference, ct);
        return ResolveLinuxAsync(reference, ct);
    }

    /// <summary>
    /// Writes a password credential to the OS keychain — Credential Manager on Windows, the login
    /// keychain on macOS (see <see cref="MacKeychain"/>), Secret Service (via <c>secret-tool</c>) on Linux.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The host OS is none of those three.</exception>
    public async Task<CredentialReference> WriteAsync(string key, string username, string password, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsCredentialManager.Write(key, username, password);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Off the caller's thread: macOS may show an access prompt and block until it's answered.
            await Task.Run(() => MacKeychain.Write(key, username, password), ct).ConfigureAwait(false);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await LinuxSecretService.WriteAsync(key, username, password, ct).ConfigureAwait(false);
        }
        else
        {
            throw new PlatformNotSupportedException("OS keychain write is only supported on Windows, macOS and Linux.");
        }
        return new CredentialReference(Id, $"{key}|{username}");
    }

    /// <summary>
    /// Removes an entry previously written via <see cref="WriteAsync"/>, by the same <paramref name="key"/>
    /// (not a full <see cref="CredentialReference"/> - deletion only needs the target, not the username
    /// half of <c>ItemRef</c>). Idempotent: deleting an already-absent entry is not an error.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The host OS isn't Windows, macOS or Linux.</exception>
    public static async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsCredentialManager.Delete(key);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await Task.Run(() => MacKeychain.Delete(key), ct).ConfigureAwait(false);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await LinuxSecretService.ClearAsync(key, ct).ConfigureAwait(false);
        }
        else
        {
            throw new PlatformNotSupportedException("OS keychain delete is only supported on Windows, macOS and Linux.");
        }
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

    private static async Task<CredentialMaterial> ResolveMacOsAsync(CredentialReference reference, CancellationToken ct)
    {
        // ItemRef format: "target|username" — target is the keychain item's service, as WriteAsync stores it.
        var parts = reference.ItemRef.Split('|', 2);
        var target = parts[0];
        var username = parts.Length > 1 ? parts[1] : null;

        // Off the caller's thread: macOS may show an access prompt and block until it's answered.
        var secret = await Task.Run(() => MacKeychain.Read(target), ct).ConfigureAwait(false);
        return new CredentialMaterial
        {
            Username = username,
            Secret = secret,
        };
    }

    private static async Task<CredentialMaterial> ResolveLinuxAsync(CredentialReference reference, CancellationToken ct)
    {
        // ItemRef format: "target|username" — same shape WriteAsync produces on every platform.
        var parts = reference.ItemRef.Split('|', 2);
        var target = parts[0];
        var username = parts.Length > 1 ? parts[1] : null;

        var secret = await LinuxSecretService.ReadAsync(target, username ?? "", ct).ConfigureAwait(false);
        return new CredentialMaterial
        {
            Username = username,
            Secret = secret is not null ? Encoding.UTF8.GetBytes(secret) : null,
        };
    }
}

/// <summary>
/// Thin CLI wrapper around libsecret's Secret Service D-Bus API via <c>secret-tool</c> (part of the
/// <c>libsecret-tools</c> package on Debian/Ubuntu) — simpler and more robust than P/Invoking
/// libsecret directly, which needs a GLib main loop running to pump D-Bus callbacks; shelling out to
/// the existing CLI avoids that entirely, same "thin wrapper" spirit as
/// <see cref="WindowsCredentialManager"/>. Targets the user's default collection. Whether that keyring
/// already exists and is unlocked depends on how the user signed in: PAM has to be set up for it, and
/// where it isn't (seen with xrdp on Ubuntu 24.04) GNOME may ask to create a keyring on the first save.
/// Confirmed end-to-end against a real Secret Service instance (gnome-keyring) during development,
/// not just written from the spec; KWallet is untested.
/// </summary>
internal static class LinuxSecretService
{
    private const string StartFailureMessage =
        "Couldn't start secret-tool, which PolyTerm needs to use the Linux keyring. Check that it's installed " +
        "(the libsecret-tools package on Debian and Ubuntu).";

    /// <summary>For tests only: the program to run in place of <c>secret-tool</c>, scoped to the setting test's own async flow.</summary>
    internal static AsyncLocal<string?> ExecutableOverride { get; } = new();

    public static async Task<string?> ReadAsync(string target, string username, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunAsync(
            ["lookup", "target", target, "username", username], stdin: null, ct).ConfigureAwait(false);
        return exitCode == 0 && stdout.Length > 0 ? stdout.TrimEnd('\n') : null;
    }

    public static async Task WriteAsync(string target, string username, string password, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunAsync(
            ["store", "--label", $"PolyTerm: {username}@{target}", "target", target, "username", username],
            stdin: password, ct).ConfigureAwait(false);
        if (exitCode != 0)
            throw new InvalidOperationException($"secret-tool store failed (exit {exitCode}): {stderr.Trim()}");
    }

    /// <summary>Removes every entry matching <paramref name="target"/>. Idempotent — <c>secret-tool clear</c> exits 0 whether or not anything actually matched.</summary>
    public static async Task ClearAsync(string target, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunAsync(["clear", "target", target], stdin: null, ct).ConfigureAwait(false);
        if (exitCode != 0)
            throw new InvalidOperationException($"secret-tool clear failed (exit {exitCode}): {stderr.Trim()}");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ExecutableOverride.Value ?? "secret-tool")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Start(psi);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    /// <summary>Starts the tool, reporting any failure to do so as an <see cref="InvalidOperationException"/> that says what to install.</summary>
    private static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException(StartFailureMessage);
        }
        catch (Win32Exception ex)
        {
            // A missing executable throws rather than returning null. The OS's own reason is kept,
            // because it can also be something else (e.g. permission denied).
            var reason = new Win32Exception(ex.NativeErrorCode).Message;
            throw new InvalidOperationException($"{StartFailureMessage} The system reported: {reason}", ex);
        }
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

    /// <summary>Deletes the credential at <paramref name="target"/>. Idempotent — a target that's already absent (ERROR_NOT_FOUND, 1168) is treated as success, not an error.</summary>
    public static void Delete(string target)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        if (CredDelete(target, 1 /*GENERIC*/, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error == 1168 /*ERROR_NOT_FOUND*/) return;
        throw new System.ComponentModel.Win32Exception(error, $"CredDelete failed for target '{target}'.");
    }

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32", SetLastError = true)]
    private static extern bool CredFree(IntPtr buffer);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

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
