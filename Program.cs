// 'Dev' tool can be used to do some development tasks based on the current directory.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dev;
using Spectre.Console;

var argList = args.ToList();
var jsonMode = argList.Remove("--json");
var autoYes = argList.Remove("--yes") || argList.Remove("-y");
args = argList.ToArray();

var ctx = new RunContext { JsonMode = jsonMode, AutoYes = autoYes };
var runWatch = Stopwatch.StartNew();

var informationalVersion = ThisAssembly.Info.InformationalVersion.Split('+', 2);
var toolVersion = informationalVersion[0];
var toolCommit = informationalVersion.Length > 1
    ? (informationalVersion[1].Length >= 7 ? informationalVersion[1][..7] : informationalVersion[1])
    : "";

if (!jsonMode)
    AnsiConsole.MarkupLine($"[bold green]{ThisAssembly.Info.Product}[/] v[green]{toolVersion}[/]+{toolCommit}");

var path = Environment.CurrentDirectory;

var envelope = new Envelope
{
    Version = toolVersion,
    Commit = toolCommit,
    Cwd = path,
};

var configFile = Path.Combine(path, "commands.json");
List<CommandsConfigEntry>? configCommands = null;
CommandsConfigEntry? defaultConfig = null;
if (File.Exists(configFile))
{
    try
    {
        configCommands = JsonSerializer.Deserialize<List<CommandsConfigEntry>>(File.ReadAllText(configFile));
        defaultConfig = configCommands?.FirstOrDefault(c => c.Default);
    }
    catch (Exception ex)
    {
        if (!jsonMode)
        {
            AnsiConsole.MarkupLine("[red]Error reading commands.json:[/]");
            AnsiConsole.WriteException(ex);
        }
        envelope.Error = new StepError { Code = "config_parse_error", Message = "Error reading commands.json", Detail = ex.Message };
        envelope.ExitCode = 4;
        return Finish(envelope, ctx, runWatch);
    }
}

if (configCommands is not null && !VerifyCommandsTrust(configFile, configCommands, ctx, envelope))
    return Finish(envelope, ctx, runWatch);

Workspace? workspace;
try
{
    workspace = Workspace.Discover(path, ctx.Log);
}
catch (WorkspaceDiscoveryException ex)
{
    if (!jsonMode)
    {
        AnsiConsole.MarkupLine($"[red]{ex.Message}:[/]");
        if (ex.InnerException is not null) AnsiConsole.WriteException(ex.InnerException);
    }
    envelope.Error = new StepError { Code = ex.Code, Message = ex.Message, Detail = ex.InnerException?.Message };
    envelope.ExitCode = 4;
    return Finish(envelope, ctx, runWatch);
}
envelope.Solution = workspace?.SolutionPath;
envelope.Project = workspace?.ProjectPath;

string commandInput;
if (args.Length > 0)
    commandInput = args[0];
else if (configCommands is not null)
    commandInput = defaultConfig?.Name ?? "help";
else
    commandInput = "launch";

var commandTokens = commandInput.Split('+').Select(c => c.Trim()).ToArray();

for (int i = 0; i < commandTokens.Length; i++)
{
    var (cmd, alias) = ResolveAlias(commandTokens[i]);

    if (!jsonMode && commandTokens.Length > 1)
        AnsiConsole.MarkupLine($"[cyan]Executing command {i + 1}/{commandTokens.Length}: {cmd}[/]");

    var commandArgs = i == 0 ? args.Skip(1).ToArray() : Array.Empty<string>();
    var step = ExecuteCommand(cmd, alias, commandArgs, path, workspace, configCommands, ctx);
    envelope.Steps.Add(step);

    if (step.Status == "failed")
    {
        if (!jsonMode && commandTokens.Length > 1)
            AnsiConsole.MarkupLine($"[red]Command '{cmd}' failed. Stopping execution.[/]");

        for (int j = i + 1; j < commandTokens.Length; j++)
        {
            var (skipped, skippedAlias) = ResolveAlias(commandTokens[j]);
            envelope.Steps.Add(new StepResult { Command = skipped, Alias = skippedAlias, Status = "skipped" });
        }
        break;
    }
}

var failingStep = envelope.Steps.FirstOrDefault(s => s.Status == "failed");
envelope.Ok = failingStep == null;
envelope.ExitCode = failingStep?.ExitCode ?? 0;
return Finish(envelope, ctx, runWatch);

// ---------------- helpers ----------------

static int Finish(Envelope envelope, RunContext ctx, Stopwatch watch)
{
    envelope.DurationMs = watch.ElapsedMilliseconds;
    if (ctx.JsonMode)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(envelope, opts));
    }
    return envelope.ExitCode;
}

static (string Command, string? Alias) ResolveAlias(string input) => input switch
{
    "b" => ("build", "b"),
    "h" or "?" => ("help", input),
    "f" => ("frontend", "f"),
    "v" => ("bump", "v"),
    "vc" => ("bump-commit", "vc"),
    "c" => ("clean", "c"),
    _ => (input, null),
};

static StepResult ExecuteCommand(
    string command, string? alias, string[] commandArgs, string path,
    Workspace? workspace, List<CommandsConfigEntry>? configCommands,
    RunContext ctx)
{
    var step = new StepResult { Command = command, Alias = alias, Args = commandArgs };
    var watch = Stopwatch.StartNew();

    try
    {
        if (command is "help")
        {
            step.Data = BuildHelpData(configCommands);
            if (!ctx.JsonMode) RenderHelpTable(configCommands);
            return step;
        }

        if (configCommands is not null)
        {
            var cfgCmd = configCommands.FirstOrDefault(c => string.Equals(c.Name, command, StringComparison.OrdinalIgnoreCase));
            if (cfgCmd is not null)
            {
                if (string.IsNullOrEmpty(cfgCmd.BuiltIn))
                    return RunCustomCommand(cfgCmd, workspace, path, step, ctx);

                command = cfgCmd.BuiltIn;
                step.Command = command;
            }
            else
            {
                ctx.Log($"[red]Unknown command: {command}[/]");
                step.Status = "failed";
                step.ExitCode = 3;
                step.Error = new StepError { Code = "unknown_command", Message = $"Unknown command: {command}" };
                return step;
            }
        }

        if (command is "frontend") return RunFrontend(path, step, ctx);
        if (command is "clean") return RunClean(path, step, ctx);

        if (workspace is null)
        {
            ctx.Log("[red]No .sln, .slnx or .csproj file found in the current directory or src/ folder.[/]");
            step.Status = "failed";
            step.ExitCode = 2;
            step.Error = new StepError { Code = "no_project_found", Message = "No .sln, .slnx or .csproj file found in the current directory or src/ folder." };
            return step;
        }

        switch (command)
        {
            case "bump": return RunBump(workspace, commandArgs, step, ctx);
            case "bump-commit": return RunBumpCommit(workspace, commandArgs, step, ctx);
            case "build": return RunBuild(workspace.BuildTarget, step, ctx);
            case "launch": return RunLaunch(workspace, step, ctx);
            default:
                ctx.Log($"[red]Unknown command: {command}[/]");
                step.Status = "failed";
                step.ExitCode = 3;
                step.Error = new StepError { Code = "unknown_command", Message = $"Unknown command: {command}" };
                return step;
        }
    }
    finally
    {
        step.DurationMs = watch.ElapsedMilliseconds;
    }
}

static StepResult RunCustomCommand(CommandsConfigEntry cfg, Workspace? workspace, string path, StepResult step, RunContext ctx)
{
    var cmdLine = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? cfg.Windows : cfg.NonWindows;
    if (string.IsNullOrWhiteSpace(cmdLine))
    {
        ctx.Log("[red]No command defined for the current environment.[/]");
        step.Status = "failed";
        step.ExitCode = 6;
        step.Error = new StepError { Code = "platform_unsupported", Message = "No command defined for the current environment." };
        return step;
    }

    cmdLine = ReplaceVariables(cmdLine, workspace?.SolutionPath, workspace?.ProjectPath, path);
    var spec = ProcSpec.Shell(cmdLine);
    var result = Runner.Run(spec, ctx);

    step.Data = new Dictionary<string, object?>
    {
        ["source"] = "config",
        ["name"] = cfg.Name,
        ["commandLine"] = cmdLine,
        ["shell"] = spec.DisplayShell,
        ["exitCode"] = result.ExitCode,
        ["stderrTail"] = result.Ok ? null : result.StderrTail,
        ["stdoutTail"] = result.Ok ? null : result.StdoutTail,
    };

    if (!result.Ok)
    {
        step.Status = "failed";
        step.ExitCode = 1;
        step.Error = new StepError { Code = "custom_command_failed", Message = $"Custom command '{cfg.Name}' exited with code {result.ExitCode}" };
    }
    return step;
}

static StepResult RunBump(Workspace workspace, string[] commandArgs, StepResult step, RunContext ctx)
{
    var part = commandArgs.Length > 0 ? commandArgs[0] : "minor";
    var outcome = ExecuteBump(workspace, part, new BumpOptions(), ctx);
    step.Data = new Dictionary<string, object?> { ["part"] = part, ["projects"] = outcome.Projects };
    return step;
}

static StepResult RunBumpCommit(Workspace workspace, string[] commandArgs, StepResult step, RunContext ctx)
{
    var part = commandArgs.Length > 0 ? commandArgs[0] : "minor";
    var outcome = ExecuteBump(workspace, part, BumpOptions.CommitAndTag(), ctx);

    var gitInfo = new Dictionary<string, object?>();
    if (outcome.Git.Committed && outcome.Git.Tag is not null)
    {
        gitInfo["committed"] = true;
        gitInfo["tag"] = outcome.Git.Tag;
        gitInfo["message"] = outcome.Git.Message;
    }
    else
    {
        gitInfo["committed"] = outcome.Git.Committed;
        gitInfo["reason"] = outcome.Git.Reason;
        if (outcome.Git.Reason is "git_commit_failed" or "git_tag_failed")
        {
            gitInfo["commitExitCode"] = outcome.Git.CommitExit;
            gitInfo["tagExitCode"] = outcome.Git.TagExit;
            gitInfo["message"] = outcome.Git.Message;
            step.Status = "failed";
            step.ExitCode = 1;
            step.Error = new StepError
            {
                Code = outcome.Git.Reason,
                Message = outcome.Git.Reason == "git_commit_failed" ? "git commit failed" : "git tag failed",
                Detail = new Dictionary<string, object?>
                {
                    ["commitExitCode"] = outcome.Git.CommitExit,
                    ["tagExitCode"] = outcome.Git.TagExit,
                    ["stderrTail"] = outcome.Git.StderrTail,
                    ["stdoutTail"] = outcome.Git.StdoutTail,
                },
            };
        }
    }

    step.Data = new Dictionary<string, object?> { ["part"] = part, ["projects"] = outcome.Projects, ["git"] = gitInfo };
    return step;
}

static BumpOutcome ExecuteBump(Workspace workspace, string part, BumpOptions options, RunContext ctx)
{
    var pipeline = new BumpPipeline(new CsprojVersionFile(), new ProcessGitPort(Runner, ctx), ctx.Log);
    return pipeline.Execute(new BumpPlan(workspace.EnumerateBumpTargets(), new BumpSpec(SemVer.ParsePart(part)), options));
}

static StepResult RunBuild(BuildTarget buildTarget, StepResult step, RunContext ctx)
{
    var (builder, result) = BuildSolutionOrProject(buildTarget.Path, ctx);
    step.Data = new Dictionary<string, object?>
    {
        ["target"] = buildTarget.Path,
        ["builder"] = builder,
        ["builderExitCode"] = result.ExitCode,
        ["stderrTail"] = result.Ok ? null : result.StderrTail,
        ["stdoutTail"] = result.Ok ? null : result.StdoutTail,
    };
    if (!result.Ok)
    {
        step.Status = "failed";
        step.ExitCode = 1;
        step.Error = new StepError { Code = "builder_failed", Message = $"Builder exited with code {result.ExitCode}" };
    }
    return step;
}

static StepResult RunLaunch(Workspace workspace, StepResult step, RunContext ctx)
{
    if (workspace.BuildTarget.Kind == BuildTargetKind.Solution)
    {
        var slnFile = workspace.SolutionPath!;
        ctx.Log($"[green]Opening[/] {slnFile} [green]in default IDE...[/]");
        Process.Start(new ProcessStartInfo(slnFile) { UseShellExecute = true });
        step.Data = new Dictionary<string, object?> { ["target"] = slnFile, ["opener"] = "shellExecute" };
    }
    else
    {
        var csprojFile = workspace.ProjectPath!;
        ctx.Log($"[green]Opening[/] {csprojFile} [green]in Visual Studio Code...[/]");
        Process.Start("code", csprojFile);
        step.Data = new Dictionary<string, object?> { ["target"] = csprojFile, ["opener"] = "code" };
    }
    return step;
}

static StepResult RunClean(string path, StepResult step, RunContext ctx)
{
    ctx.Log("[green]Cleaning current directory...[/]");
    var foldersToDelete = new[] { "bin", "obj", "tmp-build", "bin-windows", "bin-linux", "obj-windows", "obj-linux" };
    var deleted = new List<string>();
    var failed = new List<Dictionary<string, object?>>();
    var allDirectories = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
        .Where(d => !d.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
        .Where(d => foldersToDelete.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase));

    foreach (var folder in allDirectories)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                ctx.Log($"[red]Deleting[/] {folder} [red]folder...[/]");
                Directory.Delete(folder, recursive: true);
                deleted.Add(folder);
            }
        }
        catch (Exception ex)
        {
            if (!ctx.JsonMode) AnsiConsole.WriteException(ex, ExceptionFormats.ShowLinks);
            failed.Add(new Dictionary<string, object?> { ["path"] = folder, ["reason"] = ex.Message });
        }
    }
    step.Data = new Dictionary<string, object?> { ["deleted"] = deleted, ["failed"] = failed };
    return step;
}

static StepResult RunFrontend(string path, StepResult step, RunContext ctx)
{
    ctx.Log("[green]Running Vidyano frontend builder...[/]");

    var scaffold = FrontendEnv.Prepare(path, new ScaffoldOptions(
        ctx.AutoYes, ctx.JsonMode, DryRun: false,
        new AnsiConsolePrompter(), new RealFileSystem(), ctx.Log));

    foreach (var w in scaffold.Warnings) step.Warnings.Add(w);

    if (scaffold.State is ScaffoldState.BlockedByPrompt or ScaffoldState.UserDeclined)
    {
        step.Status = "failed";
        step.ExitCode = scaffold.State == ScaffoldState.BlockedByPrompt ? 5 : 1;
        step.Error = new StepError { Code = scaffold.BlockingCode!, Message = scaffold.BlockingMessage! };
        step.Data = new Dictionary<string, object?> { ["mutations"] = scaffold.Mutations };
        return step;
    }

    var bo = FrontendBuilder.Run(path, new BuildOptions(
        "ghcr.io/stevehansen/vidyano-frontend-builder:latest", Runner, ctx));

    step.Data = new Dictionary<string, object?>
    {
        ["mutations"] = scaffold.Mutations,
        ["docker"] = new Dictionary<string, object?>
        {
            ["image"] = bo.Image,
            ["exitCode"] = bo.ExitCode,
            ["stderrTail"] = bo.ExitCode != 0 ? bo.StderrTail : null,
            ["stdoutTail"] = bo.ExitCode != 0 ? bo.StdoutTail : null,
        },
    };
    if (bo.ExitCode != 0)
    {
        step.Status = "failed";
        step.ExitCode = 1;
        step.Error = new StepError { Code = "docker_failed", Message = $"Docker exited with code {bo.ExitCode}" };
    }
    return step;
}

static (string Builder, ProcRunResult Result) BuildSolutionOrProject(string buildTarget, RunContext ctx)
{
    var buildFile = Path.Combine(Path.GetDirectoryName(buildTarget) ?? ".", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "build.cmd" : "build.sh");
    if (File.Exists(buildFile))
    {
        ctx.Log($"[green]Building[/] {Path.GetFileName(buildTarget)} [green]using[/] {Path.GetFileName(buildFile)}[green]...[/]");
        return (Path.GetFileName(buildFile), Runner.Run(ProcSpec.Exec(buildFile, ""), ctx));
    }
    ctx.Log($"[green]Building[/] {Path.GetFileName(buildTarget)} [green]in Release mode...[/]");
    return ("dotnet", Runner.Run(ProcSpec.Exec("dotnet", $"build \"{buildTarget}\" -c Release"), ctx));
}

static string ReplaceVariables(string command, string? slnFile, string? csprojFile, string path)
{
    return command
        .Replace("{sln}", slnFile ?? string.Empty)
        .Replace("{project}", csprojFile ?? string.Empty)
        .Replace("{dir}", path);
}

static object BuildHelpData(List<CommandsConfigEntry>? configCommands)
{
    var aliases = new Dictionary<string, string[]>
    {
        ["build"] = new[] { "b" },
        ["help"] = new[] { "h", "?" },
        ["frontend"] = new[] { "f" },
        ["bump"] = new[] { "v" },
        ["bump-commit"] = new[] { "vc" },
        ["clean"] = new[] { "c" },
    };

    var commands = new List<Dictionary<string, object?>>();
    if (configCommands is not null)
    {
        foreach (var c in configCommands)
        {
            if (c.Name is "help" or "h") continue;
            var desc = !string.IsNullOrEmpty(c.BuiltIn)
                ? BuiltinDescription(c.BuiltIn)
                : c.Description ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? c.Windows : c.NonWindows) ?? "";
            commands.Add(new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["aliases"] = !string.IsNullOrEmpty(c.BuiltIn) && aliases.TryGetValue(c.BuiltIn, out var al) ? al : Array.Empty<string>(),
                ["description"] = desc,
                ["default"] = c.Default,
                ["source"] = !string.IsNullOrEmpty(c.BuiltIn) ? "builtin" : "config",
                ["builtIn"] = c.BuiltIn,
            });
        }
    }
    else
    {
        foreach (var name in new[] { "launch", "bump", "bump-commit", "build", "frontend", "clean" })
        {
            commands.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["aliases"] = aliases.TryGetValue(name, out var al) ? al : Array.Empty<string>(),
                ["description"] = BuiltinDescription(name),
                ["default"] = name == "launch",
                ["source"] = "builtin",
            });
        }
    }
    commands.Add(new Dictionary<string, object?>
    {
        ["name"] = "help",
        ["aliases"] = aliases["help"],
        ["description"] = "Displays this help message.",
        ["source"] = "builtin",
    });

    return new Dictionary<string, object?>
    {
        ["commands"] = commands,
        ["chaining"] = new Dictionary<string, object?> { ["separator"] = "+", ["example"] = "dev b+f" },
        ["placeholders"] = new[] { "{sln}", "{project}", "{dir}" },
        ["globalFlags"] = new[] { "--json", "--yes" },
    };
}

static string BuiltinDescription(string name) => name switch
{
    "launch" => "Launches the current solution in your default IDE or project in Visual Studio Code.",
    "bump" => "Bumps the version of all projects in the current solution or the current project. Defaults to minor.",
    "bump-commit" => "Bumps the version and commits/tag the change in the current solution or project. Defaults to minor.",
    "build" => "Builds the current solution or project in Release mode.",
    "frontend" => "Runs the Vidyano frontend builder in the current directory.",
    "clean" => "Clean the current folder by removing bin, obj, tmp-build, bin-windows, bin-linux, obj-windows, obj-linux folders.",
    _ => name,
};

static void RenderHelpTable(List<CommandsConfigEntry>? configCommands)
{
    var table = new Table()
        .Title("[yellow]Dev Tool Commands[/]")
        .AddColumn(new TableColumn("[green]Command[/]").LeftAligned())
        .AddColumn(new TableColumn("[blue]Description[/]").LeftAligned());

    if (configCommands is not null)
    {
        foreach (var c in configCommands)
        {
            if (c.Name is "help" or "h") continue;
            var desc = !string.IsNullOrEmpty(c.BuiltIn) ? BuiltinDescription(c.BuiltIn)
                : c.Description ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? c.Windows : c.NonWindows) ?? string.Empty;
            table.AddRow($"{c.Name}{(c.Default ? " (default)" : string.Empty)}".EscapeMarkup(), desc.EscapeMarkup());
        }
    }
    else
    {
        table.AddRow("launch (default)", BuiltinDescription("launch"));
        table.AddRow("bump (v) [major|minor|patch|revision]".EscapeMarkup(), BuiltinDescription("bump"));
        table.AddRow("bump-commit (vc) [major|minor|patch|revision]".EscapeMarkup(), BuiltinDescription("bump-commit"));
        table.AddRow("build (b)", BuiltinDescription("build"));
        table.AddRow("frontend (f)", BuiltinDescription("frontend"));
        table.AddRow("clean (c)", BuiltinDescription("clean"));
    }

    table.AddRow("help (h)", "Displays this help message.");
    table.AddEmptyRow();
    table.AddRow("[dim]Combine commands with '+'[/]", "[dim]Example: dev b+f (build then frontend)[/]");
    table.AddRow("[dim]Global flags[/]", "[dim]--json (machine output)  --yes (auto-accept prompts)[/]");

    AnsiConsole.Write(table);
}

static bool VerifyCommandsTrust(string configFilePath, List<CommandsConfigEntry> commands, RunContext ctx, Envelope envelope)
{
    var content = File.ReadAllBytes(configFilePath);
    var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    var fullPath = Path.GetFullPath(configFilePath);

    var trustDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hc-dev");
    var trustFile = Path.Combine(trustDir, "trust.json");

    TrustStore store;
    try
    {
        if (File.Exists(trustFile))
            store = JsonSerializer.Deserialize<TrustStore>(File.ReadAllText(trustFile)) ?? new TrustStore();
        else
            store = new TrustStore();
    }
    catch
    {
        store = new TrustStore();
    }

    if (store.TrustedConfigs.TryGetValue(fullPath, out var trustedHash) && trustedHash == hash)
        return true;

    var isModified = store.TrustedConfigs.ContainsKey(fullPath);

    if (!ctx.JsonMode)
    {
        AnsiConsole.MarkupLine(isModified
            ? "[yellow]Warning:[/] The commands.json in this directory has been modified since you last approved it."
            : "[yellow]Warning:[/] A custom commands.json was found in this directory.");
        DisplayCommandSummary(commands);
    }

    bool accepted;
    if (ctx.AutoYes)
    {
        accepted = true;
    }
    else if (ctx.JsonMode)
    {
        envelope.Error = new StepError
        {
            Code = "interaction_required",
            Message = isModified
                ? "commands.json has been modified since it was trusted; re-run with --yes to re-approve."
                : "Untrusted commands.json; re-run with --yes to approve.",
        };
        envelope.ExitCode = 5;
        return false;
    }
    else
    {
        accepted = AnsiConsole.Prompt(new ConfirmationPrompt("Do you want to trust this configuration?") { DefaultValue = false });
    }

    if (!accepted)
    {
        envelope.Error = new StepError { Code = "trust_rejected", Message = "User rejected commands.json trust." };
        envelope.ExitCode = 5;
        return false;
    }

    store.TrustedConfigs[fullPath] = hash;
    Directory.CreateDirectory(trustDir);
    File.WriteAllText(trustFile, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
    return true;
}

static void DisplayCommandSummary(List<CommandsConfigEntry> commands)
{
    AnsiConsole.MarkupLine("\nThis configuration replaces the default commands with:");
    foreach (var cmd in commands)
    {
        var label = $"[cyan]{cmd.Name}[/]";
        if (cmd.Default) label += " (default)";

        string detail;
        if (!string.IsNullOrEmpty(cmd.BuiltIn))
            detail = $"(built-in) {cmd.BuiltIn}";
        else
            detail = (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? cmd.Windows : cmd.NonWindows) ?? string.Empty;

        AnsiConsole.MarkupLine($"  - {label}: {detail.EscapeMarkup()}");
    }
    AnsiConsole.WriteLine();
}

partial class Program
{
    internal static IProcessRunner Runner { get; set; } = new RealProcessRunner();
    internal static IFrontendEnvironment FrontendEnv { get; set; } = new FrontendEnvironment();
    internal static IFrontendBuild FrontendBuilder { get; set; } = new FrontendBuild();
}

internal sealed class CommandsConfigEntry
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Default { get; set; }
    public string? BuiltIn { get; set; }
    public string? Windows { get; set; }
    public string? NonWindows { get; set; }
}

internal sealed class DevConfig
{
    public List<string>? IgnoreProjects { get; set; }
}

internal sealed class TrustStore
{
    public Dictionary<string, string> TrustedConfigs { get; set; } = new();
}

internal sealed class RunContext
{
    public bool JsonMode { get; init; }
    public bool AutoYes { get; init; }
    public void Log(string markup)
    {
        if (!JsonMode) AnsiConsole.MarkupLine(markup);
    }
}

internal sealed class StepResult
{
    public string Command { get; set; } = "";
    public string? Alias { get; set; }
    public string[] Args { get; set; } = Array.Empty<string>();
    public string Status { get; set; } = "ok";
    public int ExitCode { get; set; }
    public long DurationMs { get; set; }
    public List<string> Warnings { get; set; } = new();
    public object? Data { get; set; }
    public StepError? Error { get; set; }
}

internal sealed class StepError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public object? Detail { get; set; }
}

internal sealed class Envelope
{
    public string Tool { get; set; } = "hc-dev";
    public string Version { get; set; } = "";
    public string Commit { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Solution { get; set; }
    public string? Project { get; set; }
    public bool Ok { get; set; }
    public int ExitCode { get; set; }
    public long DurationMs { get; set; }
    public List<StepResult> Steps { get; set; } = new();
    public StepError? Error { get; set; }
}
