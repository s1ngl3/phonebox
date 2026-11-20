using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PhoneBox.Agent;

public sealed partial class SessionMonitor
{
    internal record TranscriptInfo(string Model, long TotalTokens, long InputTokens, long OutputTokens, string? LastTool, long LastCreatedAt)
    {
        public static readonly TranscriptInfo Empty = new("Gemini 3.8 Flash (High)", 0, 0, 0, null, 0);
    }

    /// <summary>
    /// Reads recent and active Antigravity coding sessions from ~/.gemini/antigravity.
    /// Fully native: reads conversation summaries, presence locks, and transcript streaming events.
    /// </summary>
    internal IEnumerable<AgentSession> ReadAntigravity(string? antigravityDir = null)
    {
        var baseDir = antigravityDir ?? Path.Combine(Home, ".gemini", "antigravity");
        if (!Directory.Exists(baseDir)) yield break;

        var presenceDir = Path.Combine(baseDir, "presence");
        var activeLocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(presenceDir))
        {
            foreach (var file in Directory.EnumerateFiles(presenceDir, "*.lock"))
            {
                var convId = Path.GetFileNameWithoutExtension(file);
                if (IsSessionLocked(file))
                {
                    activeLocks.Add(convId);
                }
            }
        }

        var dbPath = Path.Combine(baseDir, "conversation_summaries.db");
        if (!File.Exists(dbPath)) yield break;

        var rows = new List<(string ConvId, string? Title, string? WorkspaceUris, string? Status, long LastModified)>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, title, workspace_uris, status, last_modified_time FROM conversation_summaries ORDER BY last_modified_time DESC LIMIT 5";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var convId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (string.IsNullOrEmpty(convId)) continue;
                var title = reader.IsDBNull(1) ? null : reader.GetString(1);
                var uris = reader.IsDBNull(2) ? null : reader.GetString(2);
                var status = reader.IsDBNull(3) ? null : reader.GetString(3);
                long lastModMs = 0;
                if (!reader.IsDBNull(4))
                {
                    var strTime = reader.GetString(4);
                    if (DateTimeOffset.TryParse(strTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                        lastModMs = dto.ToUnixTimeMilliseconds();
                }
                rows.Add((convId, title, uris, status, lastModMs));
            }
        }
        catch (Exception ex)
        {
            log.LogDebug("Failed reading antigravity summaries db: {Msg}", ex.Message);
            yield break;
        }

        var brainDir = Path.Combine(baseDir, "brain");
        for (int i = 0; i < rows.Count; i++)
        {
            var (convId, title, uris, statusStr, lastModified) = rows[i];
            bool isRunning = activeLocks.Contains(convId) ||
                             string.Equals(statusStr, "CASCADE_RUN_STATUS_RUNNING", StringComparison.OrdinalIgnoreCase);

            // Only process running sessions or the single most recent one
            if (!isRunning && i > 0) continue;

            var cwd = DecodeWorkspace(uris);
            var transcript = ReadTranscript(brainDir, convId);

            var status = isRunning ? "busy" : "waiting";
            var toolDisplay = isRunning ? (transcript.LastTool ?? "Работает...") : "Ожидает ответа";
            var waitingFor = isRunning ? null : "Ожидает ответа";

            var since = transcript.LastCreatedAt > 0 ? transcript.LastCreatedAt : lastModified;
            if (since <= 0) since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var projectName = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(projectName)) projectName = !string.IsNullOrEmpty(title) ? title : "Antigravity";

            var shortId = convId.Length > 8 ? convId[..8] : convId;

            yield return new AgentSession(
                Id: $"antigravity:{shortId}",
                Tool: "antigravity",
                Name: projectName,
                Project: projectName,
                Status: status,
                WaitingFor: waitingFor,
                Since: since,
                StartedAt: lastModified > 0 ? lastModified : since,
                Pid: null,
                Model: transcript.Model,
                Effort: null,
                TotalTokens: transcript.TotalTokens > 0 ? transcript.TotalTokens : null,
                InputTokens: transcript.InputTokens > 0 ? transcript.InputTokens : null,
                OutputTokens: transcript.OutputTokens > 0 ? transcript.OutputTokens : null,
                CachedInputTokens: null,
                ReasoningTokens: null,
                LastInputTokens: null,
                ContextWindow: 1_000_000,
                TurnStartedAt: null,
                Outcome: null,
                LastTool: toolDisplay
            );
        }
    }

    private static bool IsSessionLocked(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string DecodeWorkspace(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return Directory.GetCurrentDirectory();
        try
        {
            var raw = uri.Trim();
            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    raw = doc.RootElement[0].GetString() ?? "";
            }
            if (raw.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Uri.UnescapeDataString(raw[8..]);
                if (decoded.Length > 2 && decoded[0] == '/' && decoded[2] == ':')
                    decoded = decoded[1..];
                else if (decoded.Length > 2 && decoded[1] == ':') { }
                else if (decoded.StartsWith('/')) decoded = decoded[1..];
                return decoded.Replace('/', Path.DirectorySeparatorChar);
            }
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Uri.UnescapeDataString(raw[7..]);
                return decoded.Replace('/', Path.DirectorySeparatorChar);
            }
            return raw.Replace('/', Path.DirectorySeparatorChar);
        }
        catch
        {
            return Directory.GetCurrentDirectory();
        }
    }

    internal static TranscriptInfo ReadTranscript(string brainDir, string convId)
    {
        var path = Path.Combine(brainDir, convId, ".system_generated", "logs", "transcript.jsonl");
        if (!File.Exists(path)) return TranscriptInfo.Empty;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int maxBytes = 64 * 1024;
            long offset = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var content = reader.ReadToEnd();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string model = "Gemini 3.8 Flash (High)";
            long totalTokens = 0, inputTokens = 0, outputTokens = 0;
            string? lastTool = null;
            long lastCreatedAt = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String)
                    {
                        if (DateTimeOffset.TryParse(ca.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                            lastCreatedAt = dto.ToUnixTimeMilliseconds();
                    }
                    if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        model = FormatModelName(m.GetString());
                    }
                    if (root.TryGetProperty("input_tokens", out var inp) && inp.TryGetInt64(out var inpVal))
                    {
                        inputTokens = inpVal;
                        totalTokens = inputTokens + outputTokens;
                    }
                    if (root.TryGetProperty("output_tokens", out var oup) && oup.TryGetInt64(out var oupVal))
                    {
                        outputTokens = oupVal;
                        totalTokens = inputTokens + outputTokens;
                    }
                    if (root.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0)
                    {
                        var lastCall = tc[tc.GetArrayLength() - 1];
                        var toolName = lastCall.TryGetProperty("name", out var tn) ? tn.GetString() ?? "tool" : "tool";
                        if (lastCall.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object &&
                            args.TryGetProperty("toolSummary", out var ts) && ts.ValueKind == JsonValueKind.String)
                        {
                            var summary = ts.GetString()?.Trim('"', '\'');
                            lastTool = !string.IsNullOrEmpty(summary) ? $"{toolName}: {summary}" : toolName;
                        }
                        else
                        {
                            lastTool = toolName;
                        }
                    }
                }
                catch { }
            }

            return new TranscriptInfo(model, totalTokens, inputTokens, outputTokens, lastTool, lastCreatedAt);
        }
        catch
        {
            return TranscriptInfo.Empty;
        }
    }

    internal static string FormatModelName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Gemini";
        var name = raw.ToLowerInvariant();
        if (name.Contains("flash"))
        {
            if (name.Contains("high")) return "Gemini 3.8 Flash (High)";
            return "Gemini 3.8 Flash";
        }
        if (name.Contains("pro")) return "Gemini Pro";
        if (name.Contains("gemini"))
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace("-", " "));
        }
        return raw;
    }
}
