// Single source of truth for the CLI's command vocabulary: the rich Builtins
// registry (name, aliases, description, needs-workspace, default-ness, arg-syntax)
// from which the alias map, command resolution, unknown-command suggestion, and the
// render-ready help model are all derived. Kept pure (no I/O) and exhaustively
// testable, mirroring TrustGate.Decide's style. Owns the builtin <-> config merge
// policy (the "is there a commands.json?" fork) so no consumer re-encodes it.

using System.Runtime.InteropServices;

namespace Dev;

// One builtin command's canonical facts. NeedsWorkspace was previously implicit in
// the dispatch switch's ordering relative to the workspace-null guard; it is now data.
internal sealed record BuiltinCommand(
    string Name, string[] Aliases, string Description,
    bool NeedsWorkspace, bool IsDefault, string? ArgSyntax);

// One render-ready help row. Both the --json payload and the human table consume
// these and never re-derive names/aliases/descriptions or repeat the config merge.
internal sealed record HelpRow(
    string Name, string[] Aliases, string Description,
    bool? IsDefault,    // NULL only for the synthetic help row — the discriminator the JSON mapper keys off
    string Source,      // "builtin" | "config"
    string? BuiltIn, string? ArgSyntax);

internal static class CommandCatalog
{
    // THE registry — a static readonly LITERAL, not a table populated at runtime by a
    // Bind()-style call from Program startup. Order is the canonical help/suggest order;
    // help last. CommandCatalogResolveTests.AllAliases() enumerates the derived Aliases
    // at test-collection time: a literal is filled by static initialization on first
    // access (so it's ready), whereas a Bind()-from-startup table would still be empty
    // then (startup never ran), silently yielding zero theory cases.
    public static IReadOnlyList<BuiltinCommand> Builtins { get; } = new[]
    {
        new BuiltinCommand("launch", [], "Launches the current solution in your default IDE or project in Visual Studio Code.", NeedsWorkspace: true, IsDefault: true, ArgSyntax: null),
        new BuiltinCommand("bump", ["v"], "Bumps the version of all projects in the current solution or the current project. Defaults to minor.", NeedsWorkspace: true, IsDefault: false, ArgSyntax: "[major|minor|patch|revision]"),
        new BuiltinCommand("bump-commit", ["vc"], "Bumps the version and commits/tag the change in the current solution or project. Defaults to minor.", NeedsWorkspace: true, IsDefault: false, ArgSyntax: "[major|minor|patch|revision]"),
        new BuiltinCommand("build", ["b"], "Builds the current solution or project in Release mode.", NeedsWorkspace: true, IsDefault: false, ArgSyntax: null),
        new BuiltinCommand("frontend", ["f"], "Runs the Vidyano frontend builder in the current directory.", NeedsWorkspace: false, IsDefault: false, ArgSyntax: null),
        new BuiltinCommand("clean", ["c"], "Clean the current folder by removing bin, obj, tmp-build, bin-windows, bin-linux, obj-windows, obj-linux folders.", NeedsWorkspace: false, IsDefault: false, ArgSyntax: null),
        new BuiltinCommand("help", ["h", "?"], "Displays this help message.", NeedsWorkspace: false, IsDefault: false, ArgSyntax: null),
    };

    // Derived once from Builtins — replaces the hand-maintained inverted literals.
    public static IReadOnlyDictionary<string, string> Aliases { get; } =
        Builtins.SelectMany(c => c.Aliases.Select(a => (a, c.Name))).ToDictionary(t => t.a, t => t.Name);

    // Case-sensitive (Ordinal): dispatch resolves to a canonical builtin name and
    // must not treat "BUILD" as "build".
    public static BuiltinCommand? Find(string name) =>
        Builtins.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

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

    // Candidate set for an unknown-command "did you mean":
    //   no config -> builtin names + alias keys (today's behavior)
    //   config    -> config names only (undeclared builtins are "unknown")
    public static IEnumerable<string> SuggestionCandidates(IReadOnlyList<CommandsConfigEntry>? config) =>
        config is null ? Builtins.Select(c => c.Name).Concat(Aliases.Keys)
                       : config.Select(c => c.Name);

    // The complete, ordered, render-ready help model for the active config. Both
    // renderers iterate this and nothing else. Centralizes the config fork, the
    // OS-specific custom-command description fallback, alias inversion, the help/h
    // skip filter, and help-row synthesis.
    public static IReadOnlyList<HelpRow> HelpModel(IReadOnlyList<CommandsConfigEntry>? config)
    {
        var help = Find("help")!;
        var helpRow = new HelpRow(help.Name, help.Aliases, help.Description,
            IsDefault: null, Source: "builtin", BuiltIn: null, ArgSyntax: null);

        if (config is null)
        {
            var rows = new List<HelpRow>();
            foreach (var c in Builtins)
            {
                if (string.Equals(c.Name, "help", StringComparison.Ordinal)) continue;
                rows.Add(new HelpRow(c.Name, c.Aliases, c.Description,
                    IsDefault: c.IsDefault, Source: "builtin", BuiltIn: null, ArgSyntax: c.ArgSyntax));
            }
            rows.Add(helpRow);
            return rows;
        }

        var configRows = new List<HelpRow>();
        foreach (var c in config)
        {
            if (c.Name is "help" or "h") continue;
            if (!string.IsNullOrEmpty(c.BuiltIn))
            {
                var backing = Find(c.BuiltIn);
                configRows.Add(new HelpRow(c.Name, backing?.Aliases ?? [],
                    backing?.Description ?? c.BuiltIn,
                    IsDefault: c.Default, Source: "builtin", BuiltIn: c.BuiltIn, ArgSyntax: null));
            }
            else
            {
                var desc = c.Description
                    ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? c.Windows : c.NonWindows)
                    ?? "";
                configRows.Add(new HelpRow(c.Name, [], desc,
                    IsDefault: c.Default, Source: "config", BuiltIn: null, ArgSyntax: null));
            }
        }
        configRows.Add(helpRow);
        return configRows;
    }

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
