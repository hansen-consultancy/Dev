using System.Runtime.InteropServices;
using Xunit;

namespace Dev.Tests;

// Boundary tests for the CommandCatalog surface introduced by issue #24:
// HelpModel (no-config + config forks), SuggestionCandidates, the dispatch
// exhaustiveness drift guard, and the NeedsWorkspace partition.
public sealed class CommandCatalogHelpModelTests
{
    private static HelpRow Row(IReadOnlyList<HelpRow> rows, string name) =>
        rows.Single(r => r.Name == name);

    // ---- 1. HelpModel(null) — no-config ----------------------------------

    [Fact]
    public void HelpModel_noConfig_returns_seven_rows_in_canonical_order()
    {
        var rows = CommandCatalog.HelpModel(null);
        Assert.Equal(
            new[] { "launch", "bump", "bump-commit", "build", "frontend", "clean", "help" },
            rows.Select(r => r.Name));
    }

    [Fact]
    public void HelpModel_noConfig_isDefault_true_only_for_launch_and_null_for_help()
    {
        var rows = CommandCatalog.HelpModel(null);

        Assert.True(Row(rows, "launch").IsDefault);
        Assert.False(Row(rows, "bump").IsDefault);
        Assert.False(Row(rows, "bump-commit").IsDefault);
        Assert.False(Row(rows, "build").IsDefault);
        Assert.False(Row(rows, "frontend").IsDefault);
        Assert.False(Row(rows, "clean").IsDefault);

        // The discriminator the JSON mapper keys off: help's IsDefault is NULL,
        // not false. Assert it explicitly.
        Assert.Null(Row(rows, "help").IsDefault);
    }

    [Fact]
    public void HelpModel_noConfig_argSyntax_only_on_bump_commands()
    {
        var rows = CommandCatalog.HelpModel(null);

        Assert.Equal("[major|minor|patch|revision]", Row(rows, "bump").ArgSyntax);
        Assert.Equal("[major|minor|patch|revision]", Row(rows, "bump-commit").ArgSyntax);

        Assert.Null(Row(rows, "launch").ArgSyntax);
        Assert.Null(Row(rows, "build").ArgSyntax);
        Assert.Null(Row(rows, "frontend").ArgSyntax);
        Assert.Null(Row(rows, "clean").ArgSyntax);
        Assert.Null(Row(rows, "help").ArgSyntax);
    }

    [Fact]
    public void HelpModel_noConfig_every_row_is_builtin_source_with_null_builtIn()
    {
        var rows = CommandCatalog.HelpModel(null);
        Assert.All(rows, r =>
        {
            Assert.Equal("builtin", r.Source);
            Assert.Null(r.BuiltIn);
        });
    }

    [Fact]
    public void HelpModel_noConfig_aliases_match_the_catalog()
    {
        var rows = CommandCatalog.HelpModel(null);

        Assert.Empty(Row(rows, "launch").Aliases);
        Assert.Equal(new[] { "v" }, Row(rows, "bump").Aliases);
        Assert.Equal(new[] { "vc" }, Row(rows, "bump-commit").Aliases);
        Assert.Equal(new[] { "b" }, Row(rows, "build").Aliases);
        Assert.Equal(new[] { "f" }, Row(rows, "frontend").Aliases);
        Assert.Equal(new[] { "c" }, Row(rows, "clean").Aliases);
        Assert.Equal(new[] { "h", "?" }, Row(rows, "help").Aliases);
    }

    [Fact]
    public void HelpModel_noConfig_descriptions_match_the_builtin_records()
    {
        var rows = CommandCatalog.HelpModel(null);

        Assert.Equal("Displays this help message.", Row(rows, "help").Description);
        Assert.Equal("Builds the current solution or project in Release mode.", Row(rows, "build").Description);
    }

    // ---- 2. HelpModel(config) — with commands.json -----------------------

    private static List<CommandsConfigEntry> GoldenConfig() => new()
    {
        new CommandsConfigEntry
        {
            Name = "deploy",
            Description = "Deploy the current app",
            Windows = "deploy.cmd {sln}",
            NonWindows = "./deploy.sh {sln}",
            Default = true,
        },
        new CommandsConfigEntry { Name = "build", BuiltIn = "build" },
        new CommandsConfigEntry { Name = "vbump", BuiltIn = "bump" },
    };

    [Fact]
    public void HelpModel_config_returns_four_rows_in_order()
    {
        var rows = CommandCatalog.HelpModel(GoldenConfig());
        Assert.Equal(new[] { "deploy", "build", "vbump", "help" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void HelpModel_config_custom_deploy_row()
    {
        var deploy = Row(CommandCatalog.HelpModel(GoldenConfig()), "deploy");

        Assert.Equal("config", deploy.Source);
        Assert.Null(deploy.BuiltIn);
        Assert.Empty(deploy.Aliases);
        Assert.Equal("Deploy the current app", deploy.Description);
        Assert.True(deploy.IsDefault);
        Assert.Null(deploy.ArgSyntax);
    }

    [Fact]
    public void HelpModel_config_builtin_backed_build_row()
    {
        var build = Row(CommandCatalog.HelpModel(GoldenConfig()), "build");
        var backing = CommandCatalog.Find("build")!;

        Assert.Equal("builtin", build.Source);
        Assert.Equal("build", build.BuiltIn);
        Assert.Equal(new[] { "b" }, build.Aliases);
        Assert.Equal(backing.Description, build.Description);
        Assert.False(build.IsDefault);
    }

    [Fact]
    public void HelpModel_config_builtin_backed_renamed_vbump_row()
    {
        var vbump = Row(CommandCatalog.HelpModel(GoldenConfig()), "vbump");
        var backing = CommandCatalog.Find("bump")!;

        Assert.Equal("vbump", vbump.Name);
        Assert.Equal("builtin", vbump.Source);
        Assert.Equal("bump", vbump.BuiltIn);
        Assert.Equal(new[] { "v" }, vbump.Aliases);
        Assert.Equal(backing.Description, vbump.Description);
        Assert.False(vbump.IsDefault);
    }

    [Fact]
    public void HelpModel_config_synthetic_help_row_is_appended()
    {
        var help = Row(CommandCatalog.HelpModel(GoldenConfig()), "help");

        Assert.Equal("help", help.Name);
        Assert.Equal(new[] { "h", "?" }, help.Aliases);
        Assert.Null(help.IsDefault);
        Assert.Equal("builtin", help.Source);
        Assert.Null(help.BuiltIn);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("h")]
    public void HelpModel_config_entry_named_help_or_h_is_skipped_and_synthetic_help_appended_once(string name)
    {
        var config = new List<CommandsConfigEntry>
        {
            new CommandsConfigEntry { Name = "deploy", Description = "Deploy the current app", Default = true },
            new CommandsConfigEntry { Name = name, Description = "should be skipped" },
        };

        var rows = CommandCatalog.HelpModel(config);

        // Exactly one help row, and it is the synthetic builtin one (not the
        // skipped config entry's description).
        var helpRows = rows.Where(r => r.Name == "help").ToList();
        Assert.Single(helpRows);
        Assert.Equal("Displays this help message.", helpRows[0].Description);
        Assert.Equal("builtin", helpRows[0].Source);
        // Custom row named "h" must not survive either.
        Assert.DoesNotContain(rows, r => r.Name == "h");
        Assert.Equal(new[] { "deploy", "help" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void HelpModel_config_custom_description_falls_back_to_os_command()
    {
        var config = new List<CommandsConfigEntry>
        {
            new CommandsConfigEntry
            {
                Name = "deploy",
                Description = null,
                Windows = "deploy.cmd {sln}",
                NonWindows = "./deploy.sh {sln}",
            },
        };

        var deploy = Row(CommandCatalog.HelpModel(config), "deploy");
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "deploy.cmd {sln}"
            : "./deploy.sh {sln}";
        Assert.Equal(expected, deploy.Description);
    }

    [Fact]
    public void HelpModel_config_custom_description_all_null_yields_empty_string()
    {
        var config = new List<CommandsConfigEntry>
        {
            new CommandsConfigEntry { Name = "deploy", Description = null, Windows = null, NonWindows = null },
        };

        var deploy = Row(CommandCatalog.HelpModel(config), "deploy");
        Assert.Equal("", deploy.Description);
    }

    // ---- 3. SuggestionCandidates -----------------------------------------

    [Fact]
    public void SuggestionCandidates_null_is_builtin_names_plus_alias_keys()
    {
        var candidates = CommandCatalog.SuggestionCandidates(null).ToList();

        foreach (var name in new[] { "launch", "bump", "bump-commit", "build", "frontend", "clean", "help" })
            Assert.Contains(name, candidates);

        foreach (var aliasKey in new[] { "b", "c", "h", "?", "f", "v", "vc" })
            Assert.Contains(aliasKey, candidates);
    }

    [Fact]
    public void SuggestionCandidates_config_is_config_names_in_order()
    {
        var candidates = CommandCatalog.SuggestionCandidates(GoldenConfig()).ToList();
        Assert.Equal(new[] { "deploy", "build", "vbump" }, candidates);
    }

    // ---- 4. Dispatch exhaustiveness (drift guard) ------------------------

    [Fact]
    public void DispatchableCommands_is_set_equal_to_builtin_names()
    {
        var dispatch = Program.DispatchableCommands;
        var builtins = CommandCatalog.Builtins.Select(b => b.Name).ToHashSet();

        // No dispatch arm without a builtin, and no builtin without a dispatch arm.
        Assert.True(dispatch.SetEquals(builtins),
            $"dispatch-only: [{string.Join(",", dispatch.Except(builtins))}]; " +
            $"builtin-only: [{string.Join(",", builtins.Except(dispatch))}]");
    }

    // ---- 5. NeedsWorkspace partition -------------------------------------

    [Theory]
    [InlineData("launch")]
    [InlineData("bump")]
    [InlineData("bump-commit")]
    [InlineData("build")]
    public void Builtin_needs_workspace(string name)
    {
        Assert.True(CommandCatalog.Find(name)!.NeedsWorkspace);
    }

    [Theory]
    [InlineData("frontend")]
    [InlineData("clean")]
    [InlineData("help")]
    public void Builtin_does_not_need_workspace(string name)
    {
        Assert.False(CommandCatalog.Find(name)!.NeedsWorkspace);
    }
}
