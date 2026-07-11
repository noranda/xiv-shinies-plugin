using Xunit;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The guard that stops a half-loaded character from being cached. A blank name or home world violates
// the contract's 1-100 character constraint, so the server rejects the upload with a 400 — which the
// plugin classifies as its own bug rather than something the user can fix, so it does NOT halt
// syncing. A blank identity, once cached, would therefore re-fail silently on every upload for the
// rest of the session.
public class CharacterIdentityTests
{
    [Fact]
    public void A_character_with_a_name_and_a_home_world_is_usable()
    {
        Assert.True(CharacterIdentity.IsUsable("Some Name", "Excalibur"));
    }

    // The home world is a lazy reference into a game data sheet. It resolves to null — and so to an
    // empty string — while the sheet is still loading, or for a world newer than the installed game
    // data.
    [Theory]
    [InlineData(null, "Excalibur")]
    [InlineData("", "Excalibur")]
    [InlineData("   ", "Excalibur")]
    [InlineData("Some Name", null)]
    [InlineData("Some Name", "")]
    [InlineData("Some Name", "   ")]
    [InlineData(null, null)]
    public void A_character_missing_either_field_is_not_usable(string? name, string? homeWorld)
    {
        Assert.False(CharacterIdentity.IsUsable(name, homeWorld));
    }

    // Whitespace-only is treated as absent, not as a one-character name: the server trims before it
    // length-checks, so "   " would arrive as "" and fail validation anyway.
    [Fact]
    public void Whitespace_is_not_a_name()
    {
        Assert.False(CharacterIdentity.IsUsable("\t\n ", "Excalibur"));
    }

    // The contract's other edge: 100 characters is the maximum the server accepts. Real names and
    // worlds are far shorter, so this guards a theoretical bad read the same way the blank check
    // guards a real one — either would 400 every upload for the session if cached.
    [Fact]
    public void The_contract_length_bound_is_enforced_on_both_ends()
    {
        var hundred = new string('a', 100);
        var hundredAndOne = new string('a', 101);

        Assert.True(CharacterIdentity.IsUsable(hundred, "Excalibur"));
        Assert.False(CharacterIdentity.IsUsable(hundredAndOne, "Excalibur"));
        Assert.False(CharacterIdentity.IsUsable("Some Name", hundredAndOne));

        // Trimming happens before the length check, matching the server's order of operations.
        Assert.True(CharacterIdentity.IsUsable($"  {hundred}  ", "Excalibur"));
    }
}
