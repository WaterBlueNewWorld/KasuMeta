using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// The admin's genre and tag dictionary, kept in memory: every overlay with genres or tags reads
/// it, and it changes only when an admin saves it here.
/// </summary>
public sealed class TermDictionary : IDisposable
{
    private readonly ILocalizedMetadataStore _store;
    private readonly SemaphoreSlim _load = new(1, 1);
    private volatile TermSnapshot? _snapshot;

    public TermDictionary(ILocalizedMetadataStore store)
    {
        _store = store;
    }

    /// <summary>Gets the current dictionary, loading it on first use.</summary>
    public async Task<TermSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is { } current)
        {
            return current;
        }

        await _load.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is { } loaded)
            {
                return loaded;
            }

            var entries = new List<TermTranslation>();
            foreach (var kind in TermKinds.All)
            {
                entries.AddRange(await _store.GetTermsAsync(kind, null, cancellationToken).ConfigureAwait(false));
            }

            var snapshot = new TermSnapshot(entries);
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _load.Release();
        }
    }

    /// <summary>Writes translations (null removes one) and drops the cached copy.</summary>
    /// <returns>How many rows were written or removed.</returns>
    public async Task<int> SetAsync(
        string kind,
        string locale,
        IReadOnlyDictionary<string, string?> translations,
        CancellationToken cancellationToken)
    {
        await _load.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = await _store.SetTermsAsync(kind, locale, translations, cancellationToken).ConfigureAwait(false);
            _snapshot = null;
            return changed;
        }
        finally
        {
            _load.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _load.Dispose();
}

/// <summary>An immutable copy of the dictionary.</summary>
public sealed class TermSnapshot
{
    // (kind, term) → locale → translation; terms compare ignoring case.
    private readonly Dictionary<(string Kind, string Term), Dictionary<string, string>> _byTerm;

    public TermSnapshot(IEnumerable<TermTranslation> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _byTerm = new Dictionary<(string, string), Dictionary<string, string>>(TermKeyComparer.Instance);
        foreach (var entry in entries)
        {
            if (!_byTerm.TryGetValue((entry.Kind, entry.Term), out var locales))
            {
                _byTerm[(entry.Kind, entry.Term)] = locales = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            locales[entry.Locale] = entry.Value;
        }
    }

    /// <summary>Gets an empty dictionary.</summary>
    public static TermSnapshot Empty { get; } = new([]);

    /// <summary>
    /// Translates one term into the requested locale: exact, then (with
    /// <paramref name="fallbackToBase"/>) another locale of its language, as text fields fall back.
    /// </summary>
    /// <returns>The translation and the locale it is stored under, or null.</returns>
    public (string Value, string Locale)? Translate(
        string kind,
        string term,
        string locale,
        bool fallbackToBase,
        IReadOnlyList<string> siblingPreference)
    {
        if (!_byTerm.TryGetValue((kind, term), out var locales))
        {
            return null;
        }

        var resolved = LocaleChain.Resolve(locale, locales.Keys, fallbackToBase, siblingPreference);
        return resolved is not null && locales.TryGetValue(resolved, out var value) ? (value, resolved) : null;
    }

    private sealed class TermKeyComparer : IEqualityComparer<(string Kind, string Term)>
    {
        public static TermKeyComparer Instance { get; } = new();

        public bool Equals((string Kind, string Term) x, (string Kind, string Term) y) =>
            StringComparer.Ordinal.Equals(x.Kind, y.Kind) && StringComparer.OrdinalIgnoreCase.Equals(x.Term, y.Term);

        public int GetHashCode((string Kind, string Term) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Kind), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Term));
    }
}
