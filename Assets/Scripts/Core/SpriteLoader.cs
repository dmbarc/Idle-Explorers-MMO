using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Loads sprites from Resources, including the ones Resources.Load cannot reach.
///
/// A texture imported as Sprite Mode "Multiple" holds several named sub-sprites and
/// no sprite of its own, so Resources.Load&lt;Sprite&gt;(path) returns NULL for it —
/// with no error, no warning, and no way to tell that apart from a typo in the path.
/// Every SPUM armour, pant and cloth sheet is a Multiple-mode texture cut into
/// Body / Left / Right, which is why chest armour equipped fine and rendered nothing.
///
/// Addresses may name a sub-sprite explicitly as "path#Name". A bare path resolves in
/// the caller's preferred order, so ONE address in item_data.json can dress a body
/// and both shoulders from the same sheet.
///
/// Results are cached, including misses: this runs on every equipment change, and
/// Resources.LoadAll walks the whole texture each time it is called.
/// </summary>
public static class SpriteLoader
{
    private static readonly Dictionary<string, Sprite> _cache = new();

    /// <summary>
    /// Resolves an address to a sprite, or null.
    ///
    /// <paramref name="preferredName"/> picks a sub-sprite from a Multiple-mode sheet
    /// when the address does not name one itself — "Left", "Right" or "Body". It is
    /// only a preference: a sheet that has no such sub-sprite falls back to its first,
    /// so a single-piece texture still works everywhere.
    /// </summary>
    public static Sprite Load(string address, string preferredName = null)
    {
        if (string.IsNullOrEmpty(address)) return null;

        string cacheKey = preferredName == null ? address : address + "#?" + preferredName;
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var resolved = Resolve(address, preferredName);
        _cache[cacheKey] = resolved;
        return resolved;
    }

    private static Sprite Resolve(string address, string preferredName)
    {
        string path    = address;
        string wanted  = preferredName;

        // An explicit "#Name" always wins over the caller's preference.
        int hash = address.IndexOf('#');
        if (hash >= 0)
        {
            path   = address.Substring(0, hash);
            wanted = address.Substring(hash + 1);
        }

        // Single-mode textures answer directly, and this is the common case.
        var direct = Resources.Load<Sprite>(path);
        if (direct != null && string.IsNullOrEmpty(wanted)) return direct;

        var all = Resources.LoadAll<Sprite>(path);
        if (all == null || all.Length == 0) return direct;

        if (!string.IsNullOrEmpty(wanted))
        {
            foreach (var sprite in all)
                if (string.Equals(sprite.name, wanted, System.StringComparison.OrdinalIgnoreCase))
                    return sprite;

            // Asked for "Left" on a sheet that has no sides. One piece of art for both
            // sides beats nothing at all.
            foreach (var sprite in all)
                if (string.Equals(sprite.name, "Body", System.StringComparison.OrdinalIgnoreCase))
                    return sprite;
        }

        return direct != null ? direct : all[0];
    }

    /// <summary>
    /// Every sub-sprite name at an address, for editor validation. Empty when the
    /// address resolves to nothing.
    /// </summary>
    public static string[] SubSpriteNames(string address)
    {
        if (string.IsNullOrEmpty(address)) return System.Array.Empty<string>();

        int hash = address.IndexOf('#');
        string path = hash >= 0 ? address.Substring(0, hash) : address;

        var all = Resources.LoadAll<Sprite>(path);
        if (all == null || all.Length == 0) return System.Array.Empty<string>();

        var names = new string[all.Length];
        for (int i = 0; i < all.Length; i++) names[i] = all[i].name;
        return names;
    }

    /// <summary>Drops the cache. Called by editor tooling after re-importing art.</summary>
    public static void Clear() => _cache.Clear();
}
