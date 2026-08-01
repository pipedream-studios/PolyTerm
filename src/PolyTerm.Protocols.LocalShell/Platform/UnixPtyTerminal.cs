using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PolyTerm.Protocols.LocalShell.Platform;

/// <summary>
/// Unix PTY implementation backed by a real <c>forkpty(3)</c> — the Linux/macOS counterpart to
/// <see cref="ConPtyTerminal"/>, replacing the earlier M0 <c>script(1)</c>-wrapping placeholder
/// (issue #69): that approach used GNU/Linux <c>util-linux</c> <c>script</c>'s <c>-c</c> syntax, which
/// classic BSD <c>script</c> (what macOS ships) doesn't have, and its <see cref="Resize"/> was a
/// documented no-op.
///
/// <c>forkpty()</c> does the risky post-<c>fork()</c> PTY setup (openpty, setsid, TIOCSCTTY, dup2 onto
/// fd 0/1/2) entirely inside libc's own native code, not here - the only thing this class's own code
/// does in the forked child (see the constructor) is <c>chdir</c>/<c>execve</c>/<c>_exit</c> on
/// pointers already marshaled before the fork, deliberately with zero managed allocation in between:
/// a forked child of a multithreaded process (which the .NET runtime always is) only has the one
/// thread that called fork() left running, while the runtime's internal state - GC locks, other
/// threads' held locks, thread-pool/finalizer threads - reflects the moment of the fork and is
/// otherwise undefined to rely on there. This is the same fork+immediately-exec pattern established
/// .NET PTY implementations (e.g. Microsoft's own Pty.Net) use for exactly this reason.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class UnixPtyTerminal : IPseudoTerminal
{
    private readonly int _childPid;
    private readonly FileStream _output;
    private readonly FileStream _input;
    private readonly SafeFileHandle _masterHandle;
    private readonly Task _exited;
    private bool _disposed;

    /// <summary>
    /// Forces the JIT to compile the P/Invoke marshaling stubs for execve/chdir/_exit once, up front,
    /// in this normal (safe, multithreaded) static constructor context - not the first time they're
    /// actually called, which for all three is only ever in a forked child (see the constructor
    /// below). JIT compilation itself takes locks and allocates; doing it for the first time in a
    /// single-threaded forked child, where those same locks might already be held by a now-nonexistent
    /// thread, is exactly the kind of "worked in testing, deadlocks in production" bug this avoids.
    /// </summary>
    static UnixPtyTerminal()
    {
        foreach (var name in new[] { nameof(NativeMethods.execve), nameof(NativeMethods.chdir), nameof(NativeMethods._exit) })
        {
            var method = typeof(NativeMethods).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }
    }

    /// <inheritdoc/>
    public Stream Input => _input;

    /// <inheritdoc/>
    public Stream Output => _output;

    /// <summary>
    /// Forks <paramref name="shell"/> under a real PTY via <c>forkpty()</c>. <paramref name="cols"/>/
    /// <paramref name="rows"/> seed the PTY's initial <c>winsize</c> directly (not via the old
    /// <c>COLUMNS</c>/<c>LINES</c> env-var hack, which many programs never read at all).
    /// </summary>
    public UnixPtyTerminal(string shell, string? workingDir, int cols, int rows)
    {
        var winsize = new NativeMethods.Winsize { ws_row = (ushort)rows, ws_col = (ushort)cols };

        // Everything the child needs is marshaled to unmanaged memory BEFORE forking - see this
        // class's own doc comment for why the child branch below must not allocate.
        var shellPathPtr = Marshal.StringToCoTaskMemUTF8(shell);
        var argvPtr = NativeMethods.BuildNullTerminatedArray([shell]);
        var envpPtr = NativeMethods.BuildNullTerminatedArray(BuildChildEnvironment());
        var workingDirPtr = workingDir is not null ? Marshal.StringToCoTaskMemUTF8(workingDir) : nint.Zero;

        try
        {
            var pid = NativeMethods.forkpty(out var master, nint.Zero, nint.Zero, ref winsize);
            if (pid < 0)
                throw new IOException($"forkpty() failed (errno {Marshal.GetLastPInvokeError()}).");

            if (pid == 0)
            {
                // CHILD - see this class's own doc comment. Only pre-marshaled pointers from here on.
                if (workingDirPtr != nint.Zero)
                    _ = NativeMethods.chdir(workingDirPtr); // best-effort - a failure here isn't fatal, the shell just starts in its own default directory instead
                _ = NativeMethods.execve(shellPathPtr, argvPtr, envpPtr);
                NativeMethods._exit(127); // execve only returns on failure
                // _exit() never returns - but if it somehow did, falling through into the rest of
                // this constructor (and beyond, back into the wider app) as a rogue duplicate process
                // sharing this one's PID-adjacent lineage would be far worse than hanging here.
                while (true) { }
            }

            _childPid = pid;
            _masterHandle = new SafeFileHandle((nint)master, ownsHandle: true);
            // Two independent FileStream wrappers over the one master fd - reading (shell output)
            // and writing (keystrokes) happen concurrently from different callers (LocalShellTerminalChannel's
            // background ReadLoop vs. SendInputAsync), and FileStream itself isn't documented safe for
            // concurrent use from a single instance. The underlying read()/write() syscalls on one fd
            // from different threads are standard, kernel-synchronized POSIX behavior regardless - only
            // one of the two SafeFileHandle wrappers owns the fd, so Dispose() closes it exactly once.
            _output = new FileStream(_masterHandle, FileAccess.Read);
            _input = new FileStream(new SafeFileHandle((nint)master, ownsHandle: false), FileAccess.Write);

            _exited = Task.Run(() =>
            {
                _ = NativeMethods.waitpid(_childPid, out _, 0);
            });
        }
        finally
        {
            // Only ever reached in the parent: execve() on success replaces the child's process image
            // entirely (this code never runs there), and the child's own failure path calls _exit()
            // directly instead of falling through to here.
            Marshal.FreeCoTaskMem(shellPathPtr);
            NativeMethods.FreeNullTerminatedArray(argvPtr);
            NativeMethods.FreeNullTerminatedArray(envpPtr);
            if (workingDirPtr != nint.Zero)
                Marshal.FreeCoTaskMem(workingDirPtr);
        }
    }

    /// <summary>
    /// Sends the new size to the kernel's PTY record, which delivers a real <c>SIGWINCH</c> to the
    /// shell's foreground process group - unlike the old placeholder, full-screen programs (vim, htop,
    /// less) now actually see resizes.
    ///
    /// macOS goes through <c>native/PolyTerm-pty-shim</c> instead of calling <c>ioctl(TIOCSWINSZ)</c>
    /// directly via P/Invoke, unlike Linux below - <c>ioctl(2)</c> is declared variadic in libc
    /// (<c>int ioctl(int fd, unsigned long request, ...)</c>), and calling a true variadic C function
    /// through a fixed-signature <c>[DllImport]</c> is unreliable on Apple Silicon's ABI, which passes
    /// variadic arguments differently (stack-only) than named/fixed ones - unlike x86-64/ARM64 Linux,
    /// where the two calling conventions happen to coincide for the first few arguments, masking the
    /// same mismatch. Confirmed the hard way: direct P/Invoke here reported ioctl() success while the
    /// kernel's own immediate TIOCGWINSZ read-back showed the size was never actually written - not a
    /// timing issue (an explicit settle delay and a 20s timeout made no difference), not a wrong
    /// TIOCSWINSZ constant (independently re-derived and confirmed correct), reproduced on two
    /// different real CI macOS runners, never reproduced on real Linux hardware running the exact same
    /// P/Invoke code. The shim is a real C compiler correctly handling the variadic call internally;
    /// .NET only ever calls its fixed, non-variadic exported function.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        var fd = _masterHandle.DangerousGetHandle().ToInt32();

        if (OperatingSystem.IsMacOS())
        {
            _ = NativeMethods.polyterm_pty_set_winsize(fd, (ushort)rows, (ushort)cols);
            return;
        }

        var winsize = new NativeMethods.Winsize { ws_row = (ushort)rows, ws_col = (ushort)cols };
        _ = NativeMethods.ioctl(fd, NativeMethods.TIOCSWINSZ, ref winsize);
    }

    /// <inheritdoc/>
    public Task WaitForExitAsync(CancellationToken ct) => _exited.WaitAsync(ct);

    /// <summary>Kills the shell process if still running, then disposes the PTY streams. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ = NativeMethods.kill(_childPid, NativeMethods.SIGKILL); } catch { }
        _input.Dispose();
        _output.Dispose(); // also disposes _masterHandle, which _output's SafeFileHandle constructor argument owns
    }

    /// <summary>
    /// The forked shell's environment: this process's own environment (PATH, HOME, LANG, ...) with
    /// TERM forced to match <see cref="LocalShellTerminalChannel.TerminalType"/> - matching the old
    /// placeholder's TERM override, but no longer needing the COLUMNS/LINES hack now that the real
    /// PTY winsize is set directly.
    /// </summary>
    private static List<string> BuildChildEnvironment()
    {
        var env = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            env.Add($"{entry.Key}={entry.Value}");
        env.Add("TERM=xterm-256color");
        return env;
    }

    private static class NativeMethods
    {
        private const string Libc = "libc";

        public const int SIGKILL = 9;

        // Linux-only now - macOS goes through polyterm_pty_shim instead (see Resize()'s own doc
        // comment for why). 0x5414 is confirmed against a real Linux box.
        public const nint TIOCSWINSZ = 0x5414;

        // native/PolyTerm-pty-shim's fixed-arity wrapper around ioctl(TIOCSWINSZ) - see Resize()'s own
        // doc comment for why this exists instead of P/Invoking ioctl() directly on macOS. Returns 0
        // on success, -errno on failure.
        [DllImport("polyterm_pty_shim", CallingConvention = CallingConvention.Cdecl)]
        public static extern int polyterm_pty_set_winsize(int fd, ushort rows, ushort cols);

        [StructLayout(LayoutKind.Sequential)]
        public struct Winsize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;
        }

        [DllImport(Libc, SetLastError = true)]
        public static extern int forkpty(out int amaster, nint name, nint termp, ref Winsize winp);

        [DllImport(Libc, SetLastError = true)]
        public static extern int execve(nint path, nint argv, nint envp);

        [DllImport(Libc, SetLastError = true)]
        public static extern int chdir(nint path);

        [DllImport(Libc)]
        public static extern void _exit(int status);

        [DllImport(Libc, SetLastError = true)]
        public static extern int ioctl(int fd, nint request, ref Winsize winsize);

        [DllImport(Libc)]
        public static extern int kill(int pid, int sig);

        [DllImport(Libc, SetLastError = true)]
        public static extern int waitpid(int pid, out int status, int options);

        /// <summary>Builds a NUL-terminated array of UTF-8 C strings (an <c>argv</c>/<c>envp</c> shape) in unmanaged memory.</summary>
        public static nint BuildNullTerminatedArray(IReadOnlyList<string> values)
        {
            var array = Marshal.AllocCoTaskMem((values.Count + 1) * nint.Size);
            for (var i = 0; i < values.Count; i++)
                Marshal.WriteIntPtr(array, i * nint.Size, Marshal.StringToCoTaskMemUTF8(values[i]));
            Marshal.WriteIntPtr(array, values.Count * nint.Size, nint.Zero);
            return array;
        }

        /// <summary>Frees an array built by <see cref="BuildNullTerminatedArray"/>, including each string it points to.</summary>
        public static void FreeNullTerminatedArray(nint array)
        {
            for (var offset = 0; ; offset += nint.Size)
            {
                var entry = Marshal.ReadIntPtr(array, offset);
                if (entry == nint.Zero) break;
                Marshal.FreeCoTaskMem(entry);
            }
            Marshal.FreeCoTaskMem(array);
        }
    }
}
