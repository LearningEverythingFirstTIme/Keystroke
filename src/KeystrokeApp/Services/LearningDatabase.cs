using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KeystrokeApp.Services;

/// <summary>
/// Central SQLite-backed storage for learning events.
/// Replaces the dual-JSONL (tracking.jsonl + completions.jsonl) architecture
/// with a single indexed database in WAL mode.
///
/// Thread safety:
///   - Writes are serialized via _writeLock (single kept-open connection).
///   - Reads use pooled connections under WAL — never block the writer.
///   - WriteVersion increments on every mutation for cheap change detection.
/// </summary>
public sealed class LearningDatabase : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private SqliteConnection? _writeConnection;
    private long _writeVersion;
    private bool _disposed;

    public LearningDatabase(string? dbPath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");
        _dbPath = dbPath ?? Path.Combine(appData, "learning.db");
        _connectionString = $"Data Source={_dbPath};Cache=Shared";
    }

    public string DbPath => _dbPath;

    /// <summary>
    /// Monotonically increasing counter, bumped on every INSERT/DELETE.
    /// Consumers compare against their last-seen value to detect changes
    /// without any DB round-trip.
    /// </summary>
    public long WriteVersion => Interlocked.Read(ref _writeVersion);

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            OpenWriteConnection();

            using var cmd = _writeConnection!.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 3000;

                CREATE TABLE IF NOT EXISTS events (
                    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id              TEXT    NOT NULL,
                    session_id            TEXT    NOT NULL DEFAULT '',
                    suggestion_id         TEXT    NOT NULL DEFAULT '',
                    request_id            INTEGER NOT NULL DEFAULT 0,
                    timestamp_utc         TEXT    NOT NULL,
                    timestamp_ticks       INTEGER NOT NULL,
                    event_type            TEXT    NOT NULL DEFAULT '',
                    source_origin         TEXT    NOT NULL DEFAULT 'tracking',
                    process_name          TEXT    NOT NULL DEFAULT '',
                    category              TEXT    NOT NULL DEFAULT '',
                    safe_context_label    TEXT    NOT NULL DEFAULT '',
                    process_key           TEXT    NOT NULL DEFAULT '',
                    window_key            TEXT    NOT NULL DEFAULT '',
                    subcontext_key        TEXT    NOT NULL DEFAULT '',
                    process_label         TEXT    NOT NULL DEFAULT '',
                    window_label          TEXT    NOT NULL DEFAULT '',
                    subcontext_label      TEXT    NOT NULL DEFAULT '',
                    typed_prefix          TEXT    NOT NULL DEFAULT '',
                    shown_completion      TEXT    NOT NULL DEFAULT '',
                    accepted_text         TEXT    NOT NULL DEFAULT '',
                    user_written_text     TEXT    NOT NULL DEFAULT '',
                    commit_reason         TEXT    NOT NULL DEFAULT '',
                    latency_ms            INTEGER NOT NULL DEFAULT -1,
                    cycle_depth           INTEGER NOT NULL DEFAULT 0,
                    edited_after_accept   INTEGER NOT NULL DEFAULT 0,
                    untouched_for_ms      INTEGER NOT NULL DEFAULT 0,
                    quality_score         REAL    NOT NULL DEFAULT 0.5,
                    source_weight         REAL    NOT NULL DEFAULT 0.5,
                    confidence            REAL    NOT NULL DEFAULT 0.5,
                    deleted_suffix        TEXT    NOT NULL DEFAULT '',
                    corrected_text        TEXT    NOT NULL DEFAULT '',
                    correction_backspaces INTEGER NOT NULL DEFAULT 0,
                    correction_type       TEXT    NOT NULL DEFAULT ''
                );

                CREATE INDEX IF NOT EXISTS idx_events_timestamp
                    ON events (timestamp_ticks);
                CREATE INDEX IF NOT EXISTS idx_events_subcontext
                    ON events (subcontext_key);
                CREATE INDEX IF NOT EXISTS idx_events_event_type
                    ON events (event_type);
                CREATE INDEX IF NOT EXISTS idx_events_cat_time
                    ON events (category, timestamp_ticks);
                CREATE INDEX IF NOT EXISTS idx_events_correction
                    ON events (correction_backspaces)
                    WHERE correction_backspaces > 0;

                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO schema_version (version)
                    SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] EnsureCreated failed: {ex.Message}");
            TryRecoverCorrupted();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_writeLock)
        {
            _writeConnection?.Close();
            _writeConnection?.Dispose();
            _writeConnection = null;
        }
    }

    // ── Write operations ─────────────────────────────────────────────────────

    public void InsertEvent(LearningEventRecord record)
    {
        // Writes are allowed to throw — callers (LearningEventService,
        // CompletionFeedbackService) wrap Append in a guarded try so user
        // typing is never interrupted, and LearningEventService in particular
        // surfaces repeated failures to ReliabilityTraceService. Swallowing
        // here would strip the failure signal before it can reach anyone.
        lock (_writeLock)
        {
            EnsureWriteConnection();
            using var cmd = _writeConnection!.CreateCommand();
            cmd.CommandText = InsertSql;
            BindEventParameters(cmd, record, "tracking");
            cmd.ExecuteNonQuery();
            Interlocked.Increment(ref _writeVersion);
        }
    }

    public void DeleteBySubcontextKey(string subcontextKey)
    {
        if (string.IsNullOrWhiteSpace(subcontextKey)) return;

        try
        {
            lock (_writeLock)
            {
                EnsureWriteConnection();
                using var cmd = _writeConnection!.CreateCommand();
                cmd.CommandText = "DELETE FROM events WHERE subcontext_key = @key";
                cmd.Parameters.AddWithValue("@key", subcontextKey);
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    Interlocked.Increment(ref _writeVersion);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] DeleteBySubcontextKey failed: {ex.Message}");
        }
    }

    public void DeleteAssistBySubcontextKey(string subcontextKey)
    {
        if (string.IsNullOrWhiteSpace(subcontextKey)) return;

        try
        {
            lock (_writeLock)
            {
                EnsureWriteConnection();
                using var cmd = _writeConnection!.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM events
                    WHERE subcontext_key = @key
                      AND event_type IN (
                          'suggestion_full_accept',
                          'suggestion_partial_accept',
                          'accepted_text_untouched'
                      )
                    """;
                cmd.Parameters.AddWithValue("@key", subcontextKey);
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    Interlocked.Increment(ref _writeVersion);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] DeleteAssistBySubcontextKey failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the most recent maxRows events. Never deletes events younger than 7 days.
    /// </summary>
    public void Prune(int maxRows = 5000)
    {
        try
        {
            lock (_writeLock)
            {
                EnsureWriteConnection();
                var floor = DateTime.UtcNow.AddDays(-7).Ticks;

                using var cmd = _writeConnection!.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM events
                    WHERE timestamp_ticks < @floor
                      AND id NOT IN (
                          SELECT id FROM events
                          ORDER BY timestamp_ticks DESC
                          LIMIT @maxRows
                      )
                    """;
                cmd.Parameters.AddWithValue("@floor", floor);
                cmd.Parameters.AddWithValue("@maxRows", maxRows);
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                {
                    Interlocked.Increment(ref _writeVersion);
                    Debug.WriteLine($"[LearningDB] Pruned {deleted} old events");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] Prune failed: {ex.Message}");
        }
    }

    // ── Read operations ──────────────────────────────────────────────────────

    public List<LearningEventRecord> GetAllEvents()
    {
        return QueryEvents("SELECT * FROM events ORDER BY timestamp_ticks DESC");
    }

    public List<LearningEventRecord> GetEventsAfter(DateTime watermark)
    {
        return QueryEvents(
            "SELECT * FROM events WHERE timestamp_ticks > @ticks ORDER BY timestamp_ticks ASC",
            ("@ticks", watermark.Ticks));
    }

    public List<LearningEventRecord> GetCorrectionEvents(int limit = 500)
    {
        return QueryEvents(
            """
            SELECT * FROM events
            WHERE correction_backspaces > 0
              AND correction_type NOT IN ('', 'none', 'minor')
            ORDER BY timestamp_ticks DESC
            LIMIT @limit
            """,
            ("@limit", limit));
    }

    public List<LearningEventRecord> GetAcceptDismissEvents()
    {
        return QueryEvents(
            """
            SELECT * FROM events
            WHERE event_type IN (
                'suggestion_full_accept',
                'suggestion_partial_accept',
                'accepted_text_untouched',
                'suggestion_dismiss',
                'suggestion_typed_past'
            )
            ORDER BY timestamp_ticks DESC
            """);
    }

    public int GetEventCount()
    {
        try
        {
            using var conn = OpenReadConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] GetEventCount failed: {ex.Message}");
            return 0;
        }
    }

    public bool HasData()
    {
        try
        {
            using var conn = OpenReadConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM events LIMIT 1)";
            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }
        catch
        {
            return false;
        }
    }

    // ── Migration ────────────────────────────────────────────────────────────

    public bool NeedsMigration()
    {
        if (HasData()) return false;

        var appData = Path.GetDirectoryName(_dbPath)!;
        return File.Exists(Path.Combine(appData, "tracking.jsonl"))
            || File.Exists(Path.Combine(appData, "completions.jsonl"))
            || File.Exists(Path.Combine(appData, "learning-events.v2.jsonl"));
    }

    public void ImportFromJsonl(
        string? trackingPath = null,
        string? completionsPath = null,
        string? legacyEventPath = null,
        ContextFingerprintService? fingerprints = null)
    {
        var appData = Path.GetDirectoryName(_dbPath)!;
        trackingPath ??= Path.Combine(appData, "tracking.jsonl");
        completionsPath ??= Path.Combine(appData, "completions.jsonl");
        legacyEventPath ??= Path.Combine(appData, "learning-events.v2.jsonl");
        fingerprints ??= new ContextFingerprintService();

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        int imported = 0;

        try
        {
            lock (_writeLock)
            {
                EnsureWriteConnection();
                using var transaction = _writeConnection!.BeginTransaction();

                // Build dedup index from tracking events (same logic as LearningRepository)
                var dualWriteIndex = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);

                // 1. Import tracking.jsonl
                imported += ImportEventFile(trackingPath, "tracking", options, dualWriteIndex);

                // 2. Import learning-events.v2.jsonl
                imported += ImportEventFile(legacyEventPath, "tracking", options, dualWriteIndex);

                // 3. Import completions.jsonl (legacy format, with dedup)
                imported += ImportLegacyFile(completionsPath, options, dualWriteIndex, fingerprints);

                transaction.Commit();
                Interlocked.Increment(ref _writeVersion);
            }

            // Rename originals to .bak (safety net)
            RenameToBackup(trackingPath);
            RenameToBackup(completionsPath);
            RenameToBackup(legacyEventPath);

            Debug.WriteLine($"[LearningDB] Migration complete: {imported} events imported");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] Migration failed: {ex.Message}");
            // Transaction auto-rolls back on dispose if not committed
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private int ImportEventFile(
        string path,
        string sourceOrigin,
        JsonSerializerOptions options,
        Dictionary<string, List<DateTime>> dualWriteIndex)
    {
        if (!File.Exists(path)) return 0;

        int count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var record = JsonSerializer.Deserialize<LearningEventRecord>(line, options);
                if (record == null) continue;

                using var cmd = _writeConnection!.CreateCommand();
                cmd.CommandText = InsertSql;
                BindEventParameters(cmd, record, sourceOrigin);
                cmd.ExecuteNonQuery();
                count++;

                // Build dedup signature for legacy filtering
                AddDualWriteSignature(dualWriteIndex, record);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LearningDB] Skipping malformed line in {path}: {ex.Message}");
            }
        }
        return count;
    }

    private int ImportLegacyFile(
        string path,
        JsonSerializerOptions options,
        Dictionary<string, List<DateTime>> dualWriteIndex,
        ContextFingerprintService fingerprints)
    {
        if (!File.Exists(path)) return 0;

        int count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var entry = JsonSerializer.Deserialize<LegacyCompletionRecord>(line, options);
                if (entry == null || string.IsNullOrWhiteSpace(entry.Completion))
                    continue;

                // Map legacy action to event_type
                var eventType = entry.Action switch
                {
                    "accepted" => "suggestion_full_accept",
                    "dismissed" => "suggestion_dismiss",
                    _ => "" // ignored entries skipped
                };
                if (string.IsNullOrEmpty(eventType))
                    continue;

                var fp = fingerprints.Create(entry.App, entry.Window);

                // Dedup: skip if a tracking event covers this
                if (IsCoveredByEvent(entry, fp, eventType, dualWriteIndex))
                    continue;

                var record = new LearningEventRecord
                {
                    EventId = Guid.NewGuid().ToString("n"),
                    TimestampUtc = entry.Timestamp,
                    EventType = eventType,
                    ProcessName = entry.App ?? "",
                    Category = string.IsNullOrWhiteSpace(entry.Category) ? fp.Category : entry.Category,
                    SafeContextLabel = fp.SafeContextLabel,
                    ContextKeys = new LearningEventContextKeys
                    {
                        ProcessKey = fp.ProcessKey,
                        WindowKey = fp.WindowKey,
                        SubcontextKey = fp.SubcontextKey,
                        ProcessLabel = fp.ProcessLabel,
                        WindowLabel = fp.WindowLabel,
                        SubcontextLabel = fp.SubcontextLabel
                    },
                    TypedPrefix = entry.Prefix ?? "",
                    ShownCompletion = entry.Completion ?? "",
                    AcceptedText = entry.Action == "accepted" ? (entry.Completion ?? "") : "",
                    LatencyMs = entry.LatencyMs,
                    CycleDepth = entry.CycleDepth,
                    EditedAfterAccept = entry.EditedAfter,
                    QualityScore = entry.QualityScore <= 0 ? 0.5f : entry.QualityScore,
                    SourceWeight = entry.Action == "accepted"
                        ? (entry.EditedAfter ? 0.35f : 0.5f)
                        : 1.0f
                };

                using var cmd = _writeConnection!.CreateCommand();
                cmd.CommandText = InsertSql;
                BindEventParameters(cmd, record, "legacy");
                cmd.ExecuteNonQuery();
                count++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LearningDB] Skipping malformed legacy line: {ex.Message}");
            }
        }
        return count;
    }

    // ── Dual-write dedup (ported from LearningRepository) ────────────────────

    private static void AddDualWriteSignature(
        Dictionary<string, List<DateTime>> index,
        LearningEventRecord record)
    {
        var eventType = record.EventType;
        if (eventType is not (
            "suggestion_full_accept" or "accepted_text_untouched" or
            "suggestion_dismiss" or "suggestion_typed_past"))
            return;

        var key = BuildDualWriteKey(
            eventType is "suggestion_dismiss" or "suggestion_typed_past",
            record.ProcessName ?? "",
            record.Category ?? "",
            record.TypedPrefix ?? "",
            record.AcceptedText ?? record.ShownCompletion ?? "");

        if (!index.TryGetValue(key, out var times))
        {
            times = [];
            index[key] = times;
        }
        times.Add(record.TimestampUtc);
    }

    private static bool IsCoveredByEvent(
        LegacyCompletionRecord entry,
        ContextFingerprint fp,
        string eventType,
        Dictionary<string, List<DateTime>> index)
    {
        bool isNeg = eventType == "suggestion_dismiss";
        var key = BuildDualWriteKey(
            isNeg,
            entry.App ?? "",
            string.IsNullOrWhiteSpace(entry.Category) ? fp.Category : entry.Category,
            entry.Prefix ?? "",
            entry.Completion ?? "");

        if (!index.TryGetValue(key, out var timestamps))
            return false;

        return timestamps.Any(ts => Math.Abs((ts - entry.Timestamp).TotalSeconds) <= 5);
    }

    private static string BuildDualWriteKey(
        bool isNegative, string processName, string category,
        string prefix, string completion)
    {
        static string Normalize(string v) =>
            v.Trim().Replace("\r", "").Replace("\n", " ").ToLowerInvariant();

        return string.Join("|",
            isNegative ? "neg" : "pos",
            Normalize(processName),
            Normalize(category),
            Normalize(prefix),
            Normalize(completion));
    }

    // ── SQL helpers ──────────────────────────────────────────────────────────

    private const string InsertSql = """
        INSERT INTO events (
            event_id, session_id, suggestion_id, request_id,
            timestamp_utc, timestamp_ticks, event_type, source_origin,
            process_name, category, safe_context_label,
            process_key, window_key, subcontext_key,
            process_label, window_label, subcontext_label,
            typed_prefix, shown_completion, accepted_text,
            user_written_text, commit_reason,
            latency_ms, cycle_depth, edited_after_accept, untouched_for_ms,
            quality_score, source_weight, confidence,
            deleted_suffix, corrected_text, correction_backspaces, correction_type
        ) VALUES (
            @event_id, @session_id, @suggestion_id, @request_id,
            @timestamp_utc, @timestamp_ticks, @event_type, @source_origin,
            @process_name, @category, @safe_context_label,
            @process_key, @window_key, @subcontext_key,
            @process_label, @window_label, @subcontext_label,
            @typed_prefix, @shown_completion, @accepted_text,
            @user_written_text, @commit_reason,
            @latency_ms, @cycle_depth, @edited_after_accept, @untouched_for_ms,
            @quality_score, @source_weight, @confidence,
            @deleted_suffix, @corrected_text, @correction_backspaces, @correction_type
        )
        """;

    private static void BindEventParameters(SqliteCommand cmd, LearningEventRecord r, string sourceOrigin)
    {
        cmd.Parameters.AddWithValue("@event_id", r.EventId ?? Guid.NewGuid().ToString("n"));
        cmd.Parameters.AddWithValue("@session_id", r.SessionId ?? "");
        cmd.Parameters.AddWithValue("@suggestion_id", r.SuggestionId ?? "");
        cmd.Parameters.AddWithValue("@request_id", r.RequestId);
        cmd.Parameters.AddWithValue("@timestamp_utc", r.TimestampUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@timestamp_ticks", r.TimestampUtc.Ticks);
        cmd.Parameters.AddWithValue("@event_type", r.EventType ?? "");
        cmd.Parameters.AddWithValue("@source_origin", sourceOrigin);
        cmd.Parameters.AddWithValue("@process_name", r.ProcessName ?? "");
        cmd.Parameters.AddWithValue("@category", r.Category ?? "");
        cmd.Parameters.AddWithValue("@safe_context_label", r.SafeContextLabel ?? "");
        cmd.Parameters.AddWithValue("@process_key", r.ContextKeys?.ProcessKey ?? "");
        cmd.Parameters.AddWithValue("@window_key", r.ContextKeys?.WindowKey ?? "");
        cmd.Parameters.AddWithValue("@subcontext_key", r.ContextKeys?.SubcontextKey ?? "");
        cmd.Parameters.AddWithValue("@process_label", r.ContextKeys?.ProcessLabel ?? "");
        cmd.Parameters.AddWithValue("@window_label", r.ContextKeys?.WindowLabel ?? "");
        cmd.Parameters.AddWithValue("@subcontext_label", r.ContextKeys?.SubcontextLabel ?? "");
        cmd.Parameters.AddWithValue("@typed_prefix", r.TypedPrefix ?? "");
        cmd.Parameters.AddWithValue("@shown_completion", r.ShownCompletion ?? "");
        cmd.Parameters.AddWithValue("@accepted_text", r.AcceptedText ?? "");
        cmd.Parameters.AddWithValue("@user_written_text", r.UserWrittenText ?? "");
        cmd.Parameters.AddWithValue("@commit_reason", r.CommitReason ?? "");
        cmd.Parameters.AddWithValue("@latency_ms", r.LatencyMs);
        cmd.Parameters.AddWithValue("@cycle_depth", r.CycleDepth);
        cmd.Parameters.AddWithValue("@edited_after_accept", r.EditedAfterAccept ? 1 : 0);
        cmd.Parameters.AddWithValue("@untouched_for_ms", r.UntouchedForMs);
        cmd.Parameters.AddWithValue("@quality_score", r.QualityScore);
        cmd.Parameters.AddWithValue("@source_weight", r.SourceWeight);
        cmd.Parameters.AddWithValue("@confidence", r.Confidence);
        cmd.Parameters.AddWithValue("@deleted_suffix", r.DeletedSuffix ?? "");
        cmd.Parameters.AddWithValue("@corrected_text", r.CorrectedText ?? "");
        cmd.Parameters.AddWithValue("@correction_backspaces", r.CorrectionBackspaces);
        cmd.Parameters.AddWithValue("@correction_type", r.CorrectionType ?? "");
    }

    private List<LearningEventRecord> QueryEvents(string sql, params (string name, object value)[] parameters)
    {
        var results = new List<LearningEventRecord>();
        try
        {
            using var conn = OpenReadConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRecord(reader));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] Query failed: {ex.Message}");
        }
        return results;
    }

    private static LearningEventRecord ReadRecord(SqliteDataReader r)
    {
        return new LearningEventRecord
        {
            EventId = r.GetString(r.GetOrdinal("event_id")),
            SessionId = r.GetString(r.GetOrdinal("session_id")),
            SuggestionId = r.GetString(r.GetOrdinal("suggestion_id")),
            RequestId = r.GetInt64(r.GetOrdinal("request_id")),
            TimestampUtc = DateTime.Parse(r.GetString(r.GetOrdinal("timestamp_utc")),
                null, System.Globalization.DateTimeStyles.RoundtripKind),
            EventType = r.GetString(r.GetOrdinal("event_type")),
            ProcessName = r.GetString(r.GetOrdinal("process_name")),
            Category = r.GetString(r.GetOrdinal("category")),
            SafeContextLabel = r.GetString(r.GetOrdinal("safe_context_label")),
            ContextKeys = new LearningEventContextKeys
            {
                ProcessKey = r.GetString(r.GetOrdinal("process_key")),
                WindowKey = r.GetString(r.GetOrdinal("window_key")),
                SubcontextKey = r.GetString(r.GetOrdinal("subcontext_key")),
                ProcessLabel = r.GetString(r.GetOrdinal("process_label")),
                WindowLabel = r.GetString(r.GetOrdinal("window_label")),
                SubcontextLabel = r.GetString(r.GetOrdinal("subcontext_label"))
            },
            TypedPrefix = r.GetString(r.GetOrdinal("typed_prefix")),
            ShownCompletion = r.GetString(r.GetOrdinal("shown_completion")),
            AcceptedText = r.GetString(r.GetOrdinal("accepted_text")),
            UserWrittenText = r.GetString(r.GetOrdinal("user_written_text")),
            CommitReason = r.GetString(r.GetOrdinal("commit_reason")),
            LatencyMs = r.GetInt32(r.GetOrdinal("latency_ms")),
            CycleDepth = r.GetInt32(r.GetOrdinal("cycle_depth")),
            EditedAfterAccept = r.GetInt32(r.GetOrdinal("edited_after_accept")) != 0,
            UntouchedForMs = r.GetInt32(r.GetOrdinal("untouched_for_ms")),
            QualityScore = r.GetFloat(r.GetOrdinal("quality_score")),
            SourceWeight = r.GetFloat(r.GetOrdinal("source_weight")),
            Confidence = r.GetDouble(r.GetOrdinal("confidence")),
            DeletedSuffix = r.GetString(r.GetOrdinal("deleted_suffix")),
            CorrectedText = r.GetString(r.GetOrdinal("corrected_text")),
            CorrectionBackspaces = r.GetInt32(r.GetOrdinal("correction_backspaces")),
            CorrectionType = r.GetString(r.GetOrdinal("correction_type"))
        };
    }

    // ── Connection management ────────────────────────────────────────────────

    private void OpenWriteConnection()
    {
        if (_writeConnection?.State == System.Data.ConnectionState.Open)
            return;

        _writeConnection?.Dispose();
        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();
    }

    private void EnsureWriteConnection()
    {
        if (_writeConnection?.State != System.Data.ConnectionState.Open)
            OpenWriteConnection();
    }

    private SqliteConnection OpenReadConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ── Recovery ─────────────────────────────────────────────────────────────

    private void TryRecoverCorrupted()
    {
        try
        {
            if (!File.Exists(_dbPath)) return;

            var corrupted = $"{_dbPath}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_dbPath, corrupted);
            Debug.WriteLine($"[LearningDB] Moved corrupted DB to {corrupted}");

            // Try importing from .bak files if they exist
            OpenWriteConnection();
            using var cmd = _writeConnection!.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 3000;
                """;
            cmd.ExecuteNonQuery();

            // Re-run full schema creation
            EnsureCreated();

            var appData = Path.GetDirectoryName(_dbPath)!;
            var trackingBak = Path.Combine(appData, "tracking.jsonl.bak");
            var completionsBak = Path.Combine(appData, "completions.jsonl.bak");
            if (File.Exists(trackingBak) || File.Exists(completionsBak))
            {
                ImportFromJsonl(
                    File.Exists(trackingBak) ? trackingBak : null,
                    File.Exists(completionsBak) ? completionsBak : null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] Recovery failed: {ex.Message}");
        }
    }

    private static void RenameToBackup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningDB] Failed to rename {path}: {ex.Message}");
        }
    }

    // ── Legacy record for migration ──────────────────────────────────────────

    private sealed class LegacyCompletionRecord
    {
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = "";
        public string Prefix { get; set; } = "";
        public string Completion { get; set; } = "";
        public string App { get; set; } = "";
        public string Window { get; set; } = "";
        public string Category { get; set; } = "";
        public int LatencyMs { get; set; } = -1;
        public int CycleDepth { get; set; }
        public bool EditedAfter { get; set; }
        public float QualityScore { get; set; } = 0.5f;
    }
}
