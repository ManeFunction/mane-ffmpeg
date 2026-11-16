using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CliWrap;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Windows;

/// <summary>
/// Pausing ffmpeg and keeping the computer awake. ffmpeg (and any process it starts) runs in a job object that kills it
/// when the app's handle to the job closes, which also happens when the app crashes, so a suspended ffmpeg never outlives it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPlatform
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const uint SnapProcess = 0x2;

    private static readonly Lazy<nint> Job = new(CreateKillOnCloseJob);

    public CommandTask<CommandResult> StartPausable(string executable, IReadOnlyList<string> arguments, Action<int> started,
        Action<string> output, Action<string> error, CancellationToken ct)
    {
        var task = Cli.Wrap(executable)
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(output))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(error))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        AddToJob(task.ProcessId);
        started(task.ProcessId);
        return task;
    }

    /// <summary>
    /// Suspends <paramref name="pid"/> and the processes it started: Chocolatey's and Scoop's ffmpeg.exe is a shim that runs
    /// the real ffmpeg as its child, so suspending the shim alone would leave the export running.
    /// </summary>
    public IFrozenProcess Freeze(int pid)
    {
        var tree = ProcessTree(pid);
        foreach (var id in tree)
        {
            // A shim's child may have started before the shim joined the job.
            AddToJob(id);
            Call(id, NtSuspendProcess, "suspend");
        }
        return new SuspendedProcesses(tree);
    }

    private sealed class SuspendedProcesses(IReadOnlyList<int> tree) : IFrozenProcess
    {
        private bool _done;

        public void Resume()
        {
            if (_done)
                return;
            _done = true;
            foreach (var id in tree)
                Call(id, NtResumeProcess, "resume");
        }

        /// <summary>Nothing watches suspended processes here: the job object kills them with the app.</summary>
        public void Release() => _done = true;
    }

    private static void Call(int pid, Func<nint, int> call, string verb)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var status = call(process.Handle);
            if (status != 0)
                AppLog.Write($"Can't {verb} pid {pid}: NTSTATUS 0x{status:x8}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Can't {verb} pid {pid}: {ex.Message}");
        }
    }

    /// <summary>
    /// <paramref name="root"/> and its descendants, root first. Windows reuses process ids, and a parent id can name a
    /// process that has since exited, so only processes started after the root count.
    /// </summary>
    internal static IReadOnlyList<int> ProcessTree(int root)
    {
        var parents = ParentIds();
        DateTime? Started(int id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return process.StartTime;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
        var rootStarted = Started(root);
        var tree = new List<int> { root };
        for (var i = 0; i < tree.Count; i++)
        {
            foreach (var (child, parent) in parents)
                if (parent == tree[i] && child != root && !tree.Contains(child)
                    && (rootStarted is null || Started(child) is not { } started || started >= rootStarted))
                    tree.Add(child);
        }
        return tree;
    }

    /// <summary>Every running process with its parent, from a Toolhelp snapshot.</summary>
    private static unsafe List<(int Id, int Parent)> ParentIds()
    {
        var result = new List<(int, int)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1)
            return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private static void AddToJob(int pid)
    {
        try
        {
            if (Job.Value == 0)
                return;
            using var process = Process.GetProcessById(pid);
            if (IsProcessInJob(process.Handle, Job.Value, out var inJob) && inJob)
                return;
            if (!AssignProcessToJobObject(Job.Value, process.Handle))
                AppLog.Write($"Can't tie pid {pid} to the app: error {Marshal.GetLastPInvokeError()}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It has already exited.
        }
    }

    /// <summary>A job that kills its processes once its last handle closes: this process's, which closes as it exits.</summary>
    private static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            AppLog.Write($"Can't create a job object: error {Marshal.GetLastPInvokeError()}");
            return 0;
        }
        var info = new JobObjectExtendedLimit { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimit>()))
            AppLog.Write($"Can't set up the job object: error {Marshal.GetLastPInvokeError()}");
        return job;
    }

    // Power: a power request, which the system drops if the app dies. It shows in `powercfg /requests`.

    public IDisposable KeepAwake(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = text };
            var request = PowerCreateRequest(ref context);
            if (request is 0 or -1)
            {
                AppLog.Write($"Can't keep the computer awake: PowerCreateRequest failed with error {Marshal.GetLastPInvokeError()}");
                return NoOp.Instance;
            }
            if (!PowerSetRequest(request, PowerRequestSystemRequired))
            {
                AppLog.Write($"Can't keep the computer awake: PowerSetRequest failed with error {Marshal.GetLastPInvokeError()}");
                CloseHandle(request);
                return NoOp.Instance;
            }
            return new PowerRequest(request);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Can't keep the computer awake: {ex.Message}");
            return NoOp.Instance;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private sealed class PowerRequest(nint handle) : IDisposable
    {
        private nint _handle = handle;

        public void Dispose()
        {
            if (_handle == 0)
                return;
            PowerClearRequest(_handle, PowerRequestSystemRequired);
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        // The rest of the union: the detailed form (module, resource id, string count, strings) is larger.
        private nint _detailed1;
        private nint _detailed2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, uint infoClass, ref JobObjectExtendedLimit info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint request, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint request, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
