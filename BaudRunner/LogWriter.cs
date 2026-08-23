using System.Text;

namespace BaudRunner;

/// <summary>
/// Buffered file logger with its own writer thread. The producer (the UI thread,
/// under a live stream) only appends to a buffer and signals; every blocking file
/// operation happens on the writer thread, so a slow, full or network-backed log
/// directory cannot stall the UI. Rolls on date change and on a size cap, and
/// reports failure instead of taking the process down with it.
/// </summary>
public sealed class LogWriter : IDisposable
{
    private const int FlushThreshold = 32 * 1024;
    private const int FlushIntervalMs = 250;
    private const long DefaultSizeCap = 256L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly string _directory;
    private readonly string _label;
    private readonly Encoding _encoding;
    private readonly long _sizeCap;

    private StreamWriter? _writer;
    private DateTime _fileDate;
    private long _fileBytes;
    private volatile bool _stopping;
    private volatile bool _dead;

    /// <summary>Raised on the writer thread when logging has stopped because of an I/O failure.</summary>
    public event Action<string>? Failed;

    /// <summary>Raised on the writer thread after the log rolls to a new file.</summary>
    public event Action<string>? Rolled;

    public string FilePath { get; private set; } = "";
    public long BytesWritten => Interlocked.Read(ref _totalBytes);
    private long _totalBytes;

    public LogWriter(string directory, string label, Encoding encoding, long sizeCap = DefaultSizeCap)
    {
        _directory = directory; _label = label; _encoding = encoding; _sizeCap = sizeCap;
        Open(DateTime.Now);
        _thread = new Thread(Run) { IsBackground = true, Name = $"BaudRunner log ({label})" };
        _thread.Start();
    }

    public void Write(string text)
    {
        if (_dead || text.Length == 0) return;
        bool wake;
        lock (_gate)
        {
            _pending.Append(text);
            wake = _pending.Length >= FlushThreshold;
        }
        if (wake) _wake.Set();
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _wake.Set();
        // Bounded: a wedged network path must not hang application shutdown.
        _thread.Join(TimeSpan.FromSeconds(2));
        lock (_gate) { CloseFile(); }
        _wake.Dispose();
    }

    private void Run()
    {
        while (true)
        {
            var stopping = _stopping;
            if (!stopping) _wake.WaitOne(FlushIntervalMs);
            try { Drain(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _dead = true;
                lock (_gate) { _pending.Clear(); CloseFile(); }
                Failed?.Invoke(ex.Message);
                return;
            }
            if (stopping) return;
        }
    }

    private void Drain()
    {
        string batch;
        lock (_gate)
        {
            if (_pending.Length == 0) return;
            batch = _pending.ToString();
            _pending.Clear();
        }

        RollIfNeeded();
        if (_writer is null) return;
        _writer.Write(batch);
        _writer.Flush();

        var written = _encoding.GetByteCount(batch);
        _fileBytes += written;
        Interlocked.Add(ref _totalBytes, written);
    }

    private void RollIfNeeded()
    {
        var now = DateTime.Now;
        if (_writer is not null && _fileDate.Date == now.Date && _fileBytes < _sizeCap) return;
        lock (_gate) { CloseFile(); }
        Open(now);
        if (FilePath.Length > 0) Rolled?.Invoke(FilePath);
    }

    private void Open(DateTime now)
    {
        Directory.CreateDirectory(_directory);
        foreach (var path in CandidatePaths(_directory, _label, now).Append(TimestampedPath(_directory, _label, now)))
        {
            try
            {
                // Skip a candidate that is already at the cap, otherwise a size roll
                // reopens the same file, immediately trips the cap again, and loops.
                if (File.Exists(path) && new FileInfo(path).Length >= _sizeCap) continue;
                var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), _encoding) { AutoFlush = false };
                lock (_gate) { _writer = writer; }
                FilePath = path; _fileDate = now.Date;
                _fileBytes = new FileInfo(path).Length;
                writer.Write($"{Environment.NewLine}==== {_label} log opened {now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}");
                writer.Flush();
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _dead = true;
        Failed?.Invoke($"no writable log file under {_directory}");
    }

    private void CloseFile()
    {
        try { _writer?.Flush(); _writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _writer = null;
    }

    /// <summary>
    /// Per-transport file names so a serial capture and a TCP capture never braid
    /// together, with numbered fallbacks when another process holds the first choice.
    /// </summary>
    public static IEnumerable<string> CandidatePaths(string directory, string label, DateTime now)
    {
        var safeLabel = string.Concat(label.Split(Path.GetInvalidFileNameChars())).Replace(" ", "");
        var prefix = $"BaudRunner-{now:yyyy-MM-dd}-{safeLabel}";
        yield return Path.Combine(directory, $"{prefix}.log");
        for (var index = 2; index <= 10; index++) yield return Path.Combine(directory, $"{prefix}_{index}.log");
    }

    /// <summary>Last-resort name when every numbered candidate is locked or already full.</summary>
    private static string TimestampedPath(string directory, string label, DateTime now)
    {
        var safeLabel = string.Concat(label.Split(Path.GetInvalidFileNameChars())).Replace(" ", "");
        return Path.Combine(directory, $"BaudRunner-{now:yyyy-MM-dd}-{safeLabel}-{now:HHmmssfff}.log");
    }

    /// <summary>Deletes captures older than the retention window. Best effort; never throws.</summary>
    public static int Prune(string directory, TimeSpan retention)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var cutoff = DateTime.Now - retention;
            foreach (var file in Directory.EnumerateFiles(directory, "BaudRunner-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= cutoff) continue;
                    File.Delete(file); removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
