using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// A Win32 Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process assigned to it,
/// plus every process those processes later start, is killed when this is disposed.
///
/// Exists because killing just <c>PolyTerm.exe</c> leaves its children running — WebView2's
/// <c>msedgewebview2</c> processes, and the real default browser <c>HttpLauncherTests</c>' open-externally
/// test launches. Those orphans inherit the test host's redirected stdout/stderr handles, so
/// <c>dotnet test</c> never sees the pipe close and hangs after every test has already passed —
/// confirmed on the v1.0.0 release run, which sat in exactly that state until GitHub's 6-hour job
/// limit killed it (the runner's cleanup then reported orphaned <c>msedge</c> processes).
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private nint _handle;

    public KillOnCloseJob()
    {
        _handle = CreateJobObject(0, null);
        if (_handle == 0) throw new Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)size))
        {
            var error = new Win32Exception();
            CloseHandle(_handle);
            throw error;
        }
    }

    /// <summary>Adds <paramref name="processId"/> (and so every process it starts from now on) to the job.</summary>
    public void Assign(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception();
    }

    /// <summary>Kills every process still in the job.</summary>
    public void Dispose()
    {
        if (_handle == 0) return;
        CloseHandle(_handle);
        _handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
