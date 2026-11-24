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

                if (pName.Contains(targetPattern, StringComparison.OrdinalIgnoreCase) ||
                    title.Contains(targetPattern, StringComparison.OrdinalIgnoreCase))
                {
                    // Avoid hidden utility windows or background workers with no title
                    if (title.Length > 0 && !title.Equals("Default IME", StringComparison.OrdinalIgnoreCase))
                    {
                        found = hwnd;
                        return false; // Stop enumeration
                    }
                }
            }
            catch { }
            return true;
        }, 0);

        return found;
    }

    private static async Task<(bool Success, string? Error)> SendPromptWindowsAsync(string prompt, string? target, CancellationToken ct)
    {
        try
        {
            var hwnd = FindTargetWindow(target);
            if (hwnd != 0)
            {
                KeySender.NudgeInput();
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                await Task.Delay(100, ct);

                // Focus chat input in Antigravity IDE (Ctrl+L)
                KeySender.SendCombo(["ctrl", "l"]);
                await Task.Delay(80, ct);
            }
            else
            {
                // If specific Antigravity window was not identified, type into foreground window
                KeySender.NudgeInput();
            }

            // Type the prompt text
            if (!KeySender.TryTypeText(prompt, ct))
                return (false, "Could not type prompt into target window");

            await Task.Delay(60, ct);

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
