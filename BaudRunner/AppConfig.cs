using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaudRunner;

public sealed class AppConfig
{
    /// <summary>Bumped when a migration is needed; lets a future version recognise an old file rather than guess.</summary>
    public int SchemaVersion { get; set; } = 2;

    public Dictionary<string, TerminalConfig> Terminals { get; set; } = new();
    public string Theme { get; set; } = "Dark";
    public WindowGeometry Window { get; set; } = new();
    public int ScrollbackLines { get; set; } = 5000;
    public int LogRetentionDays { get; set; } = 30;
    public string SelectedTab { get; set; } = "";

    // Enums as names, not ordinals: this file is meant to be readable and editable by
    // hand, and "TimestampMode": 2 tells nobody anything. Numbers are still accepted on
    // read, so files written by earlier versions keep loading.
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaudRunner");
    private static string FileName => Path.Combine(Directory, "config.json");
    private static string BackupName => FileName + ".bak";

    /// <summary>
    /// Loads the settings, falling back to the rolling backup when the main file is
    /// unreadable. A corrupt file is preserved rather than silently replaced, because
    /// it holds every quick command the user has ever typed.
    /// </summary>
    public static AppConfig Load(out string? warning)
    {
        warning = null;
        if (TryRead(FileName, out var config)) return config!;

        var corrupt = File.Exists(FileName);
        if (TryRead(BackupName, out var backup))
        {
            warning = "config.json could not be read; the previous settings were restored from config.json.bak.";
            QuarantineCorrupt();
            return backup!;
        }
        if (corrupt)
        {
            warning = "config.json could not be read and no usable backup was found; defaults are in use.";
            QuarantineCorrupt();
        }
        return new AppConfig();
    }

    private static bool TryRead(string path, out AppConfig? config)
    {
        config = null;
        try
        {
            if (!File.Exists(path)) return false;
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), _options);
            return config is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void QuarantineCorrupt()
    {
        try
        {
            if (File.Exists(FileName)) File.Move(FileName, $"{FileName}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes to a temporary file and then replaces, so an interrupted write leaves the
    /// previous settings intact instead of a truncated file. Returns the error, if any.
    /// </summary>
    public string? Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temporary = FileName + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, _options));
            if (File.Exists(FileName)) File.Replace(temporary, FileName, BackupName, ignoreMetadataErrors: true);
            else File.Move(temporary, FileName);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }
}

public sealed class WindowGeometry
{
    public double Width { get; set; } = 1380;
    public double Height { get; set; } = 880;
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public bool Maximized { get; set; }
}

public sealed class TerminalConfig
{
    public string Address { get; set; } = "";
    public string Port { get; set; } = "5800";
    public string Baud { get; set; } = "115200";
    public string DataBits { get; set; } = "8";
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public string Handshake { get; set; } = "None";
    public int DisplayMode { get; set; }
    public AnsiMode Ansi { get; set; } = AnsiMode.Interpret;
    public TimestampMode TimestampMode { get; set; } = TimestampMode.Off;
    public ReceiveEncoding Encoding { get; set; } = ReceiveEncoding.Latin1;
    public bool Pause { get; set; }
    public bool AutoReconnect { get; set; }
    public bool LocalEcho { get; set; } = true;
    public LineEnding SendEnding { get; set; } = LineEnding.CrLf;
    public List<string> History { get; set; } = new();
    public List<CommandConfig> Commands { get; set; } = new();

    /// <summary>
    /// Pre-v2.0.5 files stored a bool for "append LF". Deserialising it here keeps the
    /// user's 60 command slots working across the upgrade instead of silently resetting.
    /// </summary>
    public bool Timestamp
    {
        get => TimestampMode != TimestampMode.Off;
        set { if (value && TimestampMode == TimestampMode.Off) TimestampMode = TimestampMode.Wall; }
    }
}

public sealed class CommandConfig
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Hex { get; set; }
    public LineEnding Ending { get; set; } = LineEnding.None;

    /// <summary>Legacy "append LF" flag; still read so existing configs migrate.</summary>
    public bool Lf
    {
        get => Ending == LineEnding.Lf;
        set { if (value) Ending = LineEnding.Lf; }
    }
}
