using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class MicMuteShortcutTests
{
    private sealed class FakeAudioControl : AudioControl
    {
        public bool IsMuted { get; set; }

        public override AudioSnapshot? MicSnapshot() => new(50, IsMuted);

        public override bool ToggleMicMute()
        {
            IsMuted = !IsMuted;
            return true;
        }
    }

    [Fact]
    public void MicMuteShortcut_ReportsLiveState()
    {
        var sc = new Shortcut { Id = "mic", Label = "Mic", Cmd = "builtin:mic-mute" };
        var audio = new FakeAudioControl { IsMuted = true };

        var state = Shortcuts.State(sc, null, audio);
        Assert.True(state);

        audio.IsMuted = false;
        state = Shortcuts.State(sc, null, audio);
        Assert.False(state);
    }

    [Fact]
    public void MicMuteShortcut_IncludedInShortcutStates()
    {
        var cfg = new AgentConfig
        {
            Shortcuts =
            [
                new Shortcut { Id = "mic", Label = "Mic", Cmd = "builtin:mic-mute" },
                new Shortcut { Id = "mute", Label = "Mute", Cmd = "builtin:mute" }
            ]
        };
        var audio = new FakeAudioControl { IsMuted = true };
        var push = new PushStore(NullLogger<PushStore>.Instance);

        var states = Shortcuts.States(cfg, push, audio);
        Assert.NotNull(states);
        Assert.True(states.ContainsKey("mic"));
        Assert.True(states["mic"]);
    }

    [Fact]
    public void MicMuteShortcut_TogglesAudioControl()
    {
        var cfg = new AgentConfig
        {
            Shortcuts = [new Shortcut { Id = "mic", Label = "Mic", Cmd = "builtin:mic-mute" }]
        };
        var audio = new FakeAudioControl { IsMuted = false };
        var log = NullLogger.Instance;

        var err = Shortcuts.Run(cfg, "mic", log, null, audio);
        Assert.Null(err);
        Assert.True(audio.IsMuted);

        err = Shortcuts.Run(cfg, "mic", log, null, audio);
        Assert.Null(err);
        Assert.False(audio.IsMuted);
    }
}
