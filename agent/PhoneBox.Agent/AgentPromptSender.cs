using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Dispatches prompts from the tablet to the active coding agent (Antigravity IDE / terminal).
/// Brings the editor window to the front, focuses the agent chat (Ctrl+L), and types the prompt.
/// </summary>
public static class AgentPromptSender
{
    public const int MaxChars = 4000;

    public static async Task<(bool Success, string? Error)> SendPromptAsync(string prompt, string? target = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return (false, "Prompt is empty");

        prompt = prompt.Trim();
        if (prompt.Length > MaxChars)
            return (false, $"Prompt is too long (maximum {MaxChars} characters)");

        if (OperatingSystem.IsWindows())
        {
            return await SendPromptWindowsAsync(prompt, target, ct);
        }

        // Linux / macOS fallback
        try
        {
            KeySender.TypeText(prompt);
            await Task.Delay(50, ct);
            KeySender.SendKeys("enter");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

#if WINDOWS
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint hMem);

    private static bool SetClipboardText(string text)
    {
        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE = 0x0002;

        for (int i = 0; i < 5; i++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = Encoding.Unicode.GetBytes(text + "\0");
                    var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
                    if (hMem == 0) return false;

                    var pMem = GlobalLock(hMem);
                    if (pMem == 0) return false;

                    Marshal.Copy(bytes, 0, pMem, bytes.Length);
                    GlobalUnlock(hMem);

                    return SetClipboardData(CF_UNICODETEXT, hMem) != 0;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        return false;
    }

    private const int SW_RESTORE = 9;

    private static nint FindTargetWindow(string? target)
    {
        nint found = 0;
        var targetPattern = string.IsNullOrWhiteSpace(target) ? "Antigravity" : target;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;

            try
            {
                using var p = Process.GetProcessById((int)pid);
                var pName = p.ProcessName;
                var sb = new StringBuilder(512);
                var len = GetWindowTextW(hwnd, sb, sb.Capacity);
                var title = len > 0 ? sb.ToString() : "";

                // Skip background managers or IME windows
                if (pName.Equals("AntigravityManager", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("Antigravity Manager", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
                    title.Length == 0)
                {
                    return true;
                }

                // If looking for Antigravity, prefer "Antigravity IDE"
                if (targetPattern.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    if (pName.Contains("Antigravity IDE", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Antigravity IDE", StringComparison.OrdinalIgnoreCase))
                    {
                        found = hwnd;
                        return false; // Found exact IDE window
                    }
                }
                else if (pName.Contains(targetPattern, StringComparison.OrdinalIgnoreCase) ||
                         title.Contains(targetPattern, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }
            }
            catch { }
            return true;
        }, 0);

        return found;
    }

    private static void EnsureAntigravityKeybinding()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var keybindingsPath = Path.Combine(appData, "Antigravity IDE", "User", "keybindings.json");
            var dir = Path.GetDirectoryName(keybindingsPath);
            if (string.IsNullOrEmpty(dir)) return;

            Directory.CreateDirectory(dir);

            if (!File.Exists(keybindingsPath))
            {
                File.WriteAllText(keybindingsPath, "[\n  {\n    \"key\": \"ctrl+alt+shift+k\",\n    \"command\": \"antigravity.toggleChatFocus\",\n    \"args\": \"focus\"\n  }\n]\n");
                return;
            }

            var text = File.ReadAllText(keybindingsPath);
            if (text.Contains("antigravity.toggleChatFocus")) return;

            var trimmed = text.Trim();
            if (trimmed.EndsWith(']'))
            {
                var lastBracket = trimmed.LastIndexOf(']');
                var before = trimmed[..lastBracket].TrimEnd();
                var sep = before.EndsWith('{') || before.EndsWith('}') ? ",\n" : "\n";
                var updated = before + sep + "  {\n    \"key\": \"ctrl+alt+shift+k\",\n    \"command\": \"antigravity.toggleChatFocus\",\n    \"args\": \"focus\"\n  }\n]";
                File.WriteAllText(keybindingsPath, updated);
            }
        }
        catch { }
    }

    private static async Task<(bool Success, string? Error)> SendPromptWindowsAsync(string prompt, string? target, CancellationToken ct)
    {
        try
        {
            EnsureAntigravityKeybinding();

            var targetPattern = string.IsNullOrEmpty(target) ? "Antigravity" : target;
            var hwnd = FindTargetWindow(target);
            if (hwnd != 0)
            {
                KeySender.NudgeInput();
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                await Task.Delay(150, ct);

                if (targetPattern.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    // Focus chat input in Antigravity IDE without inserting file snippets or closing chat if already open
                    KeySender.SendCombo(["ctrl", "alt", "shift", "k"]);
                    // Give Electron / Monaco sufficient time to render and focus the chat input
                    await Task.Delay(350, ct);
                }
            }
            else
            {
                KeySender.NudgeInput();
                await Task.Delay(100, ct);
            }

            // Paste prompt text via Clipboard (instant, handles Russian/Unicode, doesn't get dropped by Monaco)
            if (SetClipboardText(prompt))
            {
                KeySender.SendCombo(["ctrl", "v"]);
                await Task.Delay(150, ct);
            }
            else
            {
                // Fallback to keystroke-by-keystroke typing
                if (!KeySender.TryTypeText(prompt, ct))
                    return (false, "Could not type prompt into target window");
                await Task.Delay(100, ct);
            }

            // Send Enter to submit the prompt to the agent
            if (!KeySender.SendCombo(["enter"]))
                return (false, "Could not submit prompt (Enter key refused)");

            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "Operation was cancelled");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
#else
    private static Task<(bool Success, string? Error)> SendPromptWindowsAsync(string prompt, string? target, CancellationToken ct) =>
        Task.FromResult<(bool Success, string? Error)>((false, "Windows only"));
#endif
}
