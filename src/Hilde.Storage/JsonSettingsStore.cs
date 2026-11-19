using System.Text.Json;
using Hilde.Core;
using Serilog;

namespace Hilde.Storage;

/// <summary>settings.json – atomar geschrieben (temp + replace), defekte Datei wird gesichert.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _lock = new();

    public HildeSettings Current { get; private set; }
    public event EventHandler<HildeSettings>? Changed;

    public JsonSettingsStore(HildePaths paths, ILogger log)
    {
        _path = paths.SettingsFile;
        _log = log.ForContext<JsonSettingsStore>();
        Current = Load();
    }

    private HildeSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<HildeSettings>(File.ReadAllText(_path), Options) ?? new HildeSettings();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "settings.json defekt – Standardwerte, Sicherung angelegt");
            try { File.Copy(_path, _path + ".defekt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
        return new HildeSettings();
    }

    public void Save(HildeSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
            Current = settings.Clone();
        }
        Changed?.Invoke(this, Current);
    }
}
