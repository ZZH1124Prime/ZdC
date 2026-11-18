using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZdC.Core;

/// <summary>与既有 applications.json 字段保持一致，额外字段随保存保留。</summary>
public sealed record RegisteredApplication(string Name, string Path,
    string? ProductName = null, string? CompanyName = null)
{
    public string? ShortcutPath { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public string? IconPath { get; init; }
    public int IconIndex { get; init; }
    public int WindowStyle { get; init; } = 1;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed class RegistryChangedException(string message) : IOException(message);

/// <summary>
/// 本机应用名单。构造只保存路径；显式 LoadAsync 后才允许修改。
/// 所有文件、元数据和 JSON 操作在后台执行，组件不会运行任何应用。
/// </summary>
public sealed class ApplicationRegistry
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RegisteredApplication[] _entries = [];
    private byte[]? _baselineHash;
    private bool _loaded;
    public string FilePath { get; }

    public ApplicationRegistry(string? filePath = null)
    {
        FilePath = System.IO.Path.GetFullPath(filePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZdC", "applications.json"));
    }

    public Task<IReadOnlyList<RegisteredApplication>> LoadAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            // 重新读取失败时保留内存中的旧快照，但锁住修改，等待用户修复并重新读取。
            _loaded = false;
            var bytes = await ReadFileAsync(ct).ConfigureAwait(false);
            RegisteredApplication[] candidate;
            if (bytes is null) candidate = [];
            else
            {
                try
                {
                    candidate = JsonSerializer.Deserialize<RegisteredApplication[]>(bytes, JsonOptions)
                        ?? throw new InvalidDataException("应用名单应为 JSON 数组。");
                }
                catch (JsonException ex) { throw new InvalidDataException("应用名单 JSON 格式有误，原文件保持原样。", ex); }
                candidate = Validate(candidate);
            }
            _entries = candidate;
            _baselineHash = bytes is null ? null : SHA256.HashData(bytes);
            _loaded = true;
            if (bytes is not null)
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.EnumerateArray().Any(e => e.EnumerateObject().Any(
                    p => p.Name.Equals("IncludeChildren", StringComparison.OrdinalIgnoreCase))))
                    await SaveAsync(candidate, ct).ConfigureAwait(false);
            }
            return Snapshot();
        }, cancellationToken);

    /// <summary>返回最近一次成功读取或保存的副本；再次 LoadAsync 可主动刷新外部变化。</summary>
    public Task<IReadOnlyList<RegisteredApplication>> ListAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ct => { RequireLoaded(); return Task.FromResult(Snapshot()); }, cancellationToken);

    /// <summary>按完整路径去重；重复注册返回原项，子进程设置由专用方法修改。</summary>
    public Task<RegisteredApplication> RegisterAsync(string executablePath,
        CancellationToken cancellationToken = default) => RunAsync(async ct =>
        {
            RequireLoaded();
            var path = NormalizeExecutablePath(executablePath);
            await VerifyUnchangedAsync(ct).ConfigureAwait(false);
            var existing = _entries.FirstOrDefault(e => SamePath(e.Path, path));
            if (existing is not null) return Clone(existing);
            var entry = ReadIdentity(path);
            await SaveAsync([.. _entries, entry], ct).ConfigureAwait(false);
            return Clone(entry);
        }, cancellationToken);

    public Task<bool> RemoveAsync(string executablePath, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            RequireLoaded();
            var path = NormalizeExecutablePath(executablePath);
            await VerifyUnchangedAsync(ct).ConfigureAwait(false);
            var candidate = _entries.Where(e => !SamePath(e.Path, path)).ToArray();
            if (candidate.Length == _entries.Length) return false;
            await SaveAsync(candidate, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<RegisteredApplication> ImportAsync(RegisteredApplication entry, string? replacePath = null,
        CancellationToken cancellationToken = default) => RunAsync(async ct =>
        {
            RequireLoaded();
            var normalized = Validate([entry]).Single();
            if (!File.Exists(normalized.Path)) throw new FileNotFoundException("应用入口已失效，请拖入新的入口。", normalized.Path);
            await VerifyUnchangedAsync(ct).ConfigureAwait(false);
            var index = replacePath is null ? -1 : Array.FindIndex(_entries, e => SamePath(e.Path, replacePath));
            if (replacePath is not null && index < 0) throw new KeyNotFoundException("要替换的入口已移除。");
            var duplicate = Array.FindIndex(_entries, e => SamePath(e.Path, normalized.Path));
            if (duplicate >= 0 && duplicate != index)
            {
                if (replacePath is null) return Clone(_entries[duplicate]);
                throw new InvalidOperationException("这个应用已有入口，请先移除重复的记录。");
            }
            RegisteredApplication[] candidate;
            if (index < 0) candidate = [.. _entries, normalized];
            else
            {
                normalized = normalized with { AdditionalProperties = Clone(_entries[index]).AdditionalProperties };
                candidate = (RegisteredApplication[])_entries.Clone();
                candidate[index] = normalized;
            }
            await SaveAsync(candidate, ct).ConfigureAwait(false);
            return Clone(normalized);
        }, cancellationToken);

    public static string NormalizeExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择完整的 EXE 文件路径。", nameof(path));
        var normalized = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf(':', 2) >= 0)
            throw new ArgumentException("应用路径应指向 EXE 文件。", nameof(path));
        return normalized;
    }

    private static RegisteredApplication ReadIdentity(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("所选应用文件不存在。", path);
        var info = FileVersionInfo.GetVersionInfo(path);
        var name = string.IsNullOrWhiteSpace(info.FileDescription) ? System.IO.Path.GetFileNameWithoutExtension(path) : info.FileDescription;
        return new(name!, path, info.ProductName, info.CompanyName);
    }
    private static RegisteredApplication[] Validate(RegisteredApplication[] entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new RegisteredApplication[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name)) throw new InvalidDataException("应用名单含有空记录或空名称。");
            string path;
            try { path = NormalizeExecutablePath(entry.Path); }
            catch (ArgumentException ex) { throw new InvalidDataException("应用名单含有无效路径。", ex); }
            if (!seen.Add(path)) throw new InvalidDataException("应用名单含有重复路径。");
            // 已登记文件可能因升级暂时不存在；读取时保留记录，交由调用方处理路径更新。
            if (entry.Arguments is null || entry.Arguments.Any(a => a is null || a.Contains('\0')))
                throw new InvalidDataException("入口参数无效。");
            if (entry.WorkingDirectory is not null && !System.IO.Path.IsPathFullyQualified(entry.WorkingDirectory))
                throw new InvalidDataException("入口起始位置必须是完整路径。");
            var properties = entry.AdditionalProperties?.Where(p => !p.Key.Equals("IncludeChildren", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
            normalized[i] = entry with { Path = path, Arguments = (string[])entry.Arguments.Clone(), AdditionalProperties = properties };
        }
        return normalized;
    }
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static RegisteredApplication Clone(RegisteredApplication entry) => entry with
    {
        Arguments = (string[])entry.Arguments.Clone(),
        AdditionalProperties = entry.AdditionalProperties?.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal)
    };
    private IReadOnlyList<RegisteredApplication> Snapshot() => Array.AsReadOnly(_entries.Select(Clone).ToArray());
    private void RequireLoaded()
    {
        if (!_loaded) throw new InvalidOperationException("请先成功读取应用名单，再进行修改。");
    }

    private async Task<byte[]?> ReadFileAsync(CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("应用名单超过 1 MB，请检查文件内容。");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private async Task VerifyUnchangedAsync(CancellationToken ct)
    {
        var current = await ReadFileAsync(ct).ConfigureAwait(false);
        var hash = current is null ? null : SHA256.HashData(current);
        if ((_baselineHash is null) != (hash is null)
            || (_baselineHash is not null && !CryptographicOperations.FixedTimeEquals(_baselineHash, hash!)))
            throw new RegistryChangedException("应用名单已被其他程序修改，请重新读取后再保存。");
    }
    private async Task SaveAsync(RegisteredApplication[] candidate, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Validate(candidate), JsonOptions);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("保存后的应用名单超过 1 MB。");
        var directory = System.IO.Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        // 同组件的多个实例用独占锁文件协调；已有界面或手动编辑通过内容哈希检测。
        await using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        await VerifyUnchangedAsync(ct).ConfigureAwait(false);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // 写临时文件期间也可能有人修改配置，因此在提交前再次核对。
            await VerifyUnchangedAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_baselineHash is null) File.Move(temporary, FilePath, overwrite: false);
            else File.Replace(temporary, FilePath, null);
            // 原子替换成功后再更新内存；提交之后的取消请求不改变已保存结果。
            _entries = candidate;
            _baselineHash = SHA256.HashData(bytes);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* 配置结果已确定，临时文件可稍后清理。 */ }
            catch (UnauthorizedAccessException) { }
        }
    }
    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => operation(ct), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
