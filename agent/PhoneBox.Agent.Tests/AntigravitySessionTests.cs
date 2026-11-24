using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class AntigravitySessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-antigravity-" + Guid.NewGuid());
    private readonly SessionMonitor _monitor = new(NullLogger<SessionMonitor>.Instance);

    public AntigravitySessionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void FormatModelName_FormatsKnownModels()
    {
        Assert.Equal("Gemini 3.8 Flash (High)", SessionMonitor.FormatModelName("gemini-3.8-flash-high"));
        Assert.Equal("Gemini 3.8 Flash", SessionMonitor.FormatModelName("gemini-flash"));
        Assert.Equal("Gemini Pro", SessionMonitor.FormatModelName("gemini-pro"));
        Assert.Equal("Gemini", SessionMonitor.FormatModelName(""));
    }

    [Fact]
    public void DecodeWorkspace_ParsesEncodedUris()
    {
        var path = SessionMonitor.DecodeWorkspace("[\"file:///c%3A/my-project\"]");
        Assert.Contains("my-project", path);
        Assert.DoesNotContain("%3A", path);
        Assert.DoesNotContain("file:", path);
    }

    [Fact]
    public void ReadTranscript_ParsesTokensAndToolSummary()
    {
        var brainDir = Path.Combine(_root, "brain");
        var convId = "test-conv-1";
        var logsDir = Path.Combine(brainDir, convId, ".system_generated", "logs");
        Directory.CreateDirectory(logsDir);

        var transcriptFile = Path.Combine(logsDir, "transcript.jsonl");
        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                created_at = "2026-11-19T13:20:37Z",
                model = "gemini-3.8-flash-high",
                input_tokens = 100,
                output_tokens = 50,
                tool_calls = new object[]
                {
                    new { name = "run_command", args = new { toolSummary = "Git status check" } }
                }
            })
        };
        File.WriteAllLines(transcriptFile, lines);

        var info = SessionMonitor.ReadTranscript(brainDir, convId);
        Assert.Equal("Gemini 3.8 Flash (High)", info.Model);
        Assert.Equal(150, info.TotalTokens);
        Assert.Equal("run_command: Git status check", info.LastTool);
    }

    [Fact]
    public void ReadAntigravity_DiscoversActiveSessionFromDbAndLock()
    {
        var presenceDir = Path.Combine(_root, "presence");
        Directory.CreateDirectory(presenceDir);
        var convId = "1be12f4a-8054-44aa-8458-d3936a71f530";
        var lockFile = Path.Combine(presenceDir, $"{convId}.lock");
        File.WriteAllText(lockFile, "");

        // Keep file locked to simulate active session
        using var lockStream = File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var dbPath = Path.Combine(_root, "conversation_summaries.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT PRIMARY KEY,
                    title TEXT,
                    workspace_uris TEXT,
                    status TEXT,
                    last_modified_time TEXT
                );
                INSERT INTO conversation_summaries VALUES (
                    '1be12f4a-8054-44aa-8458-d3936a71f530',
                    'PhoneBox Development',
                    '[""file:///d%3A/PhoneBox""]',
                    'CASCADE_RUN_STATUS_RUNNING',
                    '2026-11-19 13:25:39.9631037+00:00'
                );";
            cmd.ExecuteNonQuery();
        }

        var brainDir = Path.Combine(_root, "brain");
        var logsDir = Path.Combine(brainDir, convId, ".system_generated", "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllLines(Path.Combine(logsDir, "transcript.jsonl"), new[]
        {
            JsonSerializer.Serialize(new
            {
                created_at = "2026-11-19T13:25:40Z",
                model = "gemini-3.8-flash-high",
                input_tokens = 2000,
                output_tokens = 300,
                tool_calls = new object[]
                {
                    new { name = "edit_file", args = new { toolSummary = "Update SessionMonitor" } }
                }
            })
        });

        var sessions = _monitor.ReadAntigravity(_root).ToList();
        var session = Assert.Single(sessions);
        Assert.Equal("antigravity:1be12f4a", session.Id);
        Assert.Equal("antigravity", session.Tool);
        Assert.Equal("PhoneBox", session.Project);
        Assert.Equal("busy", session.Status);
        Assert.Equal("Gemini 3.8 Flash (High)", session.Model);
        Assert.Equal(2300, session.TotalTokens);
        Assert.Equal("edit_file: Update SessionMonitor", session.LastTool);
    }

    [Fact]
    public void CleanPrompt_StripsTagsCorrectly()
    {
        Assert.Equal("Привет, агент!", SessionMonitor.CleanPrompt("<USER_REQUEST>\nПривет, агент!\n</USER_REQUEST>"));
        Assert.Equal("Без тегов", SessionMonitor.CleanPrompt("Без тегов"));
        Assert.Equal("", SessionMonitor.CleanPrompt("   "));
        Assert.Equal("", SessionMonitor.CleanPrompt(null!));
    }

    [Fact]
    public void ReadLatestChat_ExtractsPromptResponseAndStatus()
    {
        var dbPath = Path.Combine(_root, "conversation_summaries.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS conversation_summaries (
                    conversation_id TEXT PRIMARY KEY,
                    title TEXT,
                    workspace_uris TEXT,
                    status TEXT,
                    last_modified_time TEXT
                );
                INSERT OR REPLACE INTO conversation_summaries VALUES (
                    'chat-conv-123',
                    'Chat Project',
                    '[""file:///d%3A/ChatApp""]',
                    'CASCADE_RUN_STATUS_WAITING',
                    '2026-11-20 15:00:00.0000000+00:00'
                );";
            cmd.ExecuteNonQuery();
        }

        var brainDir = Path.Combine(_root, "brain");
        var logsDir = Path.Combine(brainDir, "chat-conv-123", ".system_generated", "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllLines(Path.Combine(logsDir, "transcript.jsonl"), new[]
        {
            JsonSerializer.Serialize(new
            {
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                content = "<USER_REQUEST>\nСделай новую фичу\n</USER_REQUEST>",
                created_at = "2026-11-20T15:01:00Z"
            }),
            JsonSerializer.Serialize(new
            {
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                content = "Конечно, фича готова!\n\n- Пункт 1\n- Пункт 2",
                model = "gemini-3.8-flash-high",
                created_at = "2026-11-20T15:02:00Z"
            })
        });

        var chat = _monitor.ReadLatestChat(_root);
        Assert.Equal("chat-conv-123", chat.ConversationId);
        Assert.Equal("ChatApp", chat.Project);
        Assert.Equal("Gemini 3.8 Flash (High)", chat.Model);
        Assert.Equal("waiting", chat.Status);
        Assert.Equal("Сделай новую фичу", chat.LastPrompt);
        Assert.Equal("Конечно, фича готова!\n\n- Пункт 1\n- Пункт 2", chat.LastResponse);
    }

    [Fact]
    public async Task AgentPromptSender_ValidatesInput()
    {
        var (s1, err1) = await AgentPromptSender.SendPromptAsync("");
        Assert.False(s1);
        Assert.Equal("Prompt is empty", err1);

        var longPrompt = new string('A', 5000);
        var (s2, err2) = await AgentPromptSender.SendPromptAsync(longPrompt);
        Assert.False(s2);
        Assert.Contains("too long", err2);
    }

    [Fact]
    public void AgentEndpoints_AreAllowedOverLan()
    {
        Assert.True(Lan.AllowedOverLan(new Microsoft.AspNetCore.Http.PathString("/api/agent/chat")));
        Assert.True(Lan.AllowedOverLan(new Microsoft.AspNetCore.Http.PathString("/api/agent/prompt")));
    }
}
