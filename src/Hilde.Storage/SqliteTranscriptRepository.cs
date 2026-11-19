using System.Globalization;
using Hilde.Core;
using Microsoft.Data.Sqlite;

namespace Hilde.Storage;

/// <summary>SQLite-Archiv: Metadaten + Texte. Audio liegt separat unter Recordings/.</summary>
public sealed class SqliteTranscriptRepository : ITranscriptRepository
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteTranscriptRepository(string databaseFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync().ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        return c;
    }

    public async Task InitializeAsync()
    {
        await using var c = await OpenAsync().ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS transcripts (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT '',
                language TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL,
                mode TEXT NOT NULL,
                raw_text TEXT NOT NULL DEFAULT '',
                clean_text TEXT NOT NULL DEFAULT '',
                final_text TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                audio_path TEXT,
                target_app TEXT,
                insertion_method TEXT,
                error_id TEXT,
                fallback_reason TEXT,
                speakers_json TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_transcripts_created ON transcripts(created_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_transcripts_status ON transcripts(status);
            CREATE TABLE IF NOT EXISTS replacements (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pattern TEXT NOT NULL,
                replacement TEXT NOT NULL,
                whole_word INTEGER NOT NULL DEFAULT 1,
                case_sensitive INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS terms (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                term TEXT NOT NULL UNIQUE COLLATE NOCASE
            );
            -- Phase 3: Auftragswarteschlange (Dateien/Meetings), übersteht Neustarts.
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                status TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                progress REAL NOT NULL DEFAULT 0,
                error_id TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        await using var v = c.CreateCommand();
        v.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        var current = await v.ExecuteScalarAsync().ConfigureAwait(false) as string;
        if (current == null)
        {
            v.CommandText = "INSERT INTO meta(key,value) VALUES('schema_version',$v)";
            v.Parameters.AddWithValue("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            await v.ExecuteNonQueryAsync().ConfigureAwait(false);
            await SeedAsync(c).ConfigureAwait(false);
        }
    }

    /// <summary>Beispielregeln aus dem Pflichtenheft beim allerersten Start.</summary>
    private static async Task SeedAsync(SqliteConnection c)
    {
        var seed = new (string P, string R, bool Cs)[]
        {
            ("Marta und Prahl", "MARTENS & PRAHL", false),
            ("Martens und Prahl", "MARTENS & PRAHL", false),
            ("EVB", "eVB", true),
            ("HSN TSN", "HSN/TSN", false),
        };
        int order = 0;
        foreach (var (p, r, cs) in seed)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO replacements(pattern,replacement,whole_word,case_sensitive,enabled,sort_order) VALUES($p,$r,1,$cs,1,$o)";
            cmd.Parameters.AddWithValue("$p", p);
            cmd.Parameters.AddWithValue("$r", r);
            cmd.Parameters.AddWithValue("$cs", cs ? 1 : 0);
            cmd.Parameters.AddWithValue("$o", order++);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        foreach (var term in new[] { "MARTENS & PRAHL", "eVB", "HSN/TSN" })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO terms(term) VALUES($t)";
            cmd.Parameters.AddWithValue("$t", term);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(TranscriptRecord r)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync().ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO transcripts(id,kind,title,created_utc,updated_utc,source,language,status,mode,raw_text,clean_text,
                    final_text,duration_ms,audio_path,target_app,insertion_method,error_id,fallback_reason,speakers_json)
                VALUES($id,$kind,$title,$created,$updated,$source,$lang,$status,$mode,$raw,$clean,$final,$dur,$audio,$app,$ins,$err,$fb,$spk)
                ON CONFLICT(id) DO UPDATE SET kind=$kind,title=$title,updated_utc=$updated,source=$source,language=$lang,
                    status=$status,mode=$mode,raw_text=$raw,clean_text=$clean,final_text=$final,duration_ms=$dur,
                    audio_path=$audio,target_app=$app,insertion_method=$ins,error_id=$err,fallback_reason=$fb,speakers_json=$spk;
                """;
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$kind", r.Kind.ToString());
            cmd.Parameters.AddWithValue("$title", r.Title);
            cmd.Parameters.AddWithValue("$created", r.CreatedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$updated", r.UpdatedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$source", r.Source);
            cmd.Parameters.AddWithValue("$lang", r.Language);
            cmd.Parameters.AddWithValue("$status", r.Status);
            cmd.Parameters.AddWithValue("$mode", r.Mode.ToString());
            cmd.Parameters.AddWithValue("$raw", r.RawText);
            cmd.Parameters.AddWithValue("$clean", r.CleanText);
            cmd.Parameters.AddWithValue("$final", r.FinalText);
            cmd.Parameters.AddWithValue("$dur", r.DurationMs);
            cmd.Parameters.AddWithValue("$audio", (object?)r.AudioPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$app", (object?)r.TargetApp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ins", (object?)r.InsertionMethod ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)r.ErrorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fb", (object?)r.FallbackReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$spk", (object?)r.SpeakersJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private const string SelectColumns = "id,kind,title,created_utc,updated_utc,source,language,status,mode,raw_text,clean_text,final_text,duration_ms,audio_path,target_app,insertion_method,error_id,fallback_reason,speakers_json";

    private static TranscriptRecord Map(SqliteDataReader rd) => new()
    {
        Id = rd.GetString(0),
        Kind = Enum.TryParse<TranscriptKind>(rd.GetString(1), out var k) ? k : TranscriptKind.Dictation,
        Title = rd.GetString(2),
        CreatedUtc = DateTime.Parse(rd.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc = DateTime.Parse(rd.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Source = rd.GetString(5),
        Language = rd.GetString(6),
        Status = rd.GetString(7),
        Mode = Enum.TryParse<TextMode>(rd.GetString(8), out var m) ? m : TextMode.Clean,
        RawText = rd.GetString(9),
        CleanText = rd.GetString(10),
        FinalText = rd.GetString(11),
        DurationMs = rd.GetInt64(12),
        AudioPath = rd.IsDBNull(13) ? null : rd.GetString(13),
        TargetApp = rd.IsDBNull(14) ? null : rd.GetString(14),
        InsertionMethod = rd.IsDBNull(15) ? null : rd.GetString(15),
        ErrorId = rd.IsDBNull(16) ? null : rd.GetString(16),
        FallbackReason = rd.IsDBNull(17) ? null : rd.GetString(17),
        SpeakersJson = rd.IsDBNull(18) ? null : rd.GetString(18),
    };

    public async Task<TranscriptRecord?> GetAsync(string id)
    {
        await using var c = await OpenAsync().ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM transcripts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return await rd.ReadAsync().ConfigureAwait(false) ? Map(rd) : null;
    }

    public async Task<IReadOnlyList<TranscriptRecord>> ListAsync(TranscriptQuery q)
    {
        await using var c = await OpenAsync().ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (q.Kind is { } kind)
        {
            where.Add("kind=$kind");
            cmd.Parameters.AddWithValue("$kind", kind.ToString());
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // hilde_lower: Unicode-fähiges Kleinschreiben (Umlaute), da SQLite-LOWER nur ASCII kennt.
            c.CreateFunction("hilde_lower", (string? s) => s?.ToLowerInvariant());
            where.Add("(hilde_lower(title) LIKE $q ESCAPE '\\' OR hilde_lower(final_text) LIKE $q ESCAPE '\\' OR hilde_lower(raw_text) LIKE $q ESCAPE '\\')");
            var escaped = q.Search.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$q", "%" + escaped + "%");
        }
        cmd.CommandText = $"SELECT {SelectColumns} FROM transcripts"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY created_utc DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", q.Limit);
        cmd.Parameters.AddWithValue("$offset", q.Offset);
        var list = new List<TranscriptRecord>();
        await using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rd.ReadAsync().ConfigureAwait(false)) list.Add(Map(rd));
        return list;
    }

    public async Task DeleteAsync(string id)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync().ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM transcripts WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<TranscriptRecord>> FindUnfinishedAsync()
    {
        await using var c = await OpenAsync().ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM transcripts WHERE status IN ('recording','processing')";
        var list = new List<TranscriptRecord>();
        await using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rd.ReadAsync().ConfigureAwait(false)) list.Add(Map(rd));
        return list;
    }

    public async Task<IReadOnlyList<ReplacementRule>> GetReplacementsAsync()
    {
        await using var c = await OpenAsync().ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,pattern,replacement,whole_word,case_sensitive,enabled,sort_order FROM replacements ORDER BY sort_order, id";
        var list = new List<ReplacementRule>();
        await using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rd.ReadAsync().ConfigureAwait(false))
        {
            list.Add(new ReplacementRule
            {
                Id = rd.GetInt64(0), Pattern = rd.GetString(1), Replacement = rd.GetString(2),
                WholeWord = rd.GetInt64(3) != 0, CaseSensitive = rd.GetInt64(4) != 0,
                Enabled = rd.GetInt64(5) != 0, SortOrder = (int)rd.GetInt64(6)
            });
        }
        return list;
    }

    public async Task<ReplacementRule> SaveReplacementAsync(ReplacementRule rule)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync().ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            if (rule.Id == 0)
            {
                cmd.CommandText = "INSERT INTO replacements(pattern,replacement,whole_word,case_sensitive,enabled,sort_order) VALUES($p,$r,$w,$c,$e,$o); SELECT last_insert_rowid();";
            }
            else
            {
                cmd.CommandText = "UPDATE replacements SET pattern=$p,replacement=$r,whole_word=$w,case_sensitive=$c,enabled=$e,sort_order=$o WHERE id=$id; SELECT $id;";
                cmd.Parameters.AddWithValue("$id", rule.Id);
            }
            cmd.Parameters.AddWithValue("$p", rule.Pattern);
            cmd.Parameters.AddWithValue("$r", rule.Replacement);
            cmd.Parameters.AddWithValue("$w", rule.WholeWord ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", rule.CaseSensitive ? 1 : 0);
            cmd.Parameters.AddWithValue("$e", rule.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$o", rule.SortOrder);
            rule.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
            return rule;
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteReplacementAsync(long id)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync().ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM replacements WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<VocabularyTerm>> GetTermsAsync()
    {
        await using var c = await OpenAsync().ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, term FROM terms ORDER BY term COLLATE NOCASE";
        var list = new List<VocabularyTerm>();
        await using var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rd.ReadAsync().ConfigureAwait(false))
            list.Add(new VocabularyTerm { Id = rd.GetInt64(0), Term = rd.GetString(1) });
        return list;
    }

    public async Task<VocabularyTerm> SaveTermAsync(VocabularyTerm term)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync().ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            if (term.Id == 0)
                cmd.CommandText = "INSERT INTO terms(term) VALUES($t) ON CONFLICT(term) DO UPDATE SET term=$t; SELECT id FROM terms WHERE term=$t COLLATE NOCASE;";
            else
            {
                cmd.CommandText = "UPDATE terms SET term=$t WHERE id=$id; SELECT $id;";
                cmd.Parameters.AddWithValue("$id", term.Id);
            }
            cmd.Parameters.AddWithValue("$t", term.Term.Trim());
            term.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
            return term;
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteTermAsync(long id)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync().ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM terms WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }
}
