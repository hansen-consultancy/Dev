// 'Dev' tool can be used to do some development tasks based on the current directory.

using System.Diagnostics;
using System.Runtime.InteropServices;
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

if (configCommands is not null)
{
    var gate = TrustGateFactory.ForProduction(envelope);
    if (!gate.Authorize(configFile, configCommands, new TrustFlags(ctx.AutoYes, ctx.JsonMode)))
        return Finish(envelope, ctx, runWatch);
}

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
        Console.Out.WriteLine(JsonSerializer.Serialize(envelope, Program.JsonOptions));
    return envelope.ExitCode;
}

static (string Command, string? Alias) ResolveAlias(string input) => CommandCatalog.Resolve(input);

static StepResult ExecuteCommand(
    string command, string? alias, string[] commandArgs, string path,
    Workspace? workspace, List<CommandsConfigEntry>? configCommands,
    RunContext ctx)
{
    var step = new StepResult { Command = command, Alias = alias, Args = commandArgs };
    var watch = Stopwatch.StartNew();

    try
    {
        // help short-circuits before the commands.json unknown-command rejection so
        // `dev help` works regardless of whether commands.json declares it.
        if (command is "help") return RunHelp(configCommands, step, ctx);

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
                // When a commands.json exists, builtins it doesn't declare are
                // also "Unknown command", so suggest only config command names.
                var suggestion = CommandCatalog.Suggest(command, CommandCatalog.SuggestionCandidates(configCommands));
                if (suggestion is not null)
                    ctx.Log($"[yellow]Did you mean {CommandCatalog.DescribeSuggestion(suggestion).EscapeMarkup()}?[/]");
                step.Status = "failed";
                step.ExitCode = 3;
                step.Error = new StepError { Code = "unknown_command", Message = $"Unknown command: {command}", Suggestion = suggestion };
                return step;
            }
        }

        var spec = CommandCatalog.Find(command);
        if (spec is null)
        {
            ctx.Log($"[red]Unknown command: {command}[/]");
            var suggestion = CommandCatalog.Suggest(command, CommandCatalog.SuggestionCandidates(configCommands));
            if (suggestion is not null)
                ctx.Log($"[yellow]Did you mean {CommandCatalog.DescribeSuggestion(suggestion).EscapeMarkup()}?[/]");
            step.Status = "failed";
            step.ExitCode = 3;
            step.Error = new StepError { Code = "unknown_command", Message = $"Unknown command: {command}", Suggestion = suggestion };
            return step;
        }

        if (spec.NeedsWorkspace && workspace is null)
        {
            ctx.Log("[red]No .sln, .slnx or .csproj file found in the current directory or src/ folder.[/]");
            step.Status = "failed";
            step.ExitCode = 2;
            step.Error = new StepError { Code = "no_project_found", Message = "No .sln, .slnx or .csproj file found in the current directory or src/ folder." };
            return step;
        }

        // Dispatch on the canonical name (spec.Name), not the raw input.
        return spec.Name switch
        {
            "launch" => RunLaunch(workspace!, step, ctx),
            "bump" => RunBump(workspace!, commandArgs, step, ctx),
            "bump-commit" => RunBumpCommit(workspace!, commandArgs, step, ctx),
            "build" => RunBuild(workspace!.BuildTarget, step, ctx),
            "frontend" => RunFrontend(path, step, ctx),
            "clean" => RunClean(path, step, ctx),
            "help" => RunHelp(configCommands, step, ctx), // reached only via a commands.json entry whose "builtIn" is "help"; the bare help command short-circuits above. Kept so the switch stays exhaustive over Builtins.
            _ => throw new UnreachableException(),
        };
    }
    finally
    {
        step.DurationMs = watch.ElapsedMilliseconds;
    }
}

static StepResult RunHelp(List<CommandsConfigEntry>? configCommands, StepResult step, RunContext ctx)
{
    step.Data = Program.BuildHelpData(configCommands);
    if (!ctx.JsonMode) RenderHelpTable(configCommands);
    return step;
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

    if (PlaceholderGuard.FindUnsafe(cmdLine, workspace?.SolutionPath, workspace?.ProjectPath, path) is { } unsafeHit)
    {
        var message = $"Refusing to run '{cfg.Name}': {unsafeHit.Placeholder} resolves to a path containing '{unsafeHit.Character}', a shell metacharacter that could be interpreted as a command.";
        ctx.Log($"[red]{message.EscapeMarkup()}[/]");
        step.Status = "failed";
        step.ExitCode = 5;
        step.Error = new StepError
        {
            Code = "placeholder_unsafe",
            Message = message,
            Detail = new Dictionary<string, object?>
            {
                ["placeholder"] = unsafeHit.Placeholder,
                ["character"] = unsafeHit.Character.ToString(),
            },
        };
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

    var bo = FrontendBuilder.Run(path, new BuildOptions(FrontendImage, Runner, ctx));

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

static void RenderHelpTable(List<CommandsConfigEntry>? configCommands)
{
    var table = new Table()
        .Title("[yellow]Dev Tool Commands[/]")
        .AddColumn(new TableColumn("[green]Command[/]").LeftAligned())
        .AddColumn(new TableColumn("[blue]Description[/]").LeftAligned());

    foreach (var r in CommandCatalog.HelpModel(configCommands))
    {
        // builtins enumeration + synthetic help row carry alias/arg hints; bare config rows don't.
        var decorate = configCommands is null || r.IsDefault is null;
        var alias = decorate && r.Aliases.Length > 0 ? $" ({string.Join(", ", r.Aliases)})" : "";
        var def = r.IsDefault == true ? " (default)" : "";
        var arg = decorate && r.ArgSyntax is not null ? " " + r.ArgSyntax : "";
        table.AddRow($"{r.Name}{alias}{def}{arg}".EscapeMarkup(), r.Description.EscapeMarkup());
    }

    table.AddEmptyRow();
    table.AddRow("[dim]Combine commands with '+'[/]", "[dim]Example: dev b+f (build then frontend)[/]");
    table.AddRow("[dim]Global flags[/]", "[dim]--json (machine output)  --yes (auto-accept prompts)[/]");

    AnsiConsole.Write(table);
}

partial class Program
{
    internal static IProcessRunner Runner { get; set; } = new RealProcessRunner();
    internal static IFrontendEnvironment FrontendEnv { get; set; } = new FrontendEnvironment();
    internal static IFrontendBuild FrontendBuilder { get; set; } = new FrontendBuild();

    // Shared so the --json help golden test can serialize BuildHelpData with the
    // exact options the tool emits with.
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Mirror of the dispatch arms in ExecuteCommand's switch. Kept in sync by hand;
    // CommandCatalog<->dispatch drift is turned into a test failure by the exhaustiveness test.
    internal static readonly IReadOnlySet<string> DispatchableCommands =
        new HashSet<string>(StringComparer.Ordinal) { "launch", "bump", "bump-commit", "build", "frontend", "clean", "help" };

    // The --json help payload. Maps CommandCatalog.HelpModel; dictionary keys are
    // added conditionally because WhenWritingNull does NOT drop null *values* inside
    // a Dictionary<string, object?>, only null properties on typed objects.
    internal static object BuildHelpData(List<CommandsConfigEntry>? config)
    {
        var commands = CommandCatalog.HelpModel(config).Select(r =>
        {
            var d = new Dictionary<string, object?>
            {
                ["name"] = r.Name,
                ["aliases"] = r.Aliases,
                ["description"] = r.Description,
            };
            if (r.IsDefault is not null) d["default"] = r.IsDefault.Value;   // absent on help row
            d["source"] = r.Source;
            if (config is not null && r.IsDefault is not null) d["builtIn"] = r.BuiltIn;  // present (maybe null) for config rows only
            return d;
        }).ToList();
        return new Dictionary<string, object?>
        {
            ["commands"] = commands,
            ["chaining"] = new Dictionary<string, object?> { ["separator"] = "+", ["example"] = "dev b+f" },
            ["placeholders"] = new[] { "{sln}", "{project}", "{dir}" },
            ["globalFlags"] = new[] { "--json", "--yes" },
        };
    }

    // The frontend builder is pinned by digest, not floated on :latest, so a
    // repointed or compromised tag cannot reach a user's source tree (mounted at
    // /src). The :latest tag is retained only to document which tag this digest
    // was resolved from. To ship a new builder: re-resolve the digest
    //   docker buildx imagetools inspect ghcr.io/stevehansen/vidyano-frontend-builder:latest
    // update the constant below, and release a new version of the tool.
    internal const string FrontendImage =
        "ghcr.io/stevehansen/vidyano-frontend-builder:latest@sha256:b89acec0cfe69c5c9069e6201fa344428ca199a005e6bd74feabd6387c93614f";
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
    public string? Suggestion { get; set; }
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
