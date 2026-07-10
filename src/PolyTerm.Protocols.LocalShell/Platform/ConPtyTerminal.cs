using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PolyTerm.Protocols.LocalShell.Platform;

/// <summary>
/// Windows ConPTY implementation.
/// Requires Windows 10 1809+ (build 17763).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtyTerminal : IPseudoTerminal
{
    private readonly AnonymousPipeServerStream _inputRead;
    private readonly AnonymousPipeClientStream _inputWrite;
    private readonly AnonymousPipeServerStream _outputWrite;
    private readonly AnonymousPipeClientStream _outputRead;
    private readonly nint _pty;
    private readonly nint _procInfo;
    private readonly Task _processExited;
    private bool _disposed;

    /// <inheritdoc/>
    public Stream Input => _inputWrite;

    /// <inheritdoc/>
    public Stream Output => _outputRead;

    /// <summary>Creates the ConPTY, starts <paramref name="shell"/> inside it, and begins tracking process exit.</summary>
    public ConPtyTerminal(string shell, string? workingDir, int cols, int rows)
    {
        // NOTE: do not call DisposeLocalCopyOfClientHandle() here — AnonymousPipeClientStream's
        // constructor adopts the exact SafeHandle object returned by ClientSafePipeHandle (no
        // duplication) for this same-process usage, so disposing the server's "local copy" also
        // disposes the client stream's handle, guaranteeing an ObjectDisposedException on the
        // very first read/write. Confirmed by direct reproduction — see PR discussion.
        _inputRead   = new AnonymousPipeServerStream(PipeDirection.In,  HandleInheritability.Inheritable);
        _inputWrite  = new AnonymousPipeClientStream(PipeDirection.Out, _inputRead.ClientSafePipeHandle);

        _outputWrite = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        _outputRead  = new AnonymousPipeClientStream(PipeDirection.In,  _outputWrite.ClientSafePipeHandle);

        var hr = NativeMethods.CreatePseudoConsole(
            new NativeMethods.COORD { X = (short)cols, Y = (short)rows },
            _inputRead.SafePipeHandle.DangerousGetHandle(),
            _outputWrite.SafePipeHandle.DangerousGetHandle(),
            0, out _pty);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        _procInfo = NativeMethods.StartProcessInPseudoConsole(shell, workingDir, _pty);
        var exitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(_procInfo, false);
        _processExited = Task.Run(() =>
        {
            _ = NativeMethods.WaitForSingleObject(exitHandle.DangerousGetHandle(), 0xFFFFFFFF /*INFINITE*/);
        });
    }

    /// <inheritdoc/>
    public void Resize(int cols, int rows)
    {
        _ = NativeMethods.ResizePseudoConsole(_pty, new NativeMethods.COORD { X = (short)cols, Y = (short)rows });
    }

    /// <inheritdoc/>
    public Task WaitForExitAsync(CancellationToken ct) => _processExited.WaitAsync(ct);

    /// <summary>Closes the pseudo-console handle, the hosted process's own handle, and disposes the pipe streams. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.ClosePseudoConsole(_pty);
        // A real, confirmed leak until this line: pi.hProcess (the hosted shell process's own kernel
        // HANDLE, captured into _procInfo by StartProcessInPseudoConsole) was never closed anywhere —
        // a bare nint field gets no automatic cleanup the way a SafeHandle would. Found auditing this
        // class's disposal path for issue #2 (the intermittent STATUS_DLL_INIT_FAILED crash) - this
        // leaks a *process* handle specifically, a different (much larger) kernel resource than the
        // desktop heap that issue's own leading theory points at, so fixing this alone likely doesn't
        // fix that crash, but it's a real bug regardless: every local-shell tab opened and closed
        // during one PolyTerm.exe run leaked one handle, for the process's entire remaining lifetime.
        // Verified directly, not assumed: Process.GetCurrentProcess().HandleCount grew by exactly 15
        // for 15 create+dispose cycles with this line removed (a precise 1:1 leak), and stayed flat
        // with it restored. Deliberately not a permanent xunit test - a global process-handle-count
        // delta is inherently contaminated by whatever else is running concurrently in the same
        // process, and this project's test suite parallelizes across ~175 other tests in this same
        // assembly (confirmed: this exact check passed 4/4 in isolation, then failed under a full
        // suite run with unrelated growth from other tests). Matches this file's own established
        // precedent for this category of check - the original STATUS_DLL_INIT_FAILED investigation
        // (see issue #2) used a throwaway isolated standalone harness for the same reason, not a
        // permanent CI-integrated test.
        NativeMethods.CloseHandle(_procInfo);
        _inputWrite.Dispose();
        _inputRead.Dispose();
        _outputRead.Dispose();
        _outputWrite.Dispose();
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct COORD { public short X; public short Y; }

        [DllImport("kernel32", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, nint hInput, nint hOutput, uint flags, out nint phPC);

        [DllImport("kernel32")]
        public static extern void ClosePseudoConsole(nint hPC);

        [DllImport("kernel32")]
        public static extern int ResizePseudoConsole(nint hPC, COORD size);

        [DllImport("kernel32", SetLastError = true)]
        public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

        public static nint StartProcessInPseudoConsole(string shell, string? workingDir, nint hPC)
        {
            // Use PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE via extended startup info
            const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
            const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

            var attrListSize = nint.Zero;
            InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attrListSize);
            var attrList = Marshal.AllocHGlobal(attrListSize);

            try
            {
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");

                var hpcVal = hPC;
                if (!UpdateProcThreadAttribute(attrList, 0, (nint)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, ref hpcVal, (nint)nint.Size, nint.Zero, nint.Zero))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed.");

                var si = new STARTUPINFOEX
                {
                    StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                    lpAttributeList = attrList,
                };

                if (!CreateProcess(null, shell, nint.Zero, nint.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT, nint.Zero, workingDir,
                        ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Failed to start '{shell}'.");

                CloseHandle(pi.hThread);
                return pi.hProcess;
            }
            finally
            {
                Marshal.FreeHGlobal(attrList);
            }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine,
            nint lpProcessAttributes, nint lpThreadAttributes, bool bInheritHandles,
            int dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(nint lpAttributeList, uint dwFlags, nint Attribute, ref nint lpValue, nint cbSize, nint lpPreviousValue, nint lpReturnSize);

        [DllImport("kernel32")]
        public static extern bool CloseHandle(nint hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO { public int cb; private nint _reserved1; private nint _desktop; private nint _title; private int _x, _y, _xSize, _ySize, _xCountChars, _yCountChars, _fillAttribute; private int _flags; private short _showWindow, _reserved2; private nint _reserved3; private nint _hStdInput, _hStdOutput, _hStdError; }
        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public nint lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public nint hProcess; public nint hThread; public int dwProcessId; public int dwThreadId; }
    }
}
