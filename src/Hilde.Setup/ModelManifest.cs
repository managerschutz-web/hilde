using System.Text.Json;
using System.Text.Json.Serialization;
using Hilde.Core;

namespace Hilde.Setup;

/// <summary>Versioniertes Modellmanifest (models/manifest.json) – Quelle, Größe, SHA-256, Lizenz, Kompatibilität.</summary>
public sealed class ModelManifest
{
    public int ManifestVersion { get; set; }
    public string Updated { get; set; } = "";
    public List<ModelEntry> Models { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ModelManifest Load(string path) =>
        JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Modellmanifest leer.");

    public IEnumerable<ModelEntry> ForTier(PerformanceTier tier) =>
        Models.Where(m => m.Tiers.Count == 0 || m.Tiers.Contains(tier));

    public ModelEntry? Find(ModelKind kind, PerformanceTier tier) =>
        ForTier(tier).FirstOrDefault(m => m.Kind == kind);
}

public sealed class ModelEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ModelKind Kind { get; set; }
    public List<PerformanceTier> Tiers { get; set; } = [];
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public long? SizeBytes { get; set; }
    /// <summary>Pflicht für Freigabe-Builds. null nur in internen Builds (siehe docs/release-checklist.md).</summary>
    public string? Sha256 { get; set; }
    public string License { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    public string LicenseStatus { get; set; } = "zu prüfen";
    public string Architecture { get; set; } = "any";
    public string Compatibility { get; set; } = "";
    public string? PromptFormat { get; set; }
    public bool Optional { get; set; }
    /// <summary>"tar.bz2" = Archiv, das nach der Prüfung in einen Ordner entpackt wird (z. B. Stimmen).</summary>
    public string? Archive { get; set; }
}

/// <summary>Config/models.json – was lokal installiert und geprüft ist.</summary>
public sealed class InstalledModels
{
    public Dictionary<string, InstalledModel> Models { get; set; } = new();
}

public sealed class InstalledModel
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime InstalledUtc { get; set; }
    public int ManifestVersion { get; set; }
    /// <summary>true, wenn gegen eine Prüfsumme aus dem Manifest verifiziert (nicht nur lokal berechnet).</summary>
    public bool VerifiedAgainstManifest { get; set; }
}
