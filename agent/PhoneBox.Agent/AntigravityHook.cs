using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>
/// Antigravity integration: a PreToolUse hook in ~/.gemini/config/hooks.json posts each tool call to the
/// agent, which shows it on the phone and returns Allow / Deny as the hook's JSON output.
/// If no answer or phone offline, it returns {"decision":"ask"} so Antigravity asks in the terminal/UI as normal.
/// </summary>
public static class AntigravityHook
{
    public const string Endpoint = "/api/hooks/antigravity/permission";
    public const string HookName = "phonebox-gate";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string HooksPath = Path.Combine(Home, ".gemini", "config", "hooks.json");

    /// <summary>Handles one hook call; returns the JSON object for the hook to print.</summary>
    public static async Task<object> HandleAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions, CancellationToken aborted)
    {
        var c = cfg.Get().Antigravity;
        if (!c.PhoneApprovals) return Ask();
        if (c.OnlyWhenAway && Idle.Seconds() < 60) return Ask();

        var toolCall = input.TryGetProperty("toolCall", out var tc) && tc.ValueKind == JsonValueKind.Object ? tc : default;
        var tool = Str(toolCall, "name") ?? Str(input, "tool_name") ?? "a tool";
        var args = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("args", out var aProp) && aProp.ValueKind == JsonValueKind.Object
            ? aProp
            : (input.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object ? ti : default);

        var cwd = Str(input, "cwd") ?? "";
        var conversationId = Str(input, "conversationId") ?? Str(input, "session_id") ?? "";
        var session = sessions.Snapshot().FirstOrDefault(s => s.Id == "antigravity:" + conversationId);
        var project = session?.Name ?? ClaudeHook.ProjectName(cwd);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var question = tool is "ask_question" or "AskUserQuestion";
        var toolSummary = args.ValueKind == JsonValueKind.Object ? Str(args, "toolSummary") : null;
        var lastUserInput = Str(input, "lastUserInput");
        var description = !string.IsNullOrWhiteSpace(toolSummary) ? toolSummary : lastUserInput;

        var a = new Approval(
            ApprovalStore.NewId(),
            "Antigravity",
            conversationId,
            project,
            ToolTitle(tool),
            question ? QuestionDetail(args) : Detail(tool, args),
            description,
            now,
            now + Math.Clamp(c.TimeoutSec, 10, 3600) * 1000L,
            question ? "question" : "permission");

        var decision = await approvals.AskAsync(a, aborted);
        if (decision == "allow")
        {
            var overrides = PermissionOverrides(tool, args);
            return overrides is { Length: > 0 }
                ? (object)new { decision = "allow", permissionOverrides = overrides }
                : new { decision = "allow" };
        }
        if (decision == "deny")
            return new { decision = "deny", reason = "Denied from PhoneBox panel." };
        return Ask();
    }

    internal static string[]? PermissionOverrides(string tool, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (tool is "run_command" or "Bash")
        {
            var cmd = Str(args, "CommandLine") ?? Str(args, "command");
            if (!string.IsNullOrWhiteSpace(cmd)) return [$"command({cmd})"];
        }
        else if (tool is "write_file" or "Write" or "edit_file" or "Edit")
        {
            var p = Str(args, "Path") ?? Str(args, "file_path");
            if (!string.IsNullOrWhiteSpace(p)) return [$"write({p})", $"edit({p})"];
        }
        return null;
    }

    private static object Ask() => new { decision = "ask" };

    internal static string ToolTitle(string tool) => tool switch
    {
        "run_command" or "Bash" => "Run a command",
        "write_file" or "Write" => "Write a file",
        "edit_file" or "Edit" or "MultiEdit" => "Edit a file",
        "read_file" or "Read" => "Read a file",
        "search_web" or "WebSearch" => "Search the web",
        "ask_question" or "AskUserQuestion" => "Antigravity has a question",
        _ when tool.StartsWith("mcp__") => "Use " + tool[5..].Replace("__", " › "),
        _ => "Use " + tool,
    };

    internal static string Detail(string tool, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        string? s = tool switch
        {
            "run_command" or "Bash" => Str(args, "CommandLine") ?? Str(args, "command"),
            "read_file" or "Read" => Str(args, "Path") ?? Str(args, "file_path"),
            "write_file" or "Write" => (Str(args, "Path") ?? Str(args, "file_path")) is { } p
                ? p + "\n\n" + Head(Str(args, "Content") ?? "", 12)
                : null,
            "edit_file" or "Edit" or "MultiEdit" => (Str(args, "Path") ?? Str(args, "file_path")) is { } ep
                ? ep + (Str(args, "Target") is { } t ? "\n\n" + Head(t, 6) : "") + (Str(args, "Replacement") is { } r ? "\n→\n" + Head(r, 6) : "")
                : null,
            "search_web" or "WebSearch" => Str(args, "query"),
            _ => null,
        };
        s ??= JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    internal static string QuestionDetail(JsonElement args)
    {
        var sb = new System.Text.StringBuilder();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
            {
                if (sb.Length > 0) sb.Append("\n\n");
                var header = Str(q, "header");
                sb.Append(header is { Length: > 0 } ? header + ": " : "").Append(Str(q, "question") ?? "");
                var multi = (q.TryGetProperty("multiSelect", out var m1) && m1.ValueKind == JsonValueKind.True)
                         || (q.TryGetProperty("is_multi_select", out var m2) && m2.ValueKind == JsonValueKind.True);
                if (multi) sb.Append("  (more than one can be picked)");
                if (q.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in os.EnumerateArray())
                    {
                        if (o.ValueKind == JsonValueKind.String)
                        {
                            sb.Append("\n  • ").Append(o.GetString());
                        }
                        else if (o.ValueKind == JsonValueKind.Object)
                        {
                            var d = Str(o, "description");
                            sb.Append("\n  • ").Append(Str(o, "label") ?? Str(o, "text") ?? "").Append(d is { Length: > 0 } ? " — " + d : "");
                        }
                    }
                }
            }
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "Antigravity is asking something; answer it in the terminal on your PC.";
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    private static string Head(string text, int lines)
    {
        var l = text.Split('\n');
        return string.Join('\n', l.Take(lines)) + (l.Length > lines ? $"\n… ({l.Length - lines} more lines)" : "");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------------------------- install / remove hook in ~/.gemini/config/hooks.json

    internal static string HookCommand(int port, int timeoutSec)
    {
        var timeout = Math.Clamp(timeoutSec, 10, 3600);
        return OperatingSystem.IsWindows()
            ? $"curl.exe -s --fail --max-time {timeout + 15} -X POST http://127.0.0.1:{port}{Endpoint} -H \"Content-Type: application/json\" --data-binary \"@-\" || echo {{\"decision\":\"ask\"}}"
            : $"curl -s --fail --max-time {timeout + 15} -X POST http://127.0.0.1:{port}{Endpoint} -H \"Content-Type: application/json\" --data-binary \"@-\" || echo '{{\"decision\":\"ask\"}}'";
    }

    public static bool Installed() => Installed(HooksPath);

    internal static bool Installed(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root?[HookName]?["PreToolUse"] is JsonArray arr && arr.Count > 0;
        }
        catch { return false; }
    }

    public static string? Configure(bool install, int port, int timeoutSec, string? matcher = null, string? path = null)
    {
        path ??= HooksPath;
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new InvalidDataException("hooks.json is not a JSON object");
                File.WriteAllText(path + ".phonebox.bak", text);
            }
            else root = new JsonObject();

            root.Remove(HookName);
            if (install)
            {
                root[HookName] = new JsonObject
                {
                    ["PreToolUse"] = new JsonArray(new JsonObject
                    {
                        ["matcher"] = string.IsNullOrWhiteSpace(matcher) ? "run_command" : matcher,
                        ["hooks"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = HookCommand(port, timeoutSec),
                            ["timeout"] = Math.Clamp(timeoutSec, 10, 3600) + 15,
                        }),
                    }),
                };
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
