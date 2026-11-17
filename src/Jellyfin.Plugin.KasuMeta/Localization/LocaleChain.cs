using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.KasuMeta.Localization;

/// <summary>
/// BCP-47 handling for KasuMeta: normalizes codes and picks the best stored locale for a
/// request (exact, then base language, then a sibling of the same language and script).
/// </summary>
public static class LocaleChain
{
    /// <summary>Regions whose Chinese is written in Traditional characters.</summary>
    private static readonly HashSet<string> HantRegions = new(StringComparer.Ordinal) { "TW", "HK", "MO" };

    /// <summary>
    /// Normalizes a locale: <c>es_mx</c> → <c>es-MX</c>, <c>zh-hant-tw</c> → <c>zh-Hant-TW</c>,
    /// <c>JA</c> → <c>ja</c>. Accepts language, optional 4-letter script and optional 2-letter or
    /// 3-digit region; anything else is rejected.
    /// </summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var parts = input.Trim().Replace('_', '-').Split('-');
        if (parts.Length is < 1 or > 3 || !IsLanguage(parts[0]))
        {
            return false;
        }

        var result = new List<string>(3) { parts[0].ToLowerInvariant() };
        var index = 1;
        if (index < parts.Length && parts[index].Length == 4 && parts[index].All(char.IsAsciiLetter))
        {
            var script = parts[index];
            result.Add(char.ToUpperInvariant(script[0]) + script[1..].ToLowerInvariant());
            index++;
        }

        if (index < parts.Length)
        {
            var region = parts[index];
            if (region.Length == 2 && region.All(char.IsAsciiLetter))
            {
                result.Add(region.ToUpperInvariant());
            }
            else if (region.Length == 3 && region.All(char.IsAsciiDigit))
            {
                result.Add(region);
            }
            else
            {
                return false;
            }

            index++;
        }

        if (index != parts.Length)
        {
            return false;
        }

        normalized = string.Join('-', result);
        return true;
    }

    /// <summary>Normalizes or throws <see cref="ArgumentException"/>.</summary>
    public static string Normalize(string input) =>
        TryNormalize(input, out var normalized)
            ? normalized
            : throw new ArgumentException($"'{input}' is not a BCP-47 locale.", nameof(input));

    /// <summary>Gets the language subtag of a normalized locale (<c>es-MX</c> → <c>es</c>).</summary>
    public static string Language(string locale)
    {
        var dash = locale.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? locale : locale[..dash];
    }

    /// <summary>
    /// Gets the group a locale may fall back within: the language, except Chinese, which splits
    /// by script so Traditional and Simplified never stand in for each other.
    /// </summary>
    public static string FallbackGroup(string locale)
    {
        var language = Language(locale);
        if (language != "zh")
        {
            return language;
        }

        var parts = locale.Split('-');
        if (parts.Contains("Hant", StringComparer.Ordinal) || parts.Any(HantRegions.Contains))
        {
            return "zh-Hant";
        }

        return "zh-Hans";
    }

    /// <summary>
    /// Picks the stored locale to serve for <paramref name="requested"/>, or null if none fits.
    /// Order: exact; then (if <paramref name="fallbackToBase"/>) the bare language (<c>es</c>),
    /// then same-group siblings in <paramref name="siblingPreference"/> order (the admin's
    /// enabled-locales list), then the remaining siblings alphabetically.
    /// </summary>
    /// <param name="requested">The locale the client asked for (any casing or separator).</param>
    /// <param name="available">Locales that have a value for this field.</param>
    /// <param name="fallbackToBase">The plugin's <c>FallbackToBaseLanguage</c> setting.</param>
    /// <param name="siblingPreference">Preferred order among siblings, usually the enabled locales.</param>
    public static string? Resolve(
        string requested,
        IEnumerable<string> available,
        bool fallbackToBase,
        IReadOnlyList<string>? siblingPreference = null)
    {
        if (!TryNormalize(requested, out var wanted))
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (var locale in available)
        {
            if (TryNormalize(locale, out var n) && !candidates.Contains(n, StringComparer.Ordinal))
            {
                candidates.Add(n);
            }
        }

        if (candidates.Contains(wanted, StringComparer.Ordinal))
        {
            return wanted;
        }

        if (!fallbackToBase)
        {
            return null;
        }

        var group = FallbackGroup(wanted);
        var siblings = candidates.Where(c => FallbackGroup(c) == group).ToList();
        if (siblings.Count == 0)
        {
            return null;
        }

        var bare = Language(wanted);
        if (siblings.Contains(bare, StringComparer.Ordinal) && FallbackGroup(bare) == group)
        {
            return bare;
        }

        if (siblingPreference is not null)
        {
            foreach (var preferred in siblingPreference)
            {
                if (TryNormalize(preferred, out var p) && siblings.Contains(p, StringComparer.Ordinal))
                {
                    return p;
                }
            }
        }

        return siblings.Order(StringComparer.Ordinal).First();
    }

    private static bool IsLanguage(string part) =>
        part.Length is 2 or 3 && part.All(char.IsAsciiLetter);
}
