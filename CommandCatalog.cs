// Single source of truth for the CLI's command vocabulary: the alias→builtin
// mapping shared by command resolution and by the unknown-command suggestion
// logic, plus a pure nearest-match Suggest. Kept pure (no I/O) and exhaustively
// testable, mirroring TrustGate.Decide's style.

namespace Dev;

internal static class CommandCatalog
{
    public static IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>
    {
        ["b"] = "build",
        ["c"] = "clean",
        ["h"] = "help",
        ["?"] = "help",
        ["f"] = "frontend",
        ["v"] = "bump",
        ["vc"] = "bump-commit",
    };

    public static IReadOnlyList<string> Builtins { get; } = new[]
    {
        "launch", "bump", "bump-commit", "build", "frontend", "clean", "help",
    };

    public static (string Command, string? Alias) Resolve(string input) =>
        Aliases.TryGetValue(input, out var command) ? (command, input) : (input, null);

    // Returns the single nearest candidate to a mistyped command — the closest
    // near-miss by case-insensitive Levenshtein distance — or null when nothing
    // is close enough to be a confident "did you mean". A match is only offered
    // when the edit distance is <= 2 and strictly less than the input length, so
    // short inputs don't match unrelated short candidates. Ties resolve to the
    // first equal-distance candidate in enumeration order. Display only: this
    // never executes anything.
    public static string? Suggest(string input, IEnumerable<string> candidates)
    {
        var lowerInput = input.ToLowerInvariant();
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            // Candidates can include config command names from a user-supplied
            // commands.json, where a null/empty name survives deserialization.
            if (string.IsNullOrEmpty(candidate)) continue;

            var distance = Levenshtein(lowerInput, candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= 2 && bestDistance < lowerInput.Length ? best : null;
    }

    // Human-readable form of a suggested command for a "did you mean" prompt:
    // an alias is annotated with the command it resolves to, e.g. "'b' (build)".
    // Returns plain text; the caller is responsible for any renderer escaping.
    public static string DescribeSuggestion(string suggestion) =>
        Aliases.TryGetValue(suggestion, out var full) ? $"'{suggestion}' ({full})" : $"'{suggestion}'";

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
