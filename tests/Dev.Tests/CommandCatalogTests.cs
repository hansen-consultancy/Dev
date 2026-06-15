using Xunit;

namespace Dev.Tests;

public sealed class CommandCatalogSuggestTests
{
    // Mirrors the production caller: builtins plus alias keys.
    private static IEnumerable<string> BuiltinsAndAliases() =>
        CommandCatalog.Builtins.Concat(CommandCatalog.Aliases.Keys);

    [Fact]
    public void Bb_suggests_b_the_real_world_bug_case()
    {
        // "bb" vs alias "b": distance 1, 1 <= 2 and 1 < 2 -> suggested.
        Assert.Equal("b", CommandCatalog.Suggest("bb", BuiltinsAndAliases()));
    }

    [Theory]
    [InlineData("buld", "build")]
    [InlineData("clena", "clean")]
    public void Close_typo_of_full_command_suggests_it(string input, string expected)
    {
        Assert.Equal(expected, CommandCatalog.Suggest(input, BuiltinsAndAliases()));
    }

    [Theory]
    [InlineData("xyzzy")]   // min distance to any candidate > 2
    [InlineData("zzzzzz")]
    public void Far_off_input_returns_null(string input)
    {
        Assert.Null(CommandCatalog.Suggest(input, BuiltinsAndAliases()));
    }

    [Fact]
    public void Single_char_input_returns_null_even_when_aliases_are_distance_one()
    {
        // "x" is distance 1 from aliases like "b"/"c"/"f", but the guard
        // requires distance < input.Length, i.e. 1 < 1 -> false, so null.
        Assert.Null(CommandCatalog.Suggest("x", BuiltinsAndAliases()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_input_returns_null_without_throwing(string input)
    {
        Assert.Null(CommandCatalog.Suggest(input, BuiltinsAndAliases()));
    }

    [Fact]
    public void Null_or_empty_candidates_are_skipped_without_throwing()
    {
        // A commands.json entry with an explicit "name": null survives
        // deserialization as a null candidate; Suggest must skip it, not crash,
        // and still surface the real near-miss.
        Assert.Equal("release", CommandCatalog.Suggest("relese", new[] { null, "", "release" }!));
    }

    [Fact]
    public void Config_candidate_set_with_no_near_match_returns_null()
    {
        // "buil" vs "deploy"/"release": both far (> 2 edits) -> null.
        Assert.Null(CommandCatalog.Suggest("buil", new[] { "deploy", "release" }));
    }

    [Fact]
    public void Config_candidate_set_with_near_match_returns_it()
    {
        // "relese" vs "release": distance 1 (missing 'a') -> release.
        Assert.Equal("release", CommandCatalog.Suggest("relese", new[] { "deploy", "release" }));
    }

    [Fact]
    public void Ties_resolve_to_first_candidate_in_enumeration_order()
    {
        // "ax" is distance 1 from both "bx" (a->b) and "ay" (x->y).
        // Both <= 2 and < input length (2). First-in-order "bx" wins.
        Assert.Equal("bx", CommandCatalog.Suggest("ax", new[] { "bx", "ay" }));
        // Reverse the order to prove order is what decides the tie.
        Assert.Equal("ay", CommandCatalog.Suggest("ax", new[] { "ay", "bx" }));
    }

    [Fact]
    public void Exact_match_returns_the_candidate()
    {
        // distance 0; 0 <= 2 and 0 < 5 -> returns "build". (Won't occur in
        // practice since known commands never reach Suggest, but pin behavior.)
        Assert.Equal("build", CommandCatalog.Suggest("build", CommandCatalog.Builtins));
    }
}

public sealed class CommandCatalogResolveTests
{
    [Theory]
    [InlineData("b", "build")]
    [InlineData("?", "help")]
    [InlineData("vc", "bump-commit")]
    public void Resolve_maps_alias_to_command_and_records_alias(string alias, string command)
    {
        var (resolved, recordedAlias) = CommandCatalog.Resolve(alias);
        Assert.Equal(command, resolved);
        Assert.Equal(alias, recordedAlias);
    }

    [Fact]
    public void Resolve_passes_unknown_input_through_with_null_alias()
    {
        var (command, alias) = CommandCatalog.Resolve("bb");
        Assert.Equal("bb", command);
        Assert.Null(alias);
    }

    [Theory]
    [MemberData(nameof(AllAliases))]
    public void Resolve_agrees_with_catalog_for_every_alias(string alias, string command)
    {
        var (resolved, recordedAlias) = CommandCatalog.Resolve(alias);
        Assert.Equal(command, resolved);
        Assert.Equal(alias, recordedAlias);
    }

    public static TheoryData<string, string> AllAliases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (alias, command) in CommandCatalog.Aliases)
            data.Add(alias, command);
        return data;
    }

    [Fact]
    public void DescribeSuggestion_annotates_an_alias_with_its_command()
    {
        Assert.Equal("'b' (build)", CommandCatalog.DescribeSuggestion("b"));
    }

    [Fact]
    public void DescribeSuggestion_leaves_a_non_alias_unannotated()
    {
        Assert.Equal("'build'", CommandCatalog.DescribeSuggestion("build"));
    }
}
