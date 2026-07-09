using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>GET /api/plugin/v1/me</c> — the status/link probe. Given only its token,
/// the plugin learns which account it belongs to and which characters that account has claimed.
/// </summary>
// Every DTO in this folder is a `sealed record` whose properties are `required … { get; init; }`.
// See SyncRequest.cs for what `record`, `required`, and `init` mean — briefly: a data-holding
// class with value equality, whose properties must be supplied at construction and can never be
// reassigned afterwards.
public sealed record MeResponse
{
    /// <summary>The account's claimed characters, ordered alphabetically by name.</summary>
    public required IReadOnlyList<MeCharacter> Characters { get; init; }

    /// <summary>The account the token belongs to.</summary>
    public required MeUser User { get; init; }
}

/// <summary>One character claimed by the token's account.</summary>
public sealed record MeCharacter
{
    /// <summary>
    /// The Lodestone id. It is a BigInt on the server, so it travels as a string — do not parse
    /// it into a number.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The character's name.</summary>
    public required string Name { get; init; }

    /// <summary>True when a ContentId hash is already bound to this character.</summary>
    public required bool PluginLinked { get; init; }

    /// <summary>True when the claim is verified (by bio code or by a plugin upload).</summary>
    public required bool Verified { get; init; }

    /// <summary>The character's home world name.</summary>
    public required string World { get; init; }
}

/// <summary>The account the token belongs to.</summary>
public sealed record MeUser
{
    /// <summary>The account's UUID.</summary>
    public required string Id { get; init; }
}
