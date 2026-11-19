using System.Net;
using System.Security.Cryptography;
using Hilde.Core;
using Hilde.Setup;
using Hilde.Storage;
using Serilog;
using Xunit;

namespace Hilde.Tests.Unit;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hilde-t-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch { }
    }
}

public class RepositoryTests
{
    [Fact]
    public async Task Saves_searches_and_deletes()
    {
        using var tmp = new TempDir();
        var repo = new SqliteTranscriptRepository(System.IO.Path.Combine(tmp.Path, "hilde.db"));
        await repo.InitializeAsync();
        await repo.InitializeAsync(); // idempotent

        Assert.Equal(4, (await repo.GetReplacementsAsync()).Count);

        var r = new TranscriptRecord { Title = "Diktat Test", RawText = "äh über Gebühren", CleanText = "Über Gebühren", FinalText = "Über Gebühren", Status = TranscriptStatus.Done };
        await repo.SaveAsync(r);
        var unfinished = new TranscriptRecord { Status = TranscriptStatus.Processing };
        await repo.SaveAsync(unfinished);

        var loaded = await repo.GetAsync(r.Id);
        Assert.NotNull(loaded);
        Assert.Equal("äh über Gebühren", loaded!.RawText);
        Assert.Equal("Über Gebühren", loaded.CleanText);

        Assert.Single(await repo.ListAsync(new TranscriptQuery("ÜBER", null)));
        Assert.Empty(await repo.ListAsync(new TranscriptQuery("100%", null)));
        Assert.Single(await repo.FindUnfinishedAsync());

        await repo.DeleteAsync(r.Id);
        Assert.Null(await repo.GetAsync(r.Id));
    }

    [Fact]
    public async Task Terms_are_unique_case_insensitive()
    {
        using var tmp = new TempDir();
        var repo = new SqliteTranscriptRepository(System.IO.Path.Combine(tmp.Path, "hilde.db"));
        await repo.InitializeAsync();
        int before = (await repo.GetTermsAsync()).Count;
        await repo.SaveTermAsync(new VocabularyTerm { Term = "Gewerbe-Rechtsschutz" });
        await repo.SaveTermAsync(new VocabularyTerm { Term = "gewerbe-rechtsschutz" });
        Assert.Equal(before + 1, (await repo.GetTermsAsync()).Count);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Persists_and_recovers_from_corrupt_file()
    {
        using var tmp = new TempDir();
        var paths = new HildePaths(tmp.Path);
        paths.EnsureCreated();
        var store = new JsonSettingsStore(paths, new LoggerConfiguration().CreateLogger());
        var s = store.Current.Clone();
        s.TextMode = TextMode.Professional;
        store.Save(s);
        Assert.Equal(TextMode.Professional, new JsonSettingsStore(paths, Log.Logger).Current.TextMode);

        File.WriteAllText(paths.SettingsFile, "{ kaputt");
        var recovered = new JsonSettingsStore(paths, Log.Logger);
        Assert.Equal(TextMode.Clean, recovered.Current.TextMode);
    }
}

public class ManifestTests
{
    [Fact]
    public void Shipped_manifest_is_complete()
    {
        var m = ModelManifest.Load(Path.Combine(AppContext.BaseDirectory, "manifest.json"));
        Assert.True(m.ManifestVersion >= 1);
        Assert.Equal(m.Models.Count, m.Models.Select(x => x.Id).Distinct().Count());
        foreach (var tier in new[] { PerformanceTier.Light, PerformanceTier.Standard })
        {
            Assert.NotNull(m.Find(ModelKind.Whisper, tier));
            Assert.NotNull(m.Find(ModelKind.Vad, tier));
            Assert.NotNull(m.Find(ModelKind.Llm, tier));
        }
        Assert.All(m.Models, x =>
        {
            Assert.StartsWith("https://", x.Url);
            Assert.False(string.IsNullOrWhiteSpace(x.License));
            Assert.False(string.IsNullOrWhiteSpace(x.FileName));
        });
        Assert.All(m.Models.Where(x => x.Kind == ModelKind.Llm), x => Assert.False(string.IsNullOrEmpty(x.PromptFormat)));
    }
}

public class ModelManagerTests
{
    private sealed class FakeHandler(byte[] data) : HttpMessageHandler
    {
        public List<long> RangeStarts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            RangeStarts.Add(from);
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data[(int)from..])
            };
            return Task.FromResult(resp);
        }
    }

    private static (ModelManifest, byte[]) Manifest(bool correctHash)
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var manifest = new ModelManifest
        {
            ManifestVersion = 1,
            Models =
            [
                new ModelEntry
                {
                    Id = "vad", Kind = ModelKind.Vad, FileName = "vad.onnx", Url = "https://example.invalid/vad.onnx",
                    SizeBytes = data.Length, Sha256 = correctHash ? hash : new string('0', 64), License = "MIT"
                }
            ]
        };
        return (manifest, data);
    }

    [Fact]
    public async Task Resumes_partial_download_and_verifies()
    {
        using var tmp = new TempDir();
        var paths = new HildePaths(tmp.Path);
        paths.EnsureCreated();
        var (manifest, data) = Manifest(correctHash: true);
        // halben Download vorbereiten
        File.WriteAllBytes(Path.Combine(paths.VadModels, "vad.onnx.part"), data[..150_000]);
        var handler = new FakeHandler(data);
        var mm = new ModelManager(paths, manifest, Log.Logger, handler);

        await mm.DownloadAsync(PerformanceTier.Standard, includeLlm: false, CancellationToken.None);

        Assert.Equal(new long[] { 150_000L }, handler.RangeStarts);
        var path = mm.GetInstalledPath(ModelKind.Vad, PerformanceTier.Light);
        Assert.NotNull(path);
        Assert.Equal(data, File.ReadAllBytes(path!));
        Assert.True(mm.GetStatus(PerformanceTier.Standard).Single().Verified);
        Assert.True(await mm.VerifyAsync("vad", CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_wrong_checksum()
    {
        using var tmp = new TempDir();
        var paths = new HildePaths(tmp.Path);
        paths.EnsureCreated();
        var (manifest, data) = Manifest(correctHash: false);
        var mm = new ModelManager(paths, manifest, Log.Logger, new FakeHandler(data));

        var ex = await Assert.ThrowsAsync<HildeException>(() => mm.DownloadAsync(PerformanceTier.Standard, false, CancellationToken.None));
        Assert.Equal(HildeErrors.ModelChecksum.Id, ex.Error.Id);
        Assert.Null(mm.GetInstalledPath(ModelKind.Vad, PerformanceTier.Standard));
        Assert.False(File.Exists(Path.Combine(paths.VadModels, "vad.onnx.part")));
    }
}
