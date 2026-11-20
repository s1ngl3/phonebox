using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class AntigravityHookTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void ConfigureInstallsAndRemovesHookCleanly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "phonebox-hook-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Initial install
            Assert.False(AntigravityHook.Installed(tempFile));
            var err = AntigravityHook.Configure(true, 8765, 90, "run_command", path: tempFile);
            Assert.Null(err);
            Assert.True(AntigravityHook.Installed(tempFile));

            // Verify content
            var text = File.ReadAllText(tempFile);
            var root = JsonNode.Parse(text)?.AsObject();
            Assert.NotNull(root);
            var gate = root["phonebox-gate"]?["PreToolUse"]?.AsArray();
            Assert.NotNull(gate);
            Assert.Single(gate);
            Assert.Equal("run_command", gate[0]?["matcher"]?.GetValue<string>());
            var hook = gate[0]?["hooks"]?.AsArray()?[0];
            Assert.NotNull(hook);
            Assert.Contains("/api/hooks/antigravity/permission", hook["command"]?.GetValue<string>());
            Assert.Equal(105, hook["timeout"]?.GetValue<int>());

            // Configure with a different matcher updates it
            err = AntigravityHook.Configure(true, 8765, 60, "run_command|write_file", path: tempFile);
            Assert.Null(err);
            text = File.ReadAllText(tempFile);
            root = JsonNode.Parse(text)?.AsObject();
            gate = root?["phonebox-gate"]?["PreToolUse"]?.AsArray();
            Assert.Equal("run_command|write_file", gate?[0]?["matcher"]?.GetValue<string>());

            // Removal
            err = AntigravityHook.Configure(false, 8765, 90, path: tempFile);
            Assert.Null(err);
            Assert.False(AntigravityHook.Installed(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempFile + ".tmp")) File.Delete(tempFile + ".tmp");
            if (File.Exists(tempFile + ".phonebox.bak")) File.Delete(tempFile + ".phonebox.bak");
        }
    }

    [Fact]
    public void PreservesExistingHooksInFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "phonebox-hook-existing-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, """
                {
                  "custom-linter": {
                    "PostToolUse": [{ "matcher": "run_command", "hooks": [{ "command": "echo lint" }] }]
                  }
                }
                """);

            var err = AntigravityHook.Configure(true, 8765, 90, "run_command", path: tempFile);
            Assert.Null(err);

            var root = JsonNode.Parse(File.ReadAllText(tempFile))?.AsObject();
            Assert.NotNull(root);
            Assert.NotNull(root["custom-linter"]);
            Assert.NotNull(root["phonebox-gate"]);

            err = AntigravityHook.Configure(false, 8765, 90, path: tempFile);
            Assert.Null(err);

            root = JsonNode.Parse(File.ReadAllText(tempFile))?.AsObject();
            Assert.NotNull(root);
            Assert.NotNull(root["custom-linter"]);
            Assert.Null(root["phonebox-gate"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempFile + ".tmp")) File.Delete(tempFile + ".tmp");
            if (File.Exists(tempFile + ".phonebox.bak")) File.Delete(tempFile + ".phonebox.bak");
        }
    }

    [Fact]
    public void DetailFormatsCommandLineAndFileOperations()
    {
        var cmdDetail = AntigravityHook.Detail("run_command", Json("""{"CommandLine":"git status"}"""));
        Assert.Equal("git status", cmdDetail);

        var writeDetail = AntigravityHook.Detail("write_file", Json("""{"Path":"test.txt","Content":"hello world"}"""));
        Assert.StartsWith("test.txt\n\nhello world", writeDetail);

        var editDetail = AntigravityHook.Detail("edit_file", Json("""{"Path":"test.txt","Target":"foo","Replacement":"bar"}"""));
        Assert.StartsWith("test.txt\n\nfoo\n→\nbar", editDetail);
    }

    [Fact]
    public void QuestionDetailFormatsAntigravityQuestions()
    {
        var detail = AntigravityHook.QuestionDetail(Json("""
            {
              "questions": [
                {
                  "question": "Which database would you prefer?",
                  "is_multi_select": false,
                  "options": ["SQLite", "PostgreSQL", "SQL Server"]
                }
              ]
            }
            """));

        Assert.Contains("Which database would you prefer?", detail);
        Assert.Contains("• SQLite", detail);
        Assert.Contains("• PostgreSQL", detail);
    }

    [Fact]
    public async Task HandleAsyncReturnsAskWhenPhoneNotConnected()
    {
        var cfgFile = Path.Combine(Path.GetTempPath(), "phonebox-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(cfgFile, "{}");
            var cfg = new ConfigStore(cfgFile);
            var approvals = new ApprovalStore(new PushStore(NullLogger<PushStore>.Instance, Path.Combine(Path.GetTempPath(), "phonebox-p-" + Guid.NewGuid().ToString("N") + ".json")), NullLogger<ApprovalStore>.Instance);
            var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);

            // Phone is disconnected
            ApprovalStore.PhoneLastSeen = 0;

            var input = Json("""
                {
                  "conversationId": "conv-1",
                  "toolCall": {
                    "name": "run_command",
                    "args": { "CommandLine": "npm test" }
                  }
                }
                """);

            var result = await AntigravityHook.HandleAsync(input, cfg, approvals, sessions, CancellationToken.None);
            var json = JsonSerializer.Serialize(result);
            Assert.Contains("\"decision\":\"ask\"", json);
        }
        finally
        {
            if (File.Exists(cfgFile)) File.Delete(cfgFile);
        }
    }

    [Fact]
    public async Task HandleAsyncReturnsAllowWhenDecidedOnPhone()
    {
        var cfgFile = Path.Combine(Path.GetTempPath(), "phonebox-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(cfgFile, "{}");
            var cfg = new ConfigStore(cfgFile);
            var approvals = new ApprovalStore(new PushStore(NullLogger<PushStore>.Instance, Path.Combine(Path.GetTempPath(), "phonebox-p-" + Guid.NewGuid().ToString("N") + ".json")), NullLogger<ApprovalStore>.Instance);
            var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);

            // Phone is connected
            ApprovalStore.PhoneLastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var input = Json("""
                {
                  "conversationId": "conv-1",
                  "toolCall": {
                    "name": "run_command",
                    "args": { "CommandLine": "dotnet test", "toolSummary": "Run tests" }
                  }
                }
                """);

            var handleTask = AntigravityHook.HandleAsync(input, cfg, approvals, sessions, CancellationToken.None);

            // Wait until the approval is pending
            var pending = Assert.Single(approvals.Snapshot());
            Assert.Equal("Antigravity", pending.Source);
            Assert.Equal("Run a command", pending.Tool);
            Assert.Equal("dotnet test", pending.Detail);
            Assert.Equal("Run tests", pending.Description);

            // Simulate user clicking "Allow" on phone
            Assert.True(approvals.Decide(pending.Id, "allow"));

            var result = await handleTask;
            var json = JsonSerializer.Serialize(result);
            Assert.Contains("\"decision\":\"allow\"", json);
            Assert.Contains("\"permissionOverrides\":[\"command(dotnet test)\"]", json);
        }
        finally
        {
            if (File.Exists(cfgFile)) File.Delete(cfgFile);
        }
    }

    [Fact]
    public async Task HandleAsyncReturnsDenyWhenDeniedOnPhone()
    {
        var cfgFile = Path.Combine(Path.GetTempPath(), "phonebox-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(cfgFile, "{}");
            var cfg = new ConfigStore(cfgFile);
            var approvals = new ApprovalStore(new PushStore(NullLogger<PushStore>.Instance, Path.Combine(Path.GetTempPath(), "phonebox-p-" + Guid.NewGuid().ToString("N") + ".json")), NullLogger<ApprovalStore>.Instance);
            var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);

            // Phone is connected
            ApprovalStore.PhoneLastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var input = Json("""
                {
                  "conversationId": "conv-2",
                  "toolCall": {
                    "name": "run_command",
                    "args": { "CommandLine": "rm -rf /" }
                  }
                }
                """);

            var handleTask = AntigravityHook.HandleAsync(input, cfg, approvals, sessions, CancellationToken.None);

            var pending = Assert.Single(approvals.Snapshot());
            Assert.True(approvals.Decide(pending.Id, "deny"));

            var result = await handleTask;
            var json = JsonSerializer.Serialize(result);
            Assert.Contains("\"decision\":\"deny\"", json);
            Assert.Contains("Denied from PhoneBox panel", json);
        }
        finally
        {
            if (File.Exists(cfgFile)) File.Delete(cfgFile);
        }
    }
}
