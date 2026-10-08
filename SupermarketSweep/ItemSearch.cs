using System.Text;
using Lumina.Excel.Sheets;

namespace SupermarketSweep;

/// <summary>
/// Loose item-name search. The query is split into words and an item matches when every word appears
/// somewhere in its name, in any order, so "courtly lover's fending" finds
/// "Courtly Lover's Gauntlets of Fending". Apostrophes are ignored and other punctuation counts as a space
/// ("lovers" finds "Lover's", "general purpose" finds "General-purpose"). If that finds nothing, each word of
/// five or more letters may be off by one typo.
/// </summary>
public class ItemSearch
{
    private const int TypoMinLength = 5;

    private readonly record struct Entry(Item Item, string Name, string Normalized, string[] Words);

    private readonly Entry[] _entries;
    private string _lastQuery = string.Empty;
    private List<Item> _lastResults = [];

    public ItemSearch(IEnumerable<Item> items)
    {
        _entries = items
            .Select(item => (item, name: item.Name.ToString()))
            .Where(p => !string.IsNullOrWhiteSpace(p.name))
            .Select(p =>
            {
                var normalized = Normalize(p.name);
                return new Entry(p.item, p.name, normalized,
                    normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            })
            .ToArray();
    }

    /// <summary>Results for <paramref name="query"/>, best first. Cached, so it's cheap to call every frame.</summary>
    public IReadOnlyList<Item> Search(string query)
    {
        if (query == _lastQuery)
            return _lastResults;

        _lastQuery = query;
        var normalizedQuery = Normalize(query);
        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return _lastResults = [];

        var matches = _entries.Where(e => tokens.All(t => e.Normalized.Contains(t, StringComparison.Ordinal)))
            .ToList();
        if (matches.Count == 0)
            matches = _entries.Where(e => tokens.All(t => TokenMatchesWithTypo(t, e))).ToList();

        _lastResults = matches
            .OrderBy(e => Rank(e, normalizedQuery))
            .ThenBy(e => e.Name.Length)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Item)
            .ToList();
        return _lastResults;
    }

    /// <summary>Same matching rules as <see cref="Search"/> (minus typo tolerance), for filtering short lists.</summary>
    public static bool Matches(string name, string query)
    {
        var normalizedName = Normalize(name);
        return Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(t => normalizedName.Contains(t, StringComparison.Ordinal));
    }

    // Exact name, then names starting with the query, then the query appearing as one run, then everything else.
    private static int Rank(Entry e, string normalizedQuery)
    {
        if (e.Normalized == normalizedQuery) return 0;
        if (e.Normalized.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 1;
        if (e.Normalized.Contains(normalizedQuery, StringComparison.Ordinal)) return 2;
        return 3;
    }

    private static bool TokenMatchesWithTypo(string token, Entry e)
    {
        if (e.Normalized.Contains(token, StringComparison.Ordinal))
            return true;
        if (token.Length < TypoMinLength)
            return false;

        foreach (var word in e.Words)
        {
            // Compare against the whole word, and against its start so a partly typed word still counts.
            if (WithinOneEdit(token, word))
                return true;
            if (word.Length > token.Length && WithinOneEdit(token, word[..token.Length]))
                return true;
        }

        return false;
    }

    // True when a and b differ by at most one insertion, deletion, substitution or swap of adjacent letters.
    private static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;

        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
            i++;
        if (i == a.Length && i == b.Length)
            return true;

        if (a.Length == b.Length)
        {
            if (a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1)))
                return true;
            return i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i]
                   && a.AsSpan(i + 2).SequenceEqual(b.AsSpan(i + 2));
        }

        return a.Length > b.Length
            ? a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i))
            : a.AsSpan(i).SequenceEqual(b.AsSpan(i + 1));
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = true;
        foreach (var c in s)
        {
            if (c is '\'' or '’' or '‘')
                continue;

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
