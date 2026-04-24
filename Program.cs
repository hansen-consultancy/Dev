// 'Dev' tool can be used to do some development tasks based on the current directory.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dev;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Spectre.Console;

const int StderrTailLines = 50;

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

var devConfigFile = Path.Combine(path, "dev.json");
if (!File.Exists(devConfigFile))
{
    var parentPath = Directory.GetParent(path)?.FullName;
    if (parentPath != null)
    {
        var parentConfigFile = Path.Combine(parentPath, "dev.json");
        if (File.Exists(parentConfigFile))
            devConfigFile = parentConfigFile;
    }
}

DevConfig? devConfig = null;
if (File.Exists(devConfigFile))
{
    try
    {
        devConfig = JsonSerializer.Deserialize<DevConfig>(File.ReadAllText(devConfigFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex)
    {
        if (!jsonMode)
        {
            AnsiConsole.MarkupLine("[red]Error reading dev.json:[/]");
            AnsiConsole.WriteException(ex);
        }
        envelope.Error = new StepError { Code = "config_parse_error", Message = "Error reading dev.json", Detail = ex.Message };
        envelope.ExitCode = 4;
        return Finish(envelope, ctx, runWatch);
    }
}

var slnFile = FindSolutionFile(path);
var csprojFile = FindProjectFile(path);
if (slnFile == null && csprojFile == null)
{
    var srcDir = Path.Combine(path, "src");
    if (Directory.Exists(srcDir))
    {
        slnFile = FindSolutionFile(srcDir);
        csprojFile = FindProjectFile(srcDir);
    }
}
envelope.Solution = slnFile;
envelope.Project = csprojFile;

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
    var step = ExecuteCommand(cmd, alias, commandArgs, path, slnFile, csprojFile, configCommands, devConfig, ctx);
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
    string? slnFile, string? csprojFile,
    List<CommandsConfigEntry>? configCommands, DevConfig? devConfig,
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
                    return RunCustomCommand(cfgCmd, slnFile, csprojFile, path, step, ctx);

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

        if (slnFile == null && csprojFile == null)
        {
            ctx.Log("[red]No .sln, .slnx or .csproj file found in the current directory or src/ folder.[/]");
            step.Status = "failed";
            step.ExitCode = 2;
            step.Error = new StepError { Code = "no_project_found", Message = "No .sln, .slnx or .csproj file found in the current directory or src/ folder." };
            return step;
        }

        var buildTarget = slnFile ?? csprojFile!;

        switch (command)
        {
            case "bump": return RunBump(slnFile, csprojFile, commandArgs, devConfig, step, ctx);
            case "bump-commit": return RunBumpCommit(slnFile, csprojFile, commandArgs, devConfig, step, ctx);
            case "build": return RunBuild(buildTarget, step, ctx);
            case "launch": return RunLaunch(slnFile, csprojFile, step, ctx);
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

static StepResult RunCustomCommand(CommandsConfigEntry cfg, string? slnFile, string? csprojFile, string path, StepResult step, RunContext ctx)
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

    cmdLine = ReplaceVariables(cmdLine, slnFile, csprojFile, path);
    var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    var shell = isWindows ? "cmd" : "bash";
    var (file, procArgs) = isWindows ? ("cmd.exe", $"/c {cmdLine}") : ("bash", $"-c \"{cmdLine}\"");
    var (exit, errTail, outTail) = RunProcess(file, procArgs, ctx);

    step.Data = new Dictionary<string, object?>
    {
        ["source"] = "config",
        ["name"] = cfg.Name,
        ["commandLine"] = cmdLine,
        ["shell"] = shell,
        ["exitCode"] = exit,
        ["stderrTail"] = exit != 0 ? errTail : null,
        ["stdoutTail"] = exit != 0 ? outTail : null,
    };

    if (exit != 0)
    {
        step.Status = "failed";
        step.ExitCode = 1;
        step.Error = new StepError { Code = "custom_command_failed", Message = $"Custom command '{cfg.Name}' exited with code {exit}" };
    }
    return step;
}

static StepResult RunBump(string? slnFile, string? csprojFile, string[] commandArgs, DevConfig? devConfig, StepResult step, RunContext ctx)
{
    var part = commandArgs.Length > 0 ? commandArgs[0] : "minor";
    var results = new List<ProjectBumpResult>();
    if (slnFile != null)
    {
        foreach (var c in GetProjectPaths(slnFile, devConfig?.IgnoreProjects, ctx))
        {
            if (!c.Include) { results.Add(new ProjectBumpResult(c.Path, null, null, false, c.SkipReason)); continue; }
            results.Add(BumpProjectVersion(c.Path, part, ctx));
        }
    }
    else if (csprojFile != null)
    {
        results.Add(BumpProjectVersion(csprojFile, part, ctx));
    }
    step.Data = new Dictionary<string, object?> { ["part"] = part, ["projects"] = results };
    return step;
}

static StepResult RunBumpCommit(string? slnFile, string? csprojFile, string[] commandArgs, DevConfig? devConfig, StepResult step, RunContext ctx)
{
    var part = commandArgs.Length > 0 ? commandArgs[0] : "minor";
    var results = new List<ProjectBumpResult>();
    var newVersions = new HashSet<string>();

    void Bump(string projectPath)
    {
        var r = BumpProjectVersion(projectPath, part, ctx);
        results.Add(r);
        if (r.Bumped && r.To != null)
        {
            newVersions.Add(r.To);
            RunProcess("git", $"add \"{projectPath}\"", ctx);
        }
    }

    if (slnFile != null)
    {
        foreach (var c in GetProjectPaths(slnFile, devConfig?.IgnoreProjects, ctx))
        {
            if (!c.Include) { results.Add(new ProjectBumpResult(c.Path, null, null, false, c.SkipReason)); continue; }
            Bump(c.Path);
        }
    }
    else if (csprojFile != null)
    {
        Bump(csprojFile);
    }

    Dictionary<string, object?> gitInfo;
    switch (newVersions.Count)
    {
        case 0:
            ctx.Log("[yellow]No versions found to bump.[/]");
            gitInfo = new Dictionary<string, object?> { ["committed"] = false, ["reason"] = "no_bump" };
            break;
        case > 1:
            ctx.Log("[red]Multiple versions found to bump. Please commit them separately.[/]");
            gitInfo = new Dictionary<string, object?> { ["committed"] = false, ["reason"] = "multiple_versions" };
            break;
        default:
        {
            var nv = newVersions.First();
            ctx.Log($"[green]Committing and tagging version {nv}...[/]");
            var (commitExit, commitErr, commitOut) = RunProcess("git", $"commit -m \"build: {nv}\"", ctx);
            var (tagExit, tagErr, tagOut) = RunProcess("git", $"tag {nv}", ctx);
            if (commitExit == 0 && tagExit == 0)
            {
                gitInfo = new Dictionary<string, object?> { ["committed"] = true, ["tag"] = nv, ["message"] = $"build: {nv}" };
            }
            else
            {
                step.Status = "failed";
                step.ExitCode = 1;
                step.Error = new StepError
                {
                    Code = "git_failed",
                    Message = "git commit/tag failed",
                    Detail = new Dictionary<string, object?>
                    {
                        ["commitExitCode"] = commitExit,
                        ["tagExitCode"] = tagExit,
                        ["stderrTail"] = commitErr.Concat(tagErr).ToArray(),
                        ["stdoutTail"] = commitOut.Concat(tagOut).ToArray(),
                    },
                };
                gitInfo = new Dictionary<string, object?>
                {
                    ["committed"] = false,
                    ["reason"] = "git_failed",
                    ["commitExitCode"] = commitExit,
                    ["tagExitCode"] = tagExit,
                };
            }
            break;
        }
    }

    step.Data = new Dictionary<string, object?> { ["part"] = part, ["projects"] = results, ["git"] = gitInfo };
    return step;
}

static StepResult RunBuild(string buildTarget, StepResult step, RunContext ctx)
{
    var (builder, exit, errTail, outTail) = BuildSolutionOrProject(buildTarget, ctx);
    step.Data = new Dictionary<string, object?>
    {
        ["target"] = buildTarget,
        ["builder"] = builder,
        ["builderExitCode"] = exit,
        ["stderrTail"] = exit != 0 ? errTail : null,
        ["stdoutTail"] = exit != 0 ? outTail : null,
    };
    if (exit != 0)
    {
        step.Status = "failed";
        step.ExitCode = 1;
        step.Error = new StepError { Code = "builder_failed", Message = $"Builder exited with code {exit}" };
    }
    return step;
}

static StepResult RunLaunch(string? slnFile, string? csprojFile, StepResult step, RunContext ctx)
{
    if (slnFile != null)
    {
        ctx.Log($"[green]Opening[/] {slnFile} [green]in default IDE...[/]");
        Process.Start(new ProcessStartInfo(slnFile) { UseShellExecute = true });
        step.Data = new Dictionary<string, object?> { ["target"] = slnFile, ["opener"] = "shellExecute" };
    }
    else if (csprojFile != null)
    {
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
    var mutations = new List<Dictionary<string, object?>>();

    var buildFile = Path.Combine(path, "build-frontend.sh");
    if (!File.Exists(buildFile))
    {
        ctx.Log($"[red]No {Path.GetFileName(buildFile)} file found in the current directory.[/]");
        bool create;
        if (ctx.JsonMode)
        {
            if (!ctx.AutoYes)
            {
                step.Status = "failed";
                step.ExitCode = 5;
                step.Error = new StepError { Code = "interaction_required", Message = "build-frontend.sh is missing; re-run with --yes to create it." };
                return step;
            }
            create = true;
        }
        else
        {
            create = ctx.AutoYes || AnsiConsole.Prompt(new ConfirmationPrompt("Do you want to create it?"));
        }
        if (!create)
        {
            ctx.Log("[red]Aborting.[/]");
            step.Status = "failed";
            step.ExitCode = 1;
            step.Error = new StepError { Code = "user_aborted", Message = "User declined to create build-frontend.sh." };
            return step;
        }

        ctx.Log($"[green]Creating[/] {Path.GetFileName(buildFile)} [green]file in the current directory...[/]");
        var folderName = Path.GetFileName(path);
        var contents = $$"""
                         #!/usr/bin/env bash
                         set -euo pipefail

                         # enter your frontend folder, if any
                         cd {{folderName}}/

                         # install dependencies
                         npm ci

                         # compile Sass → CSS
                         find wwwroot -type f -name "*.scss" -print -execdir sh -c 'sass "{}:${1%.scss}.css"' _ {} \;

                         # transpile TypeScript
                         tsc --project ./tsconfig.json

                         # run any additional build steps
                         npm run build
                         """;
        File.WriteAllText(buildFile, contents.Replace("\r\n", "\n"));
        mutations.Add(new Dictionary<string, object?> { ["file"] = buildFile, ["action"] = "created" });
    }
    else
    {
        var content = File.ReadAllText(buildFile);
        if (content.Contains("\r\n"))
        {
            ctx.Log($"[yellow]Converting[/] {Path.GetFileName(buildFile)} [yellow]to LF line endings...[/]");
            content = content.Replace("\r\n", "\n");
            File.WriteAllText(buildFile, content);
            mutations.Add(new Dictionary<string, object?> { ["file"] = buildFile, ["action"] = "crlf_to_lf" });
        }
    }

    var attributesFile = Path.Combine(path, ".gitattributes");
    if (!File.Exists(attributesFile))
    {
        ctx.Log($"[red]No {Path.GetFileName(attributesFile)} file found in the current directory.[/]");
        bool createAttrs;
        if (ctx.JsonMode)
            createAttrs = ctx.AutoYes;
        else
            createAttrs = ctx.AutoYes || AnsiConsole.Prompt(new ConfirmationPrompt("Do you want to create it?"));

        if (createAttrs)
        {
            ctx.Log($"[green]Creating[/] {Path.GetFileName(attributesFile)} [green]file in the current directory...[/]");
            File.WriteAllText(attributesFile, "# Set default behavior to automatically normalize line endings.\n* text=auto\n# Explicitly declare text files we want to always be normalized and converted to native line endings on checkout.\n*.sh text eol=lf");
            mutations.Add(new Dictionary<string, object?> { ["file"] = attributesFile, ["action"] = "created" });
        }
        else
        {
            ctx.Log("[yellow]Ignoring.[/]");
            step.Warnings.Add("gitattributes_missing");
        }
    }
    else
    {
        var content = File.ReadAllText(attributesFile);
        if (!content.Contains("*.sh text eol=lf"))
        {
            ctx.Log($"[yellow]Adding LF line endings for bash files to {Path.GetFileName(attributesFile)}...[/]");
            content += "\n*.sh text eol=lf";
            File.WriteAllText(attributesFile, content);
            mutations.Add(new Dictionary<string, object?> { ["file"] = attributesFile, ["action"] = "appended", ["detail"] = "*.sh text eol=lf" });
        }
    }

    const string dockerImage = "ghcr.io/stevehansen/vidyano-frontend-builder:latest";
    var dockerCommand = $"docker run --rm -v \"{path}:/src\" -w /src {dockerImage}";
    var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    var (file, procArgs) = isWindows ? ("cmd.exe", $"/c {dockerCommand}") : ("bash", $"-c \"{dockerCommand}\"");
    var (exit, errTail, outTail) = RunProcess(file, procArgs, ctx);

    step.Data = new Dictionary<string, object?>
    {
        ["mutations"] = mutations,
        ["docker"] = new Dictionary<string, object?>
        {
            ["image"] = dockerImage,
            ["exitCode"] = exit,
            ["stderrTail"] = exit != 0 ? errTail : null,
            ["stdoutTail"] = exit != 0 ? outTail : null,
        },
    };
    if (exit != 0)
    {
        step.Status = "failed";
        step.ExitCode = 1;
        step.Error = new StepError { Code = "docker_failed", Message = $"Docker exited with code {exit}" };
    }
    return step;
}

static ProjectBumpResult BumpProjectVersion(string projectPath, string part, RunContext ctx)
{
    if (!File.Exists(projectPath))
    {
        ctx.Log($"[red]Project {Path.GetFileNameWithoutExtension(projectPath)} not found, skipping.[/]");
        return new ProjectBumpResult(projectPath, null, null, false, "not_found");
    }

    var csproj = File.ReadAllText(projectPath);
    var versionMatch = VersionRegex().Match(csproj);
    if (!versionMatch.Success)
    {
        ctx.Log($"[red]No version found in {Path.GetFileNameWithoutExtension(projectPath)}.[/]");
        return new ProjectBumpResult(projectPath, null, null, false, "no_version_tag");
    }

    var version = versionMatch.Groups["version"].Value;
    var semver = new SemVer(version);
    var newVersion = part switch
    {
        "major" => new(semver.Major + 1, 0, 0, semver.Fix is null ? semver.Fix : 0, semver.Suffix, semver.BuildVariables),
        "minor" => new(semver.Major, semver.Minor + 1, 0, semver.Fix is null ? semver.Fix : 0, semver.Suffix, semver.BuildVariables),
        "patch" => new(semver.Major, semver.Minor, semver.Build + 1, semver.Fix is null ? semver.Fix : 0, semver.Suffix, semver.BuildVariables),
        _ => new SemVer(semver.Major, semver.Minor, semver.Build, semver.Fix + 1, semver.Suffix, semver.BuildVariables),
    };

    ctx.Log($"[green]Bumping[/] {Path.GetFileNameWithoutExtension(projectPath)} [green]from[/] [blue]{version}[/] [green]to[/] [yellow]{newVersion}[/]");
    csproj = VersionRegex().Replace(csproj, $"<Version>{newVersion}</Version>");
    File.WriteAllText(projectPath, csproj);

    return new ProjectBumpResult(projectPath, version, newVersion.ToString(), true, null);
}

static (string Builder, int ExitCode, string[] StderrTail, string[] StdoutTail) BuildSolutionOrProject(string buildTarget, RunContext ctx)
{
    var buildFile = Path.Combine(Path.GetDirectoryName(buildTarget) ?? ".", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "build.cmd" : "build.sh");
    if (File.Exists(buildFile))
    {
        ctx.Log($"[green]Building[/] {Path.GetFileName(buildTarget)} [green]using[/] {Path.GetFileName(buildFile)}[green]...[/]");
        var (exit, errTail, outTail) = RunProcess(buildFile, "", ctx);
        return (Path.GetFileName(buildFile), exit, errTail, outTail);
    }
    ctx.Log($"[green]Building[/] {Path.GetFileName(buildTarget)} [green]in Release mode...[/]");
    var (dExit, dErrTail, dOutTail) = RunProcess("dotnet", $"build \"{buildTarget}\" -c Release", ctx);
    return ("dotnet", dExit, dErrTail, dOutTail);
}

static bool IsInGitSubmodule(string filePath)
{
    try
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return false;
        var currentDir = new DirectoryInfo(directory);
        while (currentDir != null)
        {
            var gitPath = Path.Combine(currentDir.FullName, ".git");
            if (File.Exists(gitPath)) return true;
            if (Directory.Exists(gitPath)) return false;
            currentDir = currentDir.Parent;
        }
        return false;
    }
    catch
    {
        return false;
    }
}

static IEnumerable<ProjectCandidate> GetProjectPaths(string slnFile, IReadOnlyCollection<string>? ignoreProjects, RunContext ctx)
{
    var serializer = SolutionSerializers.GetSerializerByMoniker(slnFile);
    if (serializer is null)
    {
        ctx.Log($"[red]Unable to find a serializer for {slnFile}[/]");
        yield break;
    }

    SolutionModel solution;
    try
    {
        solution = serializer.OpenAsync(slnFile, CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (SolutionException ex)
    {
        ctx.Log($"[red]Error opening solution file:[/] {ex.Message}");
        yield break;
    }

    foreach (var solutionProject in solution.SolutionProjects)
    {
        var projectPath = solutionProject.FilePath?.Replace('\\', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(projectPath)) continue;
        if (!Path.IsPathRooted(projectPath))
            projectPath = Path.Combine(Path.GetDirectoryName(slnFile) ?? "", projectPath);

        if (IsInGitSubmodule(projectPath))
        {
            ctx.Log($"[yellow]Skipping[/] {Path.GetFileNameWithoutExtension(projectPath)} [yellow](inside git submodule)[/]");
            yield return new ProjectCandidate(projectPath, false, "submodule");
            continue;
        }

        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        if (ignoreProjects?.Any(p => string.Equals(p, projectName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            ctx.Log($"[yellow]Skipping[/] {projectName} [yellow](configured in dev.json)[/]");
            yield return new ProjectCandidate(projectPath, false, "ignored");
            continue;
        }

        yield return new ProjectCandidate(projectPath, true, null);
    }
}

static string ReplaceVariables(string command, string? slnFile, string? csprojFile, string path)
{
    return command
        .Replace("{sln}", slnFile ?? string.Empty)
        .Replace("{project}", csprojFile ?? string.Empty)
        .Replace("{dir}", path);
}

static string? FindSolutionFile(string searchPath)
{
    return Directory.GetFiles(searchPath, "*.sln*")
        .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f.Length)
        .FirstOrDefault();
}

static string? FindProjectFile(string searchPath)
{
    return Directory.GetFiles(searchPath, "*.csproj").FirstOrDefault();
}

static (int ExitCode, string[] StderrTail, string[] StdoutTail) RunProcess(string fileName, string procArgs, RunContext ctx, int tailLines = StderrTailLines)
{
    var psi = new ProcessStartInfo(fileName, procArgs)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = ctx.JsonMode,
    };

    using var p = new Process { StartInfo = psi };
    var errBuffer = new Queue<string>();
    var outBuffer = new Queue<string>();
    var bufferLock = new object();

    p.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is null) return;
        lock (bufferLock)
        {
            if (!ctx.JsonMode) Console.Error.WriteLine(e.Data);
            errBuffer.Enqueue(e.Data);
            while (errBuffer.Count > tailLines) errBuffer.Dequeue();
        }
    };
    if (ctx.JsonMode)
    {
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (bufferLock)
            {
                outBuffer.Enqueue(e.Data);
                while (outBuffer.Count > tailLines) outBuffer.Dequeue();
            }
        };
    }

    try
    {
        p.Start();
    }
    catch (Exception ex)
    {
        if (!ctx.JsonMode) AnsiConsole.WriteException(ex);
        return (-1, new[] { ex.Message }, Array.Empty<string>());
    }

    p.BeginErrorReadLine();
    if (ctx.JsonMode) p.BeginOutputReadLine();
    p.WaitForExit();
    lock (bufferLock) return (p.ExitCode, errBuffer.ToArray(), outBuffer.ToArray());
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
    [GeneratedRegex("<Version>(?<version>.*)</Version>")]
    private static partial Regex VersionRegex();
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

internal sealed record ProjectBumpResult(string Path, string? From, string? To, bool Bumped, string? Reason);

internal sealed record ProjectCandidate(string Path, bool Include, string? SkipReason);

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
