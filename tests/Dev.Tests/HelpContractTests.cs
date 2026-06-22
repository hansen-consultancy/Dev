using System.Text.Json;
using Xunit;

namespace Dev.Tests;

// Golden --json help contract. The fragile invariants are KEY PRESENCE/ABSENCE
// and null-vs-absent, so we serialize the real BuildHelpData with the real
// JsonOptions and assert structurally with JsonDocument — never byte-comparing
// against the (Python-formatted) scratchpad fixtures.
public sealed class HelpContractTests
{
    private static JsonDocument Serialize(List<CommandsConfigEntry>? config)
    {
        var json = JsonSerializer.Serialize(Program.BuildHelpData(config), Program.JsonOptions);
        return JsonDocument.Parse(json);
    }

    private static bool Has(JsonElement obj, string prop) => obj.TryGetProperty(prop, out _);

    private static string[] Names(JsonElement commands) =>
        commands.EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();

    private static JsonElement Command(JsonElement commands, string name) =>
        commands.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string[] Aliases(JsonElement command) =>
        command.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!).ToArray();

    // ---- BuildHelpData(null) ---------------------------------------------

    [Fact]
    public void NoConfig_commands_is_seven_objects_in_order()
    {
        using var doc = Serialize(null);
        var commands = doc.RootElement.GetProperty("commands");

        Assert.Equal(JsonValueKind.Array, commands.ValueKind);
        Assert.Equal(7, commands.GetArrayLength());
        Assert.Equal(
            new[] { "launch", "bump", "bump-commit", "build", "frontend", "clean", "help" },
            Names(commands));
    }

    [Fact]
    public void NoConfig_launch_through_clean_have_default_bool_and_no_builtIn()
    {
        using var doc = Serialize(null);
        var commands = doc.RootElement.GetProperty("commands");

        foreach (var name in new[] { "launch", "bump", "bump-commit", "build", "frontend", "clean" })
        {
            var cmd = Command(commands, name);
            Assert.True(Has(cmd, "default"), $"{name} should have a default property");
            Assert.Contains(cmd.GetProperty("default").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
            Assert.False(Has(cmd, "builtIn"), $"{name} should NOT have a builtIn property");
            Assert.Equal("builtin", cmd.GetProperty("source").GetString());
        }
    }

    [Fact]
    public void NoConfig_launch_default_is_true_others_false()
    {
        using var doc = Serialize(null);
        var commands = doc.RootElement.GetProperty("commands");

        Assert.True(Command(commands, "launch").GetProperty("default").GetBoolean());
        foreach (var name in new[] { "bump", "bump-commit", "build", "frontend", "clean" })
            Assert.False(Command(commands, name).GetProperty("default").GetBoolean());
    }

    [Fact]
    public void NoConfig_help_row_has_no_default_and_no_builtIn()
    {
        using var doc = Serialize(null);
        var help = Command(doc.RootElement.GetProperty("commands"), "help");

        Assert.False(Has(help, "default"));
        Assert.False(Has(help, "builtIn"));
        Assert.Equal("builtin", help.GetProperty("source").GetString());
    }

    [Fact]
    public void NoConfig_aliases_are_correct()
    {
        using var doc = Serialize(null);
        var commands = doc.RootElement.GetProperty("commands");

        Assert.Empty(Aliases(Command(commands, "launch")));
        Assert.Equal(new[] { "v" }, Aliases(Command(commands, "bump")));
        Assert.Equal(new[] { "vc" }, Aliases(Command(commands, "bump-commit")));
        Assert.Equal(new[] { "b" }, Aliases(Command(commands, "build")));
        Assert.Equal(new[] { "f" }, Aliases(Command(commands, "frontend")));
        Assert.Equal(new[] { "c" }, Aliases(Command(commands, "clean")));
        Assert.Equal(new[] { "h", "?" }, Aliases(Command(commands, "help")));
    }

    [Fact]
    public void NoConfig_chaining_placeholders_and_globalFlags()
    {
        using var doc = Serialize(null);
        var root = doc.RootElement;

        var chaining = root.GetProperty("chaining");
        Assert.Equal("+", chaining.GetProperty("separator").GetString());
        Assert.Equal("dev b+f", chaining.GetProperty("example").GetString());

        Assert.Equal(
            new[] { "{sln}", "{project}", "{dir}" },
            root.GetProperty("placeholders").EnumerateArray().Select(e => e.GetString()).ToArray());

        Assert.Equal(
            new[] { "--json", "--yes" },
            root.GetProperty("globalFlags").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // ---- BuildHelpData(config) -------------------------------------------

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
    public void Config_commands_is_four_objects_in_order()
    {
        using var doc = Serialize(GoldenConfig());
        var commands = doc.RootElement.GetProperty("commands");

        Assert.Equal(4, commands.GetArrayLength());
        Assert.Equal(new[] { "deploy", "build", "vbump", "help" }, Names(commands));
    }

    [Fact]
    public void Config_deploy_emits_builtIn_as_json_null_and_default_true()
    {
        using var doc = Serialize(GoldenConfig());
        var deploy = Command(doc.RootElement.GetProperty("commands"), "deploy");

        Assert.Equal("config", deploy.GetProperty("source").GetString());
        // Contract subtlety: custom config rows emit "builtIn": null, NOT omitted.
        Assert.True(Has(deploy, "builtIn"));
        Assert.Equal(JsonValueKind.Null, deploy.GetProperty("builtIn").ValueKind);
        Assert.True(deploy.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void Config_build_is_builtin_backed()
    {
        using var doc = Serialize(GoldenConfig());
        var build = Command(doc.RootElement.GetProperty("commands"), "build");

        Assert.Equal("builtin", build.GetProperty("source").GetString());
        Assert.Equal("build", build.GetProperty("builtIn").GetString());
        Assert.Equal(new[] { "b" }, Aliases(build));
    }

    [Fact]
    public void Config_vbump_is_builtin_backed_by_bump()
    {
        using var doc = Serialize(GoldenConfig());
        var vbump = Command(doc.RootElement.GetProperty("commands"), "vbump");

        Assert.Equal("builtin", vbump.GetProperty("source").GetString());
        Assert.Equal("bump", vbump.GetProperty("builtIn").GetString());
        Assert.Equal(new[] { "v" }, Aliases(vbump));
    }

    [Fact]
    public void Config_help_row_has_no_default_and_no_builtIn()
    {
        using var doc = Serialize(GoldenConfig());
        var help = Command(doc.RootElement.GetProperty("commands"), "help");

        Assert.False(Has(help, "default"));
        Assert.False(Has(help, "builtIn"));
    }

    [Fact]
    public void Config_command_order_is_exactly_deploy_build_vbump_help()
    {
        using var doc = Serialize(GoldenConfig());
        var order = Names(doc.RootElement.GetProperty("commands"));
        Assert.Equal(new[] { "deploy", "build", "vbump", "help" }, order);
    }
}
