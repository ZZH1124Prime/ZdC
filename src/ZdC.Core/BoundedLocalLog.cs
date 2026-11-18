using System.Threading.Channels;

namespace ZdC.Core;

/// <summary>Bounded, single-writer local event log. No network or capture APIs.</summary>
public sealed class BoundedLocalLog : IDisposable, IAsyncDisposable
{
    private const long Limit = 1024 * 1024;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly string _directory;
    private readonly object _gate = new();
    private Task? _writer;
    private bool _closed;
    public BoundedLocalLog(string directory) => _directory = Path.GetFullPath(directory);

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (_closed) return;
            _writer ??= Task.Run(DrainAsync);
            // Bound temporary allocations as well as the queued line.
            var bounded = message.Length > 2048 ? message[..2048] : message;
            var clean = bounded.Replace('\r', ' ').Replace('\n', ' ');

            _queue.Writer.TryWrite($"{DateTimeOffset.Now:O} {clean}{Environment.NewLine}");
        }
    }
    private async Task DrainAsync()
    {
        await foreach (var line in _queue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, "events.log");
                if (File.Exists(path) && new FileInfo(path).Length >= Limit)
                {
                    var previous = path + ".1";
                    var older = path + ".2";
                    if (File.Exists(previous)) File.Move(previous, older, overwrite: true);
                    File.Move(path, previous, overwrite: true);
                }
                await File.AppendAllTextAsync(path, line).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Storage errors must not interrupt routing or grow an in-memory backlog.
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_writer is not null) await _writer.ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _queue.Writer.TryComplete();
        }
        // Do not block UI or capture shutdown on disk latency.
    }
}
