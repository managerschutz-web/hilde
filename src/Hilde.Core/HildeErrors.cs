namespace Hilde.Core;

/// <summary>Fehlerkatalog: interne ID + verständliche deutsche Meldung (Pflichtenheft Kap. 8).</summary>
public static class HildeErrors
{
    public sealed record Entry(string Id, string Message, string? Hint = null);

    public static readonly Entry NoMicrophone = new("H-AUD-001",
        "Es wurde kein Mikrofon gefunden.", "Bitte ein Mikrofon anschließen und in den Einstellungen auswählen.");
    public static readonly Entry MicrophoneLost = new("H-AUD-002",
        "Das Mikrofon ist während der Aufnahme weggefallen.", "Das bisher Gesprochene wurde gesichert.");
    public static readonly Entry MicrophoneBlocked = new("H-AUD-003",
        "Windows erlaubt HILDE keinen Mikrofonzugriff.",
        "Einstellungen › Datenschutz und Sicherheit › Mikrofon › Desktop-Apps den Zugriff erlauben.");
    public static readonly Entry TooShort = new("H-AUD-010", "Die Aufnahme war zu kurz.");
    public static readonly Entry NoSpeech = new("H-AUD-011", "Es wurde keine Sprache erkannt.");

    public static readonly Entry WhisperModelMissing = new("H-MOD-001",
        "Das Spracherkennungsmodell fehlt.", "Bitte die Einrichtung öffnen und die Modelle laden.");
    public static readonly Entry LlmModelMissing = new("H-MOD-002",
        "Das Modell für die Textbereinigung fehlt. Der Text wurde regelbasiert bereinigt.");
    public static readonly Entry ModelChecksum = new("H-MOD-003",
        "Die Prüfsumme eines Modells stimmt nicht. Die Datei wurde verworfen.");
    public static readonly Entry DownloadFailed = new("H-MOD-004",
        "Der Download ist fehlgeschlagen.", "Er kann später fortgesetzt werden.");
    public static readonly Entry DiskFull = new("H-MOD-005",
        "Nicht genug freier Speicherplatz.");
    public static readonly Entry VoiceMissing = new("H-MOD-006",
        "Die Vorlesestimme ist noch nicht installiert.", "Einstellungen › Vorlesen › Stimme laden.");
    public static readonly Entry NothingSelected = new("H-INS-005",
        "Es ist kein Text markiert.", "Text markieren und erneut drücken – oder „Zwischenablage vorlesen“ wählen.");

    public static readonly Entry SpeechEngineFailed = new("H-ENG-001",
        "Die Spracherkennung ist abgestürzt und wurde neu gestartet. Die Aufnahme ist gesichert.",
        "Im Archiv »Erneut verarbeiten« wählen.");
    public static readonly Entry SpeechTimeout = new("H-ENG-002",
        "Die Spracherkennung hat zu lange gebraucht. Die Aufnahme ist gesichert.");
    public static readonly Entry LlmFailed = new("H-ENG-003",
        "Die KI-Bereinigung ist ausgefallen. Der erkannte Text wurde regelbasiert bereinigt.");
    public static readonly Entry LlmRejected = new("H-ENG-004",
        "Die KI-Bereinigung hätte Inhalte verändert und wurde verworfen.");
    public static readonly Entry EngineStartFailed = new("H-ENG-005",
        "Eine Verarbeitungskomponente konnte nicht gestartet werden.");

    public static readonly Entry TargetGone = new("H-INS-001",
        "Das Zielfenster ist nicht mehr da. Der Text steht zum Kopieren bereit.");
    public static readonly Entry ClipboardBlocked = new("H-INS-002",
        "Die Zwischenablage ist gerade blockiert. Der Text steht zum Kopieren bereit.");
    public static readonly Entry InsertFailed = new("H-INS-003",
        "Der Text konnte nicht eingefügt werden. Er steht zum Kopieren bereit.");
    public static readonly Entry ElevatedTarget = new("H-INS-004",
        "Das Zielprogramm läuft mit Administratorrechten. Windows verhindert das Einfügen.",
        "Der Text steht zum Kopieren bereit.");

    public static readonly Entry StorageFailed = new("H-DAT-001",
        "Das Diktat konnte nicht im Archiv gespeichert werden.");
    public static readonly Entry Unexpected = new("H-GEN-001", "Ein unerwarteter Fehler ist aufgetreten.");

    private static readonly Dictionary<string, Entry> ById = typeof(HildeErrors)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(Entry))
        .Select(f => (Entry)f.GetValue(null)!)
        .ToDictionary(e => e.Id);

    public static Entry Get(string? id) => id != null && ById.TryGetValue(id, out var e) ? e : Unexpected;

    public static IReadOnlyCollection<Entry> All => ById.Values;
}

public sealed class HildeException : Exception
{
    public HildeErrors.Entry Error { get; }
    public HildeException(HildeErrors.Entry error, Exception? inner = null, string? detail = null)
        : base(detail is null ? $"{error.Id}: {error.Message}" : $"{error.Id}: {error.Message} ({detail})", inner)
    {
        Error = error;
    }
}
