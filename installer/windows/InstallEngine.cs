using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal sealed class Installed
{
    internal string Code, Directory, Version;
    internal bool Desktop;
}

internal static class InstallEngine
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumRelatedProductsW(string family, uint reserved, uint index, StringBuilder product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoW(string product, string name, StringBuilder value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MsiQueryFeatureStateW(string product, string feature);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiInstallProductW(string package, string properties);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiSetInternalUI(uint level, ref IntPtr owner);

    internal sealed class AppInUseException : Exception { }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MessageHandler(IntPtr context, uint type, uint record);
    [DllImport("msi.dll",ExactSpelling=true)]
    private static extern uint MsiSetExternalUIRecord(MessageHandler handler,uint filter,IntPtr context,out IntPtr previous);
    [DllImport("msi.dll",ExactSpelling=true)]
    private static extern int MsiRecordGetInteger(uint record,uint field);

    // Windows Installer progress messages reset, add or advance ticks. Error/UI messages remain with the engine.
    private sealed class Meter
    {
        private readonly IProgress<int> output;
        private readonly CancellationToken cancellation;
        private int cancellationSent;
        private long total,position,actionTicks;
        private bool reverse;
        private int last=-1;
        internal Meter(IProgress<int> output,CancellationToken cancellation)
        {this.output=output;this.cancellation=cancellation;}
        internal int OnMessage(IntPtr context,uint type,uint record)
        {
            uint kind=type & 0xff000000u;
            // IDCANCEL on a progress/action-data message asks MSI to cancel. Subsequent callbacks belong to MSI, including rollback.
            if((kind==0x09000000u || kind==0x0a000000u) && cancellation.IsCancellationRequested &&
                Interlocked.Exchange(ref cancellationSent,1)==0) return 2;
            switch(type & 0xff000000u) {
                case 0x09000000u: position+=reverse?-actionTicks:actionTicks;break;
                case 0x0a000000u:
                    int op=MsiRecordGetInteger(record,1),ticks=MsiRecordGetInteger(record,2),flag=MsiRecordGetInteger(record,3);
                    if(ticks<0)return 1;
                    switch(op) {
                        case 0: total=ticks; reverse=flag!=0; position=reverse?total:0;actionTicks=0;break;
                        case 1: actionTicks=flag!=0?ticks:0;break;
                        case 2: position+=reverse?-ticks:ticks;break;
                        case 3: total+=ticks;break;
                    }
                    break;
                default: return 0;
            }
            if(total>0) {
                position=Math.Max(0,Math.Min(total,position));
                int percent=(int)Math.Min(99,position*100/total);
                if(percent!=last){last=percent;output.Report(percent);}
            }
            return 1;
        }
    }

    private static string ReadInfo(string product, string key)
    {
        uint length = 0;
        uint result = MsiGetProductInfoW(product, key, null, ref length);
        if (result != 0 && result != 234) throw new Win32Exception((int)result);
        var buffer = new StringBuilder(checked((int)++length));
        result = MsiGetProductInfoW(product, key, buffer, ref length);
        if (result != 0) throw new Win32Exception((int)result);
        return buffer.ToString();
    }

    /// <summary>Returns the one registered MSI installation. Multiple matches have no unambiguous directory to update.</summary>
    internal static Installed Find()
    {
        var code = new StringBuilder(39);
        uint status = MsiEnumRelatedProductsW(Identity.Family, 0, 0, code);
        if (status == 259) return null;
        if (status != 0) throw new Win32Exception((int)status);
        string product = code.ToString();
        status = MsiEnumRelatedProductsW(Identity.Family, 0, 1, code);
        if (status != 259) throw new Win32Exception(status == 0 ? 1638 : (int)status);
        string path = ReadInfo(product, "InstallLocation");
        if (String.IsNullOrWhiteSpace(path)) throw new Win32Exception(1610);
        int desktop = MsiQueryFeatureStateW(product, "Desktop");
        if (desktop != 2 && desktop != 3 && desktop != 1) throw new Win32Exception(1610);
        return new Installed { Code=product, Directory=path, Version=ReadInfo(product,"VersionString"), Desktop=desktop==3 };
    }

    internal static bool AppRunning()
    {
        try { using (var mutex = Mutex.OpenExisting(@"Local\ZdC.ConfigEditor")) return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static bool RuntimeAvailable()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") };
        foreach (var view in new[] {RegistryView.Registry32, RegistryView.Registry64})
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))
        using (var key = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
        {
            var path = key == null ? null : key.GetValue("InstallLocation") as string;
            if (!String.IsNullOrWhiteSpace(path)) roots.Add(path);
        }
        foreach (string root in roots)
        {
            bool okay = true;
            foreach (string fx in new[] {"Microsoft.WindowsDesktop.App","Microsoft.NETCore.App"})
            {
                var loc = Path.Combine(root,"shared",fx);
                okay &= Directory.Exists(loc) && Directory.EnumerateDirectories(loc,"9.0.*").Any(d =>
                    Version.TryParse(Path.GetFileName(d),out var v) && File.Exists(Path.Combine(d,
                    fx == "Microsoft.NETCore.App" ? "coreclr.dll" : "PresentationFramework.dll")));
            }
            if (okay) return true;
        }
        return false;
    }

    /// <summary>Validates the chosen local directory without writing to it. MSI owns installation of the individual files.</summary>
    internal static string CheckDirectory(string input, Installed existing)
    {
        if (String.IsNullOrWhiteSpace(input) || input.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            input.Length < 3 || !Char.IsLetter(input[0]) || input[1] != ':' || input[2] != '\\' ||
            input.Substring(2).Contains(":") || input.IndexOfAny(new[]{'"','*','?'}) >= 0)
            throw new ArgumentException();
        if (input.Substring(3).Split('\\').Any(part => part == "." || part == ".." || part.EndsWith(".",StringComparison.Ordinal) || part.EndsWith(" ",StringComparison.Ordinal)))
            throw new ArgumentException();
        string path = Path.GetFullPath(input).TrimEnd('\\');
        if (path.Length <= 3 || File.Exists(path)) throw new ArgumentException();
        var drive = new DriveInfo(Path.GetPathRoot(path));
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed) throw new ArgumentException();
        for (var d = new DirectoryInfo(path); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException();
        if (existing == null && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) throw new ArgumentException();
        if (existing != null && !String.Equals(path,Path.GetFullPath(existing.Directory).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
        if (drive.AvailableFreeSpace < Identity.Size) throw new Win32Exception(112);
        return path;
    }

    /// <summary>Installs the pinned embedded database through Windows Installer. The native engine owns elevation and rollback.</summary>
    internal static uint Apply(Installed existing, string path, bool desktop, IntPtr owner, IProgress<int> progress, CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested) return 1602;
        var now = Find();
        if ((existing == null) != (now == null) || (now != null && (!String.Equals(existing.Code,now.Code,StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(existing.Directory,now.Directory,StringComparison.OrdinalIgnoreCase)))) return 1638;
        if (now != null && Version.Parse(now.Version) > Version.Parse(Identity.Version)) return 1638;
        path = CheckDirectory(path,now);
        if (AppRunning()) throw new AppInUseException();
        string source = Path.Combine(Path.GetTempPath(),"ZdC-"+Guid.NewGuid().ToString("N")+".msi");
        bool owned = false;
        try
        {
            using (var output = new FileStream(source,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZdC.msi"))
            { owned = true; input.CopyTo(output); output.Flush(true); }
            using (var guard = File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            using (var hash = SHA256.Create())
            {
                if (!String.Equals(BitConverter.ToString(hash.ComputeHash(guard)).Replace("-",""),Identity.PackageSha,StringComparison.Ordinal)) return 1620;
                string properties = "REBOOT=ReallySuppress INSTALLDIR=\"" + path + "\" ADDLOCAL=" + (desktop ? "Main,Desktop" : "Main");
                if (now != null && String.Equals(now.Code,Identity.Product,StringComparison.OrdinalIgnoreCase))
                {
                    properties += " REINSTALL=Main REINSTALLMODE=amus";
                    if (!desktop && now.Desktop) properties += " REMOVE=Desktop";
                }
                var previousOwner=owner;
                var previousLevel=MsiSetInternalUI(2u | 0x200u, ref previousOwner);
                var meter=new Meter(progress,cancellation);
                MessageHandler handler=meter.OnMessage;
                bool installedHandler=false;
                try
                {
                    var registration=MsiSetExternalUIRecord(handler,(1u<<9)|(1u<<10),IntPtr.Zero,out _);
                    if(registration!=0)return registration;
                    installedHandler=true;
                    if(cancellation.IsCancellationRequested)return 1602;
                    return MsiInstallProductW(source,properties);
                }
                finally
                {
                    if(installedHandler)MsiSetExternalUIRecord(null,0,IntPtr.Zero,out _);
                    MsiSetInternalUI(previousLevel,ref previousOwner);
                    GC.KeepAlive(handler);
                }
            }
        }
        finally
        {
            if (owned) try { File.Delete(source); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
