using System;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Who the upload is about: the local character, identified the way the server knows it.
/// </summary>
/// <remarks>
/// Note this holds the <b>hash</b>, never the raw ContentId. Hashing happens at the edge, the moment
/// the id is read from the game, so the raw value never reaches the payload, the log, or the disk.
/// The name and world are carried only for the server's first-upload binding and to render a
/// friendly "claim this character" message; the hash is the durable identity, which is what lets a
/// character survive a rename or a world transfer.
/// </remarks>
// `required` forces the caller to set the property in the object initializer; the compiler refuses to
// build the object otherwise. `init` means it can be set only at construction, never reassigned. Both
// together give a record that cannot be built half-populated and cannot be mutated afterwards.
public sealed record CharacterIdentity
{
    /// <summary>Lowercase-hex SHA-256 of the character's ContentId.</summary>
    public required string ContentIdHash { get; init; }

    /// <summary>The character's name. The server requires 1-100 characters after trimming.</summary>
    public required string Name { get; init; }

    /// <summary>The character's home world name. The server requires 1-100 characters.</summary>
    public required string HomeWorld { get; init; }

    /// <summary>
    /// True when this identity can produce a payload the server will accept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game does not always have the character fully populated the instant it says it is loaded,
    /// and the home world is a lazy reference into a data sheet that can fail to resolve. Either can
    /// yield a blank string.
    /// </para>
    /// <para>
    /// A blank — or absurdly long — name or world violates the contract's 1-100 character
    /// constraint, so the server rejects the whole upload with a 400. That rejection is classified
    /// as a plugin bug rather than something the user can fix, so it does not halt syncing —
    /// meaning a bad identity, once cached, would quietly re-fail every upload for the rest of the
    /// session. Checking before caching is what prevents that. (Real names and worlds are nowhere
    /// near 100 characters; the upper bound is defensive, so the guard fully matches the
    /// constraint it exists to satisfy.)
    /// </para>
    /// </remarks>
    // A static method rather than an instance one, so the caller can test the raw values it read from
    // the game before it commits them to a CharacterIdentity.
    public static bool IsUsable(string? name, string? homeWorld) =>
        IsWithinContract(name) && IsWithinContract(homeWorld);

    // The contract's bound: 1-100 characters after trimming (the payload builder trims too).
    private static bool IsWithinContract(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.AsSpan().Trim().Length <= 100;
}
