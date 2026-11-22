using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ZdC.Core;

/// <summary>Reads an entry without launching it or modifying the original shortcut.</summary>
public static class ApplicationEntryReader
{
    public static Task<RegisteredApplication> ReadAsync(string sourcePath,
        CancellationToken token = default) => ReadOnStaAsync(() => Read(sourcePath), token);

    internal static Task<string> ReadShortcutTargetAsync(string shortcut, CancellationToken token) =>
        ReadOnStaAsync(() => ReadShortcut(shortcut, link => NormalizeTarget((string)link.TargetPath)), token);

    private static Task<T> ReadOnStaAsync<T>(Func<T> read, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { token.ThrowIfCancellationRequested(); completion.SetResult(read()); }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "ZdC entry reader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static RegisteredApplication Read(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("入口文件不存在，请拖入新的快捷方式。", source);
        var extension = Path.GetExtension(source);
        string target, arguments = "", directory = "", icon = "";
        var show = 1;
        var shortcut = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        if (shortcut)
        {
            (target, arguments, directory, icon, show) = ReadShortcut(source, link =>
                ((string)link.TargetPath, (string)link.Arguments, (string)link.WorkingDirectory,
                 (string)link.IconLocation, (int)link.WindowStyle));
        }
        else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) target = source;
        else throw new ArgumentException("请拖入指向桌面应用的 .lnk 快捷方式或 .exe 文件。");
        target = NormalizeTarget(target);
        if (!File.Exists(target)) throw new FileNotFoundException("快捷方式目标已失效，请拖入新的入口。", target);
        var version = FileVersionInfo.GetVersionInfo(target);
        var iconPath = target;
        var iconIndex = 0;
        var separator = icon.LastIndexOf(',');
        if (separator >= 0 && int.TryParse(icon.AsSpan(separator + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed))
        {
            var candidate = Environment.ExpandEnvironmentVariables(icon[..separator].Trim().Trim('"'));
            if (Path.IsPathFullyQualified(candidate)) { iconPath = candidate; iconIndex = parsed; }
        }
        var workingDirectory = string.IsNullOrWhiteSpace(directory) ? Path.GetDirectoryName(target)!
            : Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException("快捷方式的起始位置已失效，请修正入口后重新拖入。");
        return new RegisteredApplication(shortcut ? Path.GetFileNameWithoutExtension(source)
            : version.FileDescription ?? Path.GetFileNameWithoutExtension(target), target,
            version.ProductName, version.CompanyName)
        {
            ShortcutPath = shortcut ? source : null, Arguments = SplitArguments(arguments),
            WorkingDirectory = Path.GetFullPath(workingDirectory), IconPath = iconPath, IconIndex = iconIndex,
            WindowStyle = show is 1 or 3 or 7 ? show : 1
        };
    }

    private static string NormalizeTarget(string target) =>
        ApplicationRegistry.NormalizeExecutablePath(Environment.ExpandEnvironmentVariables(target));

    private static T ReadShortcut<T>(string source, Func<dynamic, T> read)
    {
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            link = ((dynamic)shell).CreateShortcut(source);
            return read(link);
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string[] SplitArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        var pointer = CommandLineToArgvW("ZdC.exe " + arguments, out var count);
        if (pointer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var result = new string[count - 1];
            for (var i = 1; i < count; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
