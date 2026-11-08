using System.Text;

namespace Kota.Core;

public readonly record struct BucketKey(DateTime HourUtc, Provider Provider, string Project, string Model);

public sealed class TokenTotals
{
    public long Input, Output, CacheWrite, CacheRead;
    public decimal Cost;
    public int Requests;

    public long Total => Input + Output + CacheWrite + CacheRead;

    public void Add(TokenTotals o)
    {
        Input += o.Input; Output += o.Output; CacheWrite += o.CacheWrite; CacheRead += o.CacheRead;
        Cost += o.Cost; Requests += o.Requests;
    }

    public TokenTotals Clone() => (TokenTotals)MemberwiseClone();
}

/// <summary>
/// Token usage per hour, provider, project and model, built from the local Claude Code and Codex logs.
/// Scans are incremental: each file is read from where the previous scan stopped, and the state is
/// cached on disk so a restart does not re-read gigabytes of logs.
/// </summary>
public sealed class UsageHistory(string claudeDir, string codexDir, string cachePath)
{
    // Bump when parsing or pricing changes, to rebuild caches made by older versions.
    const int CacheVersion = 1;

    readonly object _gate = new();
    readonly Dictionary<BucketKey, TokenTotals> _buckets = [];
    readonly HashSet<ulong> _seenClaudeMessages = [];
    readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    bool _dirty;

    sealed class FileState
    {
        public long Offset;
        // Codex only: running totals, since Codex logs cumulative session counts.
        public long PrevInput, PrevCached, PrevOutput;
        public string? Model, Project;
    }

    /// <summary>Scans new log lines and returns a copy of all buckets. Safe to call from any thread.</summary>
    public IReadOnlyDictionary<BucketKey, TokenTotals> Refresh()
    {
        lock (_gate)
        {
            ScanClaude();
            ScanCodex();
            if (_dirty)
            {
                TrySave();
                _dirty = false;
            }
            return _buckets.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        }
    }

    // ---------------- Claude Code ----------------

    void ScanClaude()
    {
        var projects = Path.Combine(claudeDir, "projects");
        foreach (var file in SafeEnumerate(projects))
        {
            var fallbackProject = FallbackProjectName(projects, file.FullName);
            ScanFile(file, (ReadOnlySpan<byte> line, FileState _) => HandleClaudeLine(line, fallbackProject));
        }
    }

    internal void HandleClaudeLine(ReadOnlySpan<byte> line, string fallbackProject)
    {
        if (line.IndexOf("\"usage\""u8) < 0 || line.IndexOf("\"assistant\""u8) < 0) return;
        using var doc = JsonlReader.TryParse(line);
        if (doc is null) return;
        var root = doc.RootElement;
        if (root.Str("type") != "assistant" || root.Obj("message") is not { } message
                                            || message.Obj("usage") is not { } usage) return;

        var model = message.Str("model") ?? "unknown";
        if (model.StartsWith('<')) return; // "<synthetic>" messages are not real API calls

        // Resumed and forked sessions copy earlier messages into a new file; count each one once.
        var id = (message.Str("id") ?? "") + ":" + (root.Str("requestId") ?? "");
        if (id != ":" && !_seenClaudeMessages.Add(Fnv1a(id))) return;
        if (root.Time("timestamp") is not { } timestamp) return;

        long input = usage.Long("input_tokens");
        long output = usage.Long("output_tokens");
        long cacheWrite = usage.Long("cache_creation_input_tokens");
        long cacheRead = usage.Long("cache_read_input_tokens");
        long cacheWrite1h = usage.Obj("cache_creation") is { } cc ? cc.Long("ephemeral_1h_input_tokens") : 0;
        cacheWrite1h = Math.Min(cacheWrite1h, cacheWrite);
        bool fast = usage.Str("speed") == "fast";

        var totals = new TokenTotals
        {
            Input = input, Output = output, CacheWrite = cacheWrite, CacheRead = cacheRead, Requests = 1,
            Cost = Pricing.ClaudeCost(model, input, output, cacheWrite - cacheWrite1h, cacheWrite1h, cacheRead, fast),
        };
        var project = ProjectName(root.Str("cwd")) ?? fallbackProject;
        Add(new BucketKey(HourUtc(timestamp), Provider.Claude, project, model), totals);
    }

    // ---------------- Codex ----------------

    void ScanCodex()
    {
        foreach (var folder in new[] { "sessions", "archived_sessions" })
            foreach (var file in SafeEnumerate(Path.Combine(codexDir, folder)))
                ScanFile(file, HandleCodexLine);
    }

    void HandleCodexLine(ReadOnlySpan<byte> line, FileState state)
    {
        bool meta = line.IndexOf("\"session_meta\""u8) >= 0;
        bool context = !meta && line.IndexOf("\"turn_context\""u8) >= 0;
        bool tokens = !meta && !context && line.IndexOf("\"token_count\""u8) >= 0;
        if (!meta && !context && !tokens) return;

        using var doc = JsonlReader.TryParse(line);
        if (doc is null || doc.RootElement.Obj("payload") is not { } payload) return;
        var root = doc.RootElement;
        var type = root.Str("type");

        if (type == "session_meta")
        {
            state.Project = ProjectName(payload.Str("cwd")) ?? state.Project;
            return;
        }
        if (type == "turn_context")
        {
            state.Model = payload.Str("model") ?? state.Model;
            return;
        }
        if (type != "event_msg" || payload.Str("type") != "token_count") return;
        if (payload.Obj("info") is not { } info || info.Obj("total_token_usage") is not { } total) return;
        if (root.Time("timestamp") is not { } timestamp) return;

        long input = total.Long("input_tokens");
        long cached = total.Long("cached_input_tokens");
        long output = total.Long("output_tokens");
        if (input < state.PrevInput || output < state.PrevOutput)
            state.PrevInput = state.PrevCached = state.PrevOutput = 0; // counter restarted
        if (input + output <= state.PrevInput + state.PrevOutput) return; // repeated event, nothing new

        long dInput = input - state.PrevInput;
        long dCached = Math.Clamp(cached - state.PrevCached, 0, dInput);
        long dOutput = output - state.PrevOutput;
        state.PrevInput = input; state.PrevCached = cached; state.PrevOutput = output;

        // Codex counts cached tokens inside input_tokens; split them so totals match Claude's meaning.
        var totals = new TokenTotals
        {
            Input = dInput - dCached, CacheRead = dCached, Output = dOutput, Requests = 1,
        };
        Add(new BucketKey(HourUtc(timestamp), Provider.Codex, state.Project ?? "?", state.Model ?? "codex"), totals);
    }

    // ---------------- shared ----------------

    delegate void StatefulLineHandler(ReadOnlySpan<byte> line, FileState state);

    void ScanFile(FileInfo file, StatefulLineHandler handler)
    {
        if (!_files.TryGetValue(file.FullName, out var state))
        {
            state = new FileState();
            _files[file.FullName] = state;
        }
        if (file.Length == state.Offset) return;
        if (file.Length < state.Offset) state.Offset = 0; // replaced; Claude dedupe protects totals

        try
        {
            long newOffset = JsonlReader.ReadFrom(file.FullName, state.Offset, line => handler(line, state));
            if (newOffset != state.Offset)
            {
                state.Offset = newOffset;
                _dirty = true;
            }
        }
        catch (IOException)
        {
            // Locked or deleted mid-scan; try again next time.
        }
    }

    void Add(BucketKey key, TokenTotals totals)
    {
        if (_buckets.TryGetValue(key, out var existing)) existing.Add(totals);
        else _buckets[key] = totals;
        _dirty = true;
    }

    static IEnumerable<FileInfo> SafeEnumerate(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true,
            }).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    static DateTime HourUtc(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc);
    }

    internal static string? ProjectName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    /// <summary>Claude stores each project as a folder like "C--Users-me-code-myapp".</summary>
    static string FallbackProjectName(string projectsDir, string filePath)
    {
        var relative = Path.GetRelativePath(projectsDir, filePath);
        var folder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var dash = folder.LastIndexOf('-');
        return dash >= 0 && dash < folder.Length - 1 ? folder[(dash + 1)..] : folder;
    }

    static ulong Fnv1a(string s)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    // ---------------- cache ----------------

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(cachePath)) return;
                using var r = new BinaryReader(File.OpenRead(cachePath), Encoding.UTF8);
                if (r.ReadInt32() != CacheVersion) return;
                if (r.ReadString() != claudeDir || r.ReadString() != codexDir) return;

                int buckets = r.ReadInt32();
                for (int i = 0; i < buckets; i++)
                {
                    var key = new BucketKey(new DateTime(r.ReadInt64(), DateTimeKind.Utc), (Provider)r.ReadByte(),
                        r.ReadString(), r.ReadString());
                    _buckets[key] = new TokenTotals
                    {
                        Input = r.ReadInt64(), Output = r.ReadInt64(), CacheWrite = r.ReadInt64(),
                        CacheRead = r.ReadInt64(), Cost = r.ReadDecimal(), Requests = r.ReadInt32(),
                    };
                }
                int seen = r.ReadInt32();
                for (int i = 0; i < seen; i++) _seenClaudeMessages.Add(r.ReadUInt64());
                int files = r.ReadInt32();
                for (int i = 0; i < files; i++)
                {
                    var path = r.ReadString();
                    _files[path] = new FileState
                    {
                        Offset = r.ReadInt64(), PrevInput = r.ReadInt64(), PrevCached = r.ReadInt64(),
                        PrevOutput = r.ReadInt64(), Model = NullIfEmpty(r.ReadString()),
                        Project = NullIfEmpty(r.ReadString()),
                    };
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                           or UnauthorizedAccessException or ArgumentException)
            {
                // A broken cache just means a full rescan.
                _buckets.Clear(); _seenClaudeMessages.Clear(); _files.Clear();
            }
        }
    }

    void TrySave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temp = cachePath + ".tmp";
            using (var w = new BinaryWriter(File.Create(temp), Encoding.UTF8))
            {
                w.Write(CacheVersion);
                w.Write(claudeDir);
                w.Write(codexDir);
                w.Write(_buckets.Count);
                foreach (var (k, v) in _buckets)
                {
                    w.Write(k.HourUtc.Ticks); w.Write((byte)k.Provider); w.Write(k.Project); w.Write(k.Model);
                    w.Write(v.Input); w.Write(v.Output); w.Write(v.CacheWrite); w.Write(v.CacheRead);
                    w.Write(v.Cost); w.Write(v.Requests);
                }
                w.Write(_seenClaudeMessages.Count);
                foreach (var h in _seenClaudeMessages) w.Write(h);
                w.Write(_files.Count);
                foreach (var (path, s) in _files)
                {
                    w.Write(path); w.Write(s.Offset); w.Write(s.PrevInput); w.Write(s.PrevCached);
                    w.Write(s.PrevOutput); w.Write(s.Model ?? ""); w.Write(s.Project ?? "");
                }
            }
            File.Move(temp, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: we'll rescan next start.
        }
    }

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}

public sealed record DailyTotal(DateOnly Day, TokenTotals Claude, TokenTotals Codex);

public sealed record ProjectTotal(string Project, long ClaudeTokens, long CodexTokens, decimal Cost);

public static class UsageStats
{
    public static IReadOnlyList<DailyTotal> LastDays(IReadOnlyDictionary<BucketKey, TokenTotals> buckets,
        int days, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = today.AddDays(-(days - 1));
        var result = Enumerable.Range(0, days)
            .Select(i => new DailyTotal(first.AddDays(i), new TokenTotals(), new TokenTotals()))
            .ToArray();
        foreach (var (k, v) in buckets)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(k.HourUtc, zone));
            int index = day.DayNumber - first.DayNumber;
            if (index < 0 || index >= days) continue;
            (k.Provider == Provider.Claude ? result[index].Claude : result[index].Codex).Add(v);
        }
        return result;
    }

    public static IReadOnlyList<ProjectTotal> TopProjects(IReadOnlyDictionary<BucketKey, TokenTotals> buckets,
        DateTimeOffset since, int take)
    {
        return buckets
            .Where(kv => kv.Key.HourUtc >= since.UtcDateTime.AddHours(-1))
            .GroupBy(kv => kv.Key.Project, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectTotal(
                g.First().Key.Project,
                g.Where(x => x.Key.Provider == Provider.Claude).Sum(x => x.Value.Total),
                g.Where(x => x.Key.Provider == Provider.Codex).Sum(x => x.Value.Total),
                g.Sum(x => x.Value.Cost)))
            .OrderByDescending(p => p.Cost)
            .ThenByDescending(p => p.ClaudeTokens + p.CodexTokens)
            .Take(take)
            .ToList();
    }
}
