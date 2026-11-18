using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ZdC.Core;

public sealed record ApplicationBlockingProcess(int ProcessId, string Name, string ExecutablePath, long CreatedFileTimeUtc);

// The launch guard and confirmation list share exactly the same image set.
// Termination accepts a frozen list and verifies identity on the handle it uses.
internal static class ApplicationProcessControl
{
    private const uint Query = 0x1000, Synchronize = 0x100000, Terminate = 1;
    internal static string[] BlockingImages(string application)
    {
        var path = Path.GetFullPath(application);
        var weixin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "Weixin", "Weixin.exe");
        return path.Equals(weixin, StringComparison.OrdinalIgnoreCase)
            ? [path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Tencent", "xwechat", "xplugin", "plugins", "RadiumWMPF", "WeChatAppEx.exe")]
            : [path];
    }

    internal static IReadOnlyList<ApplicationBlockingProcess> List(string application)
    {
        var images = BlockingImages(application);
        using var own = Process.GetCurrentProcess();
        var ownSession = own.SessionId;
        var result = new List<ApplicationBlockingProcess>();
        foreach (var name in images.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    if (!ProcessIdToSessionId(process.Id, out var session) || session != ownSession) continue;
                    using var handle = OpenProcess(Query | Synchronize, false, process.Id);
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 87) continue; // The process exited between enumeration and opening.
                        throw new Win32Exception(error);
                    }
                    var item = Read(handle, process.Id);
                    if (item is not null && images.Contains(item.ExecutablePath, StringComparer.OrdinalIgnoreCase) && SameUser(handle)) result.Add(item);
                }
        return result.OrderBy(x => x.ProcessId).ToArray();
    }

    internal static IReadOnlyList<ApplicationBlockingProcess> ListKnown(IEnumerable<(int Pid, long Created)> members)
    {
        var result = new List<ApplicationBlockingProcess>();
        using var own = Process.GetCurrentProcess();
        foreach (var (pid, created) in members)
        {
            if (!ProcessIdToSessionId(pid, out var session) || session != own.SessionId) continue;
            using var handle = OpenProcess(Query | Synchronize, false, pid);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 87 || (error == 5 && HasExited(pid))) continue;
                throw new Win32Exception(error);
            }
            var item = Read(handle, pid);
            if (item is not null && item.CreatedFileTimeUtc == created && SameUser(handle)) result.Add(item);
        }
        return result;
    }

    internal static IReadOnlyList<int> Stop(string application, IReadOnlyList<ApplicationBlockingProcess> confirmed,
        IReadOnlyList<ApplicationBlockingProcess> known, Action<int>? onStopped)
    {
        var images = BlockingImages(application);
        using var own = Process.GetCurrentProcess();
        var ownSession = own.SessionId;
        var stopped = new List<int>();
        foreach (var item in confirmed.DistinctBy(x => x.ProcessId))
        {
            if (!images.Contains(item.ExecutablePath, StringComparer.OrdinalIgnoreCase) &&
                !known.Any(x => x.ProcessId == item.ProcessId && x.CreatedFileTimeUtc == item.CreatedFileTimeUtc &&
                    x.ExecutablePath.Equals(item.ExecutablePath, StringComparison.OrdinalIgnoreCase))) continue;
            if (!ProcessIdToSessionId(item.ProcessId, out var session) || session != ownSession) continue;
            using var handle = OpenProcess(Query | Synchronize | Terminate, false, item.ProcessId);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 87 || (error == 5 && HasExited(item.ProcessId))) continue;
                throw new Win32Exception(error);
            }
            var current = Read(handle, item.ProcessId);
            if (current is null || current.CreatedFileTimeUtc != item.CreatedFileTimeUtc ||
                !current.ExecutablePath.Equals(item.ExecutablePath, StringComparison.OrdinalIgnoreCase) || !SameUser(handle)) continue;
            if (!TerminateProcess(handle, 1))
            {
                var error = Marshal.GetLastWin32Error();
                if (WaitForSingleObject(handle, error == 5 ? 150u : 0u) == 0) continue;
                throw new Win32Exception(error);
            }
            var wait = WaitForSingleObject(handle, 3000);
            if (wait == 0x102) throw new TimeoutException();
            if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            stopped.Add(item.ProcessId);
            onStopped?.Invoke(item.ProcessId);
        }
        return stopped;
    }

    private static ApplicationBlockingProcess? Read(SafeProcessHandle handle, int pid)
    {
        if (WaitForSingleObject(handle, 0) == 0) return null;
        var path = new StringBuilder(32768);
        var count = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref count) ||
            !GetProcessTimes(handle, out var created, out _, out _, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (WaitForSingleObject(handle, 0) == 0) return null;
            throw new Win32Exception(error);
        }
        var image = Path.GetFullPath(path.ToString());
        return new(pid, Path.GetFileName(image), image, created);
    }

    private static bool SameUser(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var own = WindowsIdentity.GetCurrent())
            return identity.User is not null && identity.User == own.User;
    }

    private static bool HasExited(int pid)
    {
        // Ending a parent may make a confirmed child exit before its turn.
        // Windows can briefly deny TERMINATE while that exit is in progress.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var handle = OpenProcess(Query | Synchronize, false, pid);
            if (handle.IsInvalid)
            {
                if (Marshal.GetLastWin32Error() == 87) return true;
                Thread.Sleep(50);
            }
            else if (WaitForSingleObject(handle, 50) == 0) return true;
        }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);
}
