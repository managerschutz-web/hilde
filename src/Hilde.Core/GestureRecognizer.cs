namespace Hilde.Core;

public enum GestureAction
{
    None,
    /// <summary>Taste gedrückt: Aufnahme sofort (vorläufig) starten, damit kein Wortanfang fehlt.</summary>
    StartProvisional,
    /// <summary>Vorläufige Aufnahme verwerfen (kurzer Tipp oder Tastenkombination).</summary>
    DiscardProvisional,
    /// <summary>Push-to-Talk beendet: verarbeiten und einfügen.</summary>
    StopAndProcess,
    /// <summary>Doppeltipp erkannt: Freihandaufnahme starten.</summary>
    StartHandsFree,
    /// <summary>Escape: laufende Aufnahme verwerfen.</summary>
    Cancel
}

/// <summary>
/// Reine Zustandslogik der Auslösetaste (ohne Windows-Abhängigkeit, testbar).
/// Speichert keinerlei Tasteninhalte, nur Zeitpunkte der Auslösetaste.
/// </summary>
public sealed class GestureRecognizer
{
    private enum S { Idle, Held, TapReleased, ComboHeld, HandsFreeHeld, HandsFree, StopHeld }

    private S _state = S.Idle;
    private long _downAt;
    private long _tapUpAt;

    public int MinHoldMs { get; set; } = 250;
    public int DoubleTapMs { get; set; } = 350;

    /// <summary>True, solange eine (auch vorläufige) Aufnahme läuft.</summary>
    public bool IsRecording => _state is S.Held or S.HandsFreeHeld or S.HandsFree;

    public bool IsHandsFree => _state is S.HandsFreeHeld or S.HandsFree;

    public GestureAction TriggerDown(long nowMs)
    {
        switch (_state)
        {
            case S.Idle:
                _downAt = nowMs;
                _state = S.Held;
                return GestureAction.StartProvisional;
            case S.TapReleased:
                if (nowMs - _tapUpAt <= DoubleTapMs)
                {
                    _state = S.HandsFreeHeld;
                    return GestureAction.StartHandsFree;
                }
                _downAt = nowMs;
                _state = S.Held;
                return GestureAction.StartProvisional;
            case S.HandsFree:
                _state = S.StopHeld;
                return GestureAction.StopAndProcess;
            default:
                // Autorepeat (Taste bleibt gedrückt) oder bereits behandelt.
                return GestureAction.None;
        }
    }

    public GestureAction TriggerUp(long nowMs)
    {
        switch (_state)
        {
            case S.Held:
                if (nowMs - _downAt < MinHoldMs)
                {
                    _tapUpAt = nowMs;
                    _state = S.TapReleased;
                    return GestureAction.DiscardProvisional;
                }
                _state = S.Idle;
                return GestureAction.StopAndProcess;
            case S.HandsFreeHeld:
                _state = S.HandsFree;
                return GestureAction.None;
            case S.ComboHeld:
            case S.StopHeld:
                _state = S.Idle;
                return GestureAction.None;
            default:
                return GestureAction.None;
        }
    }

    /// <summary>Andere Taste gedrückt (Inhalt wird nicht ausgewertet).</summary>
    public GestureAction OtherKeyDown(long nowMs)
    {
        if (_state == S.Held)
        {
            // Strg+C o. Ä.: Nutzer verwendet ein Tastenkürzel, kein Diktat.
            _state = S.ComboHeld;
            return GestureAction.DiscardProvisional;
        }
        if (_state == S.TapReleased) _state = S.Idle;
        return GestureAction.None;
    }

    public GestureAction Escape()
    {
        switch (_state)
        {
            case S.Held:
            case S.HandsFreeHeld:
                _state = S.ComboHeld;
                return GestureAction.Cancel;
            case S.HandsFree:
                _state = S.Idle;
                return GestureAction.Cancel;
            default:
                return GestureAction.None;
        }
    }

    /// <summary>Freihandmodus extern (Pill-Klick, Tray) starten/beenden.</summary>
    public GestureAction ExternalToggle()
    {
        if (_state == S.HandsFree) { _state = S.Idle; return GestureAction.StopAndProcess; }
        if (_state == S.Idle || _state == S.TapReleased) { _state = S.HandsFree; return GestureAction.StartHandsFree; }
        return GestureAction.None;
    }

    /// <summary>Laufende Push-to-Talk-Aufnahme in Freihand umwandeln (z. B. beim Pausieren).</summary>
    public void ForceHandsFree()
    {
        if (_state == S.Held) _state = S.HandsFreeHeld;
    }

    /// <summary>Nach Fehlern oder externem Abbruch zurücksetzen.</summary>
    public void Reset() => _state = S.Idle;
}
