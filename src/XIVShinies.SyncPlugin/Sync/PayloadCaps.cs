using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Enforces the server contract's payload caps client-side, before an upload is built.
/// </summary>
/// <remarks>
/// <para>
/// The contract caps id-list categories at 50,000 ids and object-entry categories (items)
/// at 10,000 entries; an over-cap payload is rejected whole (400), losing every category in
/// it. Truncating client-side keeps the rest of the upload alive, and monotonic writes make
/// the loss safe: a dropped id or entry is simply re-sent by a later full sweep.
/// </para>
/// <para>
/// Truncation is detected by SHAPE (an array of numbers vs an array of objects), never by
/// category name — the extensibility contract forbids category-name branches, and a future
/// category gets the right cap by looking like one shape or the other.
/// </para>
/// <para>
/// Never silent: every truncation is reported so the caller can log it. A silently capped
/// payload would read as "covered everything" when it did not.
/// </para>
/// </remarks>
public static class PayloadCaps
{
    /// <summary>The contract's ceiling for id-list categories.</summary>
    public const int MaxIdsPerCategory = 50_000;

    /// <summary>The contract's ceiling for object-entry categories.</summary>
    public const int MaxEntriesPerCategory = 10_000;

    /// <summary>Caps every over-limit category, returning what was dropped.</summary>
    /// <param name="collections">
    /// The snapshot's collections, keyed by category — the same shape that becomes the sync
    /// payload's <c>collections</c> object.
    /// </param>
    /// <returns>
    /// <c>Bounded</c> is the capped dictionary — the very same instance as <paramref
    /// name="collections"/> when nothing needed truncating, so a compliant payload costs no
    /// extra allocation. <c>Dropped</c> is one human-readable line per category that was cut,
    /// empty when nothing was.
    /// </returns>
    // The return type is a named TUPLE: one value carrying two named parts, which the caller
    // takes apart with `var (bounded, dropped) = ...`. The closest JS/TS analog is returning
    // an object and destructuring it — `const { bounded, dropped } = ...` — but a C# tuple is
    // a lightweight value type, not an allocated object.
    public static (Dictionary<string, JsonNode> Bounded, IReadOnlyList<string> Dropped)
        Bound(Dictionary<string, JsonNode> collections)
    {
        var dropped = new List<string>();

        // Rebuilt lazily: only allocated the first time a category actually needs truncating,
        // so a compliant payload (the overwhelmingly common case) returns the original
        // dictionary instance unchanged.
        Dictionary<string, JsonNode>? bounded = null;

        foreach (var (key, value) in collections)
        {
            // Only a JsonArray can be over a count cap; a category could in principle carry
            // some other JSON shape in the future, and that shape is left untouched here.
            if (value is not JsonArray array)
                continue;

            if (array.Count == 0)
                continue;

            // Shape, not name, decides which cap applies: a JsonValue element (a bare number)
            // is an id list, a JsonObject element is an entry list. The first element stands in
            // for the whole array — collectors never mix element shapes within one category.
            var cap = array[0] is JsonObject ? MaxEntriesPerCategory : MaxIdsPerCategory;

            if (array.Count <= cap)
                continue;

            var overBy = array.Count - cap;

            var truncated = new JsonArray();
            for (var i = 0; i < cap; i++)
            {
                // A JsonNode already parented to `array` cannot simply be moved into a second
                // array — JsonNode tracks a single parent, and re-adding it as-is throws.
                // DeepClone hands back a detached copy that `truncated` can own instead.
                // The null test guards a JSON `null` element: the collectors never produce one,
                // but a JsonArray can represent it, and cloning it would crash the whole pass.
                if (array[i] is { } element)
                    truncated.Add(element.DeepClone());
            }

            // First truncation seen: copy the dictionary once, so untouched categories keep
            // sharing their original JsonNode instances (Assert.Same territory) and only the
            // capped one gets a fresh value.
            bounded ??= new Dictionary<string, JsonNode>(collections);
            bounded[key] = truncated;

            dropped.Add($"{key}: dropped {overBy} entries over the contract cap of {cap}");
        }

        return (bounded ?? collections, dropped);
    }
}
