namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One source of collection facts — completed quests, unlocked mounts, item possession, and so on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding a collection to this plugin is one new class implementing this interface, and nothing
/// else.</b> A collector announces its own <see cref="CategoryKey"/> and returns its own facts;
/// the runner, the payload, and the settings UI all iterate collectors generically. Nothing
/// downstream may branch on a category name — if you find yourself writing
/// <c>if (key == "quests")</c>, the design has gone wrong.
/// </para>
/// <para>
/// This interface is deliberately free of Dalamud types even though every real implementation
/// reads the game. That keeps the runner and the payload assembly unit-testable with a fake
/// collector, while the implementations themselves are verified by in-game QA.
/// </para>
/// </remarks>
// An `interface` is a contract a class promises to fulfil — the same idea as a TypeScript
// `interface` used with `class Foo implements Bar`, except C# enforces it at compile time.
public interface ICollector
{
    /// <summary>
    /// The payload key this collector's facts are sent under (for example <c>"quests"</c>). It is
    /// also the key used for the user's opt-in toggle and the server's kill switch, so all three
    /// line up without a lookup table.
    /// </summary>
    string CategoryKey { get; }

    /// <summary>
    /// The category's name as a person reads it (for example <c>"Mounts"</c>).
    /// </summary>
    /// <remarks>
    /// The copy lives on the collector rather than in the settings window on purpose. If the window
    /// held a key-to-label table, adding a collection would mean editing the window too, and that
    /// table would be a category-name branch by another name. A collector describes itself; the UI
    /// renders whatever it is handed.
    /// </remarks>
    string DisplayName { get; }

    /// <summary>
    /// A plain-language sentence naming exactly what leaves the player's machine for this category.
    /// </summary>
    /// <remarks>
    /// Shown next to the opt-in toggle. Dalamud requires that users be told what is collected before
    /// they consent to it, so this is a compliance surface, not decoration: it must describe the real
    /// payload, and it must be updated whenever <see cref="Collect"/> starts sending something new.
    /// Write it for someone who has never read this code.
    /// </remarks>
    string WhatGetsSent { get; }

    /// <summary>
    /// Reads the facts from the game, or explains why it could not.
    /// </summary>
    /// <param name="context">
    /// Values that vary between passes, such as the server's latest config and item manifest.
    /// Collectors that need nothing from it ignore it.
    /// </param>
    /// <remarks>
    /// Reads game state, so it must be called on the framework thread. Return
    /// <see cref="CollectResult.Skipped"/> rather than throwing when a source is simply not
    /// available yet (for example, the achievements list has never been opened) — a skip omits the
    /// category from the upload, which the server reads as "not read this time".
    /// </remarks>
    CollectResult Collect(CollectContext context);
}
