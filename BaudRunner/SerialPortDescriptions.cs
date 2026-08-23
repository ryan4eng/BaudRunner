using System.IO.Ports;
using System.Text.RegularExpressions;

namespace BaudRunner;

/// <summary>
/// Friendly names for serial ports. The port list itself is cheap to enumerate; the
/// Windows PnP lookup that turns COM7 into "Silicon Labs CP210x" costs hundreds of
/// milliseconds, so it is cached and refreshed in the background.
/// </summary>
public static class SerialPortDescriptions
{
    private static readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex _comPattern = new(@"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<SerialPortOption> Enumerate()
    {
        string[] names;
        try { names = SerialPort.GetPortNames(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { names = Array.Empty<string>(); }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new SerialPortOption(name, _descriptions.TryGetValue(name, out var description) ? $"{name}  <{description}>" : name))
            .ToList();
    }

    /// <summary>Folds a fresh query into the cache. Returns true when anything changed.</summary>
    public static bool Merge(Dictionary<string, string> fresh)
    {
        var changed = false;
        foreach (var (port, description) in fresh)
        {
            if (_descriptions.TryGetValue(port, out var existing) && string.Equals(existing, description, StringComparison.Ordinal)) continue;
            _descriptions[port] = description;
            changed = true;
        }

        // Drop descriptions for ports that are gone, so plugging a different adapter
        // into the same COM number does not keep showing the old device's name.
        var present = fresh.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _descriptions.Keys.Where(key => !present.Contains(key)).ToList())
        {
            _descriptions.Remove(stale);
            changed = true;
        }
        return changed;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static Dictionary<string, string> QueryWindows()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
#if WMI
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var device in searcher.Get())
            {
                // Per device: one badly-named adapter used to throw out of the loop and
                // discard every description that had already been collected.
                try
                {
                    var name = device["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var match = _comPattern.Match(name);
                    if (!match.Success) continue;
                    var cut = name.LastIndexOf(" (", StringComparison.Ordinal);
                    descriptions[match.Groups[1].Value] = cut > 0 ? name[..cut] : name;
                }
                catch (Exception) { }
                finally { (device as IDisposable)?.Dispose(); }
            }
        }
        catch (Exception) { }
#endif
        return descriptions;
    }
}
