using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace ZdC.Core;

internal static class ShortcutRecovery
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    internal static async Task<RegisteredApplication> ResolveAsync(RegisteredApplication saved,
        CancellationToken token, IEnumerable<string>? searchDirectories = null)
    {
        if (saved.ShortcutPath is not { } link || !Path.IsPathFullyQualified(link) ||
            !Path.GetExtension(link).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return saved;

        var original = await ReadLinkAsync(link, token).ConfigureAwait(false);
        if (original is not null && Paths.Equals(original.Path, saved.Path))
            return KeepCard(saved, original, saved.ShortcutPublisher ?? Publisher.Read(saved.Path) ?? "");

        string? publisher = saved.ShortcutPublisher;
        if (publisher is null && File.Exists(saved.Path)) publisher = Publisher.Read(saved.Path);
        if (original is not null && TrustedReplacement(saved, original, publisher, out var updatedSigner))
            return KeepCard(saved, original, updatedSigner);

        // Installer shortcuts often use the product or EXE name after a user-named shortcut disappears.
        // Names only narrow the lookup. Each result must pass the independent identity and launch checks below.
        var names = new[] { Path.GetFileNameWithoutExtension(link), saved.ProductName, Path.GetFileNameWithoutExtension(saved.Path) }
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .Distinct(Paths).ToArray();
        RegisteredApplication? match = null;
        var directories = searchDirectories ?? ShortcutDirectories(link);
        foreach (var alternate in directories.Distinct(Paths).SelectMany(dir => names.Select(name => Path.Combine(dir, name + ".lnk"))))
        {
            token.ThrowIfCancellationRequested();
            if (Paths.Equals(alternate, link) || !File.Exists(alternate)) continue;
            var candidate = await ReadLinkAsync(alternate, token).ConfigureAwait(false);
            if (candidate is null || !candidate.Arguments.SequenceEqual(saved.Arguments) ||
                !CompatibleWorkingDirectory(saved, candidate) ||
                !TrustedReplacement(saved, candidate, publisher, out var signer)) continue;
            if (match is not null) return saved;
            match = KeepCard(saved, candidate, signer);
        }
        return match ?? saved;
    }

    private static RegisteredApplication KeepCard(RegisteredApplication saved, RegisteredApplication candidate,
        string publisher) => candidate with { Name = saved.Name, ShortcutPublisher = publisher,
            AdditionalProperties = saved.AdditionalProperties };

    private static bool CompatibleWorkingDirectory(RegisteredApplication saved, RegisteredApplication next) =>
        saved.WorkingDirectory is null || Paths.Equals(saved.WorkingDirectory, next.WorkingDirectory) ||
        (Paths.Equals(saved.WorkingDirectory, Path.GetDirectoryName(saved.Path)) &&
         Paths.Equals(next.WorkingDirectory, Path.GetDirectoryName(next.Path)));

    private static async Task<RegisteredApplication?> ReadLinkAsync(string link, CancellationToken token)
    {
        if (!File.Exists(link)) return null;
        try { return await ApplicationEntryReader.ReadAsync(link, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
        { return null; }
    }

    private static IEnumerable<string> ShortcutDirectories(string link)
    {
        yield return Path.GetDirectoryName(link)!;
        foreach (var key in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                     Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var folder = Environment.GetFolderPath(key);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            yield return folder;
            if (key is Environment.SpecialFolder.Programs or Environment.SpecialFolder.CommonPrograms)
            {
                string[] groups;
                try { groups = Directory.GetDirectories(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var group in groups)
                    yield return group;
            }
        }
    }

    // Match the exact executable and installation family. A version change may replace a single directory.
    private static bool SameInstallation(string left, string right)
    {
        var a = left.Split(Path.DirectorySeparatorChar);
        var b = right.Split(Path.DirectorySeparatorChar);
        if (a.Length != b.Length || !Paths.Equals(a[^1], b[^1])) return false;
        var changed = false;
        for (var i = 0; i < a.Length - 1; i++)
        {
            if (Paths.Equals(a[i], b[i])) continue;
            if (changed || i < 2) return false;
            var oldVersion = Regex.Match(a[i], @"^(.*?)[0-9]+(?:[._-][0-9]+)+$");
            var newVersion = Regex.Match(b[i], @"^(.*?)[0-9]+(?:[._-][0-9]+)+$");
            if (!oldVersion.Success || !newVersion.Success || !Paths.Equals(oldVersion.Groups[1].Value,
                    newVersion.Groups[1].Value)) return false;
            changed = true;
        }
        return changed;
    }

    private static bool TrustedReplacement(RegisteredApplication saved, RegisteredApplication candidate,
        string? publisher, out string candidatePublisher)
    {
        if (Paths.Equals(saved.Path, candidate.Path))
        {
            candidatePublisher = publisher ?? Publisher.Read(candidate.Path) ?? "";
            return true;
        }
        candidatePublisher = "";
        if (string.IsNullOrWhiteSpace(saved.ProductName) || string.IsNullOrWhiteSpace(saved.CompanyName) ||
            !Paths.Equals(saved.ProductName, candidate.ProductName) || !Paths.Equals(saved.CompanyName, candidate.CompanyName) ||
            !SameInstallation(saved.Path, candidate.Path)) return false;
        candidatePublisher = Publisher.Read(candidate.Path) ?? "";
        if (candidatePublisher.Length == 0) return false;
        if (!string.IsNullOrEmpty(publisher)) return string.Equals(publisher, candidatePublisher, StringComparison.Ordinal);
        return publisher is null && MachineInstallerOwns(candidate);
    }

    // Older cards have no saved signer. Bootstrap only from a machine-wide installer identifying this exact EXE.
    private static bool MachineInstallerOwns(RegisteredApplication candidate)
    {
        if (!new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Any(p => p.Length > 0 && candidate.Path.StartsWith(
                p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" +
                    Path.GetFileNameWithoutExtension(candidate.Path));
                if (key?.GetValue("DisplayIcon") is not string icon ||
                    key.GetValue("InstallLocation") is not string location ||
                    !Paths.Equals(key.GetValue("DisplayName") as string, candidate.ProductName)) continue;
                icon = Regex.Replace(icon.Trim(), @",\s*-?\d+\s*$", "").Trim('"');
                if (Paths.Equals(icon, candidate.Path) &&
                    Paths.Equals(Path.TrimEndingDirectorySeparator(location.Trim('"')), Path.GetDirectoryName(candidate.Path))) return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
        return false;
    }

    internal static class Publisher
    {
        // Authenticode policy with cached trust only. No UI and no downloads during registration or recovery.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo { public uint Size; public string Path; public IntPtr Handle, KnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size;
            public IntPtr Policy, Client;
            public uint UiChoice, Revocation, UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr State, Url;
            public uint Flags, UiContext;
            public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, in Guid action, ref TrustData data);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr data, uint index, int counterSigner, uint counterIndex);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderCert { public uint Size; public IntPtr Context; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CertContext { public uint Encoding; public IntPtr Encoded; public uint Length; }

        internal static string? Read(string path)
        {
            var file = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path };
            var ptr = Marshal.AllocHGlobal((int)file.Size);
            Marshal.StructureToPtr(file, ptr, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2,
                UnionChoice = 1, File = ptr, StateAction = 1, Flags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            try
            {
                if (WinVerifyTrust(new IntPtr(-1), in action, ref data) != 0) return null;
                var provider = WTHelperProvDataFromStateData(data.State);
                if (provider == IntPtr.Zero) return null;
                var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
                if (signer == IntPtr.Zero) return null;
                var certificate = WTHelperGetProvCertFromChain(signer, 0);
                if (certificate == IntPtr.Zero) return null;
                var context = Marshal.PtrToStructure<ProviderCert>(certificate).Context;
                if (context == IntPtr.Zero) return null;
                var encoded = Marshal.PtrToStructure<CertContext>(context);
                if (encoded.Encoded == IntPtr.Zero || encoded.Length is 0 or > 1024 * 1024) return null;
                var bytes = new byte[encoded.Length];
                Marshal.Copy(encoded.Encoded, bytes, 0, bytes.Length);
                using var cert = X509CertificateLoader.LoadCertificate(bytes);
                return cert.Subject;
            }
            catch (CryptographicException) { return null; }
            finally
            {
                if (data.State != IntPtr.Zero)
                {
                    data.StateAction = 2;
                    WinVerifyTrust(new IntPtr(-1), in action, ref data);
                }
                Marshal.DestroyStructure<FileInfo>(ptr);
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
