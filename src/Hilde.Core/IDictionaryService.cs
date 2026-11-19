namespace Hilde.Core;

public interface IDictionaryService
{
    /// <summary>Wendet alle aktiven Ersetzungsregeln an (exakt, mit Wortgrenzen).</summary>
    string Apply(string text);
    /// <summary>Begriffe, die die KI unverändert lassen muss (Ziele der Ersetzungen + Fachbegriffe).</summary>
    IReadOnlyList<string> ProtectedTerms { get; }
    /// <summary>Erkennungshilfe für Whisper (Fachbegriffe/Eigennamen als Prompt).</summary>
    string? InitialPrompt { get; }
    Task ReloadAsync();
}
