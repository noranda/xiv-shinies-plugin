// xUnit is the test framework (comparable to Jest/Vitest in the JS world). `Xunit` gives us
// the [Fact] attribute and the Assert helpers.
using Xunit;
// The namespace of the code under test, so we can name PluginMeta directly.
using XIVShinies.SyncPlugin;

namespace XIVShinies.SyncPlugin.Tests;

// A plain class holds the tests (xUnit creates a fresh instance per test, so there's no shared
// state to leak between them). This smoke test proves two things at once: the xUnit harness runs,
// and the test project successfully links against pure plugin logic.
//
// Reminder on scope (see CLAUDE.md): unit tests here cover PURE logic only. Anything that touches
// game APIs (IUnlockState, IPlayerState, inventory) or live HTTP is verified via in-game QA
// instead, because Dalamud services can't be created outside the running game.
public class PluginMetaTests
{
    // `[Fact]` marks a parameterless test method — the equivalent of `test(...)`/`it(...)` in
    // Jest/Vitest. xUnit discovers and runs every [Fact].
    [Fact]
    public void CommandName_is_the_slash_command()
    {
        // Assert.Equal(expected, actual) fails the test if they differ — like expect(actual).toBe(expected).
        Assert.Equal("/shinies", PluginMeta.CommandName);
    }

    [Fact]
    public void CommandAlias_is_the_longer_form()
    {
        Assert.Equal("/xivshinies", PluginMeta.CommandAlias);
    }

    [Fact]
    public void DisplayName_is_the_plugin_name()
    {
        // Locks the human-facing name (used in the window title and /xlhelp text, and meant to
        // mirror the manifest "Name") so it can't drift silently.
        Assert.Equal("XIV Shinies Sync", PluginMeta.DisplayName);
    }

    [Fact]
    public void UserAgent_embeds_the_version_in_the_contract_format()
    {
        // Verifies the exact User-Agent shape the server contract requires.
        Assert.Equal("XIVShinies.SyncPlugin/1.2.3", PluginMeta.UserAgent("1.2.3"));
    }
}
