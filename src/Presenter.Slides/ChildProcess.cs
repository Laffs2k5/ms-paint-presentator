using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Presenter.Slides;

/// <summary>
/// A Windows job object with kill-on-close: every process started in it, and
/// everything those start (Node, then Edge and its helpers), ends when the job
/// is terminated or its handle closes, also when the presenter dies. Only
/// processes we started are ever in it, so stopping it never touches anything
/// of the user's.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    public JobObject()
    {
        _handle = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw Native.Failed("CreateJobObject failed");
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(_handle, Native.JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var err = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw Native.Failed("SetInformationJobObject failed", err);
        }
    }

    internal SafeFileHandle Handle => _handle;

    /// <summary>Processes in the job that are still running.</summary>
    public int ActiveProcesses =>
        !_handle.IsClosed && Native.QueryInformationJobObject(_handle, Native.JobObjectBasicAccountingInformation,
            out var acc, Marshal.SizeOf<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? (int)acc.ActiveProcesses
            : 0;

    /// <summary>Ends every process in the job. The job stays usable.</summary>
    public void Terminate()
    {
        if (!_handle.IsClosed) Native.TerminateJobObject(_handle, 1);
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// A console tool started directly inside a <see cref="JobObject"/> (no race
/// between start and assignment), without a window, with stdin on NUL and
/// stdout and stderr into a log file, inheriting only those handles.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly Task<int> _exit;
    private readonly RegisteredWaitHandle _registration;

    public int Id { get; }
    public string LogPath { get; }

    /// <summary>Completes with the exit code.</summary>
    public Task<int> Exited => _exit;

    private ChildProcess(SafeProcessHandle process, int id, string logPath)
    {
        _process = process;
        Id = id;
        LogPath = logPath;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new ProcessWaitHandle(process);
        _registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
        {
            tcs.TrySetResult(Native.GetExitCodeProcess(_process, out var code) ? code : -1);
            wait.Dispose();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        _exit = tcs.Task;
    }

    public static ChildProcess Start(JobObject job, string exe, IEnumerable<string> args, string workingDir,
        IReadOnlyDictionary<string, string?> envOverrides, string logPath)
    {
        var cmdLine = new StringBuilder(Quote(exe));
        foreach (var a in args) cmdLine.Append(' ').Append(Quote(a));

        var sa = new Native.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = 1,
        };
        using var stdin = Native.CreateFileW("NUL", Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            ref sa, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (stdin.IsInvalid) throw Native.Failed("opening NUL failed");
        using var log = Native.CreateFileW(logPath, Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            ref sa, Native.CREATE_ALWAYS, 0, IntPtr.Zero);
        if (log.IsInvalid) throw Native.Failed($"creating {logPath} failed");

        var env = BuildEnvironment(envOverrides);
        IntPtr cmdPtr = IntPtr.Zero, envPtr = IntPtr.Zero, attrList = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero;
        bool attrInit = false;
        try
        {
            cmdPtr = Marshal.StringToHGlobalUni(cmdLine.ToString());
            envPtr = Marshal.StringToHGlobalUni(env);

            IntPtr size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attrList, 2, 0, ref size))
                throw Native.Failed("InitializeProcThreadAttributeList failed");
            attrInit = true;

            handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handles, 0, stdin.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, log.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles,
                    IntPtr.Size * 2, IntPtr.Zero, IntPtr.Zero))
                throw Native.Failed("handle list failed");

            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobs, job.Handle.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_JOB_LIST, jobs,
                    IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Native.Failed("job list failed");

            var si = new Native.STARTUPINFOEX
            {
                cb = Marshal.SizeOf<Native.STARTUPINFOEX>(),
                dwFlags = Native.STARTF_USESTDHANDLES,
                hStdInput = stdin.DangerousGetHandle(),
                hStdOutput = log.DangerousGetHandle(),
                hStdError = log.DangerousGetHandle(),
                lpAttributeList = attrList,
            };
            if (!Native.CreateProcessW(exe, cmdPtr, IntPtr.Zero, IntPtr.Zero, true,
                    Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW,
                    envPtr, workingDir, ref si, out var pi))
                throw Native.Failed($"starting {Path.GetFileName(exe)} failed");
            Native.CloseHandle(pi.hThread);
            return new ChildProcess(new SafeProcessHandle(pi.hProcess, ownsHandle: true), pi.dwProcessId, logPath);
        }
        finally
        {
            if (attrInit) Native.DeleteProcThreadAttributeList(attrList);
            if (attrList != IntPtr.Zero) Marshal.FreeHGlobal(attrList);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
            if (cmdPtr != IntPtr.Zero) Marshal.FreeHGlobal(cmdPtr);
            if (envPtr != IntPtr.Zero) Marshal.FreeHGlobal(envPtr);
        }
    }

    /// <summary>The last part of the tool's output, for error messages.</summary>
    public string LogTail(int chars = 1500)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd().Trim();
            return text.Length <= chars ? text : "..." + text[^chars..];
        }
        catch (IOException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _process.Dispose();
    }

    /// <summary>Quotes an argument the way the C runtime (and so Node) parses it.</summary>
    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static string BuildEnvironment(IReadOnlyDictionary<string, string?> overrides)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (k, v) in overrides)
        {
            if (v is null) env.Remove(k);
            else env[k] = v;
        }
        var sb = new StringBuilder();
        foreach (var (k, v) in env) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0'); // StringToHGlobalUni adds the final terminator
        return sb.ToString();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
