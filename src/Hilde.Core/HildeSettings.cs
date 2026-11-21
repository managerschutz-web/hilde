using System.Text.Json.Serialization;

namespace Hilde.Core;

/// <summary>Inhalt von Config/settings.json. Neue Felder immer mit sinnvollem Standard anlegen.</summary>
public sealed class HildeSettings
{
    public int SchemaVersion { get; set; } = 1;

    public HotkeySettings Hotkey { get; set; } = new();
    public string? MicrophoneId { get; set; }
    /// <summary>"de", "en" oder "auto".</summary>
    public string Language { get; set; } = "de";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TextMode TextMode { get; set; } = TextMode.Clean;
    public InsertionSettings Insertion { get; set; } = new();
    public PillSettings Pill { get; set; } = new();
    public ArchiveSettings Archive { get; set; } = new();
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerformanceTier PerformanceTier { get; set; } = PerformanceTier.Standard;
    public bool UseGpu { get; set; } = true;
    /// <summary>Kurze Diktate mit verkleinertem Whisper-Kontext erkennen.</summary>
    public bool FastShortDictation { get; set; } = true;
    /// <summary>Live-Text in der Pill während des Sprechens (rechnet vor, Endfassung kommt schneller).</summary>
    public bool LivePreview { get; set; } = true;
    /// <summary>Eigener Modellordner (null = %LOCALAPPDATA%\HILDE\Models).</summary>
    public string? ModelsDirectory { get; set; }
    /// <summary>„neuer Absatz“, „neue Zeile“, „Doppelpunkt“.</summary>
    public bool DictationCommands { get; set; } = true;
    public FormatSettings Format { get; set; } = new();
    public ReadAloudSettings ReadAloud { get; set; } = new();
    public int ModelIdleUnloadMinutes { get; set; } = 5;
    public bool Autostart { get; set; }
    /// <summary>"system", "light", "dark".</summary>
    public string Theme { get; set; } = "system";
    public bool SetupCompleted { get; set; }
    public bool LlmSkipped { get; set; }

    public HildeSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<HildeSettings>(json)!;
    }
}

public sealed class HotkeySettings
{
    /// <summary>Virtueller Tastencode der Auslösetaste. Standard: rechte Strg (0xA3).</summary>
    public int VirtualKey { get; set; } = 0xA3;
    public string DisplayName { get; set; } = "Strg rechts";
    /// <summary>Doppeltipp-Fenster für den Freihandmodus.</summary>
    public int DoubleTapMs { get; set; } = 350;
    /// <summary>Kürzer gehalten = Tipp (keine Aufnahme).</summary>
    public int MinHoldMs { get; set; } = 250;
    public bool EscapeCancels { get; set; } = true;
}

public sealed class ReadAloudSettings
{
    /// <summary>Modell-ID der Stimme (Standard: Kerstin, weiblich).</summary>
    public string VoiceId { get; set; } = "tts-de-kerstin-low";
    public double Speed { get; set; } = 1.0;
    /// <summary>Taste zum Vorlesen von markiertem Text (Standard F9).</summary>
    public int VirtualKey { get; set; } = 0x78;
    public string DisplayName { get; set; } = "F9";
}

public sealed class FormatSettings
{
    public bool Amounts { get; set; } = true;
    public bool Dates { get; set; } = true;
    public bool Times { get; set; } = true;
    public bool Codes { get; set; } = true;
    public bool Percent { get; set; } = true;
    public FormatOptions ToOptions() => new(Amounts, Dates, Times, Codes, Percent);
}

public sealed class InsertionSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InsertionMethod Method { get; set; } = InsertionMethod.Auto;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DictationFlow Flow { get; set; } = DictationFlow.Auto;
    public bool RestoreClipboard { get; set; } = true;
    /// <summary>Wartezeit nach dem Einfügebefehl, bevor die Zwischenablage zurückgesetzt wird.</summary>
    public int RestoreDelayMs { get; set; } = 600;
    /// <summary>Für RDP/RemoteApp: längere Wartezeit (Zwischenablage-Umleitung).</summary>
    public int RemoteRestoreDelayMs { get; set; } = 1500;
    /// <summary>Leerzeichen voranstellen, wenn das Ziel bereits Text enthält (nur wenn erkennbar).</summary>
    public bool SmartLeadingSpace { get; set; } = true;
}

public sealed class PillSettings
{
    public bool Visible { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Opacity { get; set; } = 0.92;
    /// <summary>Hintergrundfarbe der Pill (#RRGGBB).</summary>
    public string BackgroundColor { get; set; } = "#161A22";
    /// <summary>Akzentfarbe für Aufnahme und Pegel (#RRGGBB).</summary>
    public string AccentColor { get; set; } = "#34D399";
}

public sealed class ArchiveSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Audio nach erfolgreicher Verarbeitung behalten.</summary>
    public bool KeepAudio { get; set; }
    /// <summary>Behaltenes Audio nach so vielen Tagen löschen (0 = nie).</summary>
    public int AudioRetentionDays { get; set; } = 14;
}
