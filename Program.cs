// 'Dev' tool can be used to do some development tasks based on the current directory.

using Dev;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var informationalVersion = ThisAssembly.Info.InformationalVersion.Split('+', 2);
AnsiConsole.MarkupLine($"[bold green]{ThisAssembly.Info.Product}[/] v[green]{informationalVersion[0]}[/]+{informationalVersion[1][..7]}");

var path = Environment.CurrentDirectory;

var configFile = Path.Combine(path, "commands.json");
List<ConfigCommand>? configCommands = null;
ConfigCommand? defaultConfig = null;
if (File.Exists(configFile))
{
    try
    {
        configCommands = JsonSerializer.Deserialize<List<ConfigCommand>>(File.ReadAllText(configFile));
        defaultConfig = configCommands?.FirstOrDefault(c => c.Default);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine("[red]Error reading commands.json:[/]");
        AnsiConsole.WriteException(ex);
        return;
    }
}

var devConfigFile = Path.Combine(path, "dev.json");
if (!File.Exists(devConfigFile))
{
    // Check parent directory
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
        AnsiConsole.MarkupLine("[red]Error reading dev.json:[/]");
        AnsiConsole.WriteException(ex);
        return;
    }
}

string commandInput;
if (args.Length > 0)
    commandInput = args[0];
else if (configCommands is not null)
    commandInput = defaultConfig?.Name ?? "help";
else
    commandInput = "launch";

// Check if we have combined commands with '+'
var commands = commandInput.Split('+').Select(cmd => cmd.Trim()).ToArray();

// Process each command
for (int i = 0; i < commands.Length; i++)
{
    var command = commands[i];

    // Map aliases to full commands
    // TODO: Make this configurable in commands.json
    command = command switch
    {
        "b" => "build",
        "h" or "?" => "help",
        "f" => "frontend",
        "v" => "bump",
        "vc" => "bump-commit",
        "c" => "clean",
        _ => command,
    };

    // For combined commands, display what we're executing
    if (commands.Length > 1)
    {
        AnsiConsole.MarkupLine($"[cyan]Executing command {i + 1}/{commands.Length}: {command}[/]");
    }

    // Pass remaining args only for the first command in the combination
    var commandArgs = i == 0 ? args.Skip(1).ToArray() : Array.Empty<string>();

    // Execute the command
    if (!ExecuteCommand(command, commandArgs, path, configCommands, defaultConfig, devConfig))
    {
        // If a command fails, stop executing the rest
        if (commands.Length > 1)
        {
            AnsiConsole.MarkupLine($"[red]Command '{command}' failed. Stopping execution.[/]");
        }
        return;
    }
}

static bool ExecuteCommand(string command, string[] commandArgs, string path, List<ConfigCommand>? configCommands, ConfigCommand? defaultConfig, DevConfig? devConfig)
{
    if (command is "help")
    {
        var table = new Table()
            .Title("[yellow]Dev Tool Commands[/]")
            .AddColumn(new TableColumn("[green]Command[/]").LeftAligned())
            .AddColumn(new TableColumn("[blue]Description[/]").LeftAligned());

        if (configCommands is not null)
        {
            foreach (var c in configCommands)
            {
                if (c.Name is "help" or "h")
                    continue; // Skip help command

                var desc = !string.IsNullOrEmpty(c.BuiltIn) ? c.BuiltIn switch
                {
                    "launch" => "Launches the current solution in your default IDE or project in Visual Studio Code.",
                    "bump" => "Bumps the version of all projects in the current solution or the current project. Defaults to minor.",
                    "bump-commit" => "Bumps the version and commits/tag the change in the current solution or project. Defaults to minor.",
                    "build" => "Builds the current solution or project in Release mode.",
                    "frontend" => "Runs the Vidyano frontend builder in the current directory.",
                    "clean" => "Clean the current folder by removing [yellow]bin[/], [yellow]obj[/], [yellow]tmp-build[/], [yellow]bin-windows[/], [yellow]bin-linux[/], [yellow]obj-windows[/], [yellow]obj-linux[/] folders. Use this command if you experience build issues.",
                    _ => c.BuiltIn,
                } : c.Description ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? c.Windows : c.NonWindows) ?? string.Empty;

                // TODO: Need to show aliases as well, e.g. "bump (v) [major|minor|patch|revision]" and optional parameters like "[major|minor|patch|revision]"
                table.AddRow($"{c.Name}{(c.Default ? " (default)" : string.Empty)}".EscapeMarkup(), desc.EscapeMarkup());
            }
        }
        else
        {
            table.AddRow("launch (default)", "Launches the current solution in your default IDE or project in Visual Studio Code.");
            table.AddRow("bump (v) [major|minor|patch|revision]".EscapeMarkup(), "Bumps the version of all projects in the current solution or the current project. Defaults to minor.");
            table.AddRow("bump-commit (vc) [major|minor|patch|revision]".EscapeMarkup(), "Bumps the version and commits/tag the change in the current solution or project. Defaults to minor.");
            table.AddRow("build (b)", "Builds the current solution or project in Release mode.");
            table.AddRow("frontend (f)", "Runs the Vidyano frontend builder in the current directory.");
            table.AddRow("clean (c)", "Clean the current folder by removing [yellow]bin[/], [yellow]obj[/], [yellow]tmp-build[/], [yellow]bin-windows[/], [yellow]bin-linux[/], [yellow]obj-windows[/], [yellow]obj-linux[/] folders. Use this command if you experience build issues.");
        }

        // Help is always available
        table.AddRow("help (h)", "Displays this help message.");

        // Add note about command combinations
        table.AddEmptyRow();
        table.AddRow("[dim]Combine commands with '+'[/]", "[dim]Example: dev b+f (build then frontend)[/]");

        AnsiConsole.Write(table);
        return true;
    }

    if (configCommands is not null)
    {
        var cfgCmd = configCommands.FirstOrDefault(c => string.Equals(c.Name, command, StringComparison.OrdinalIgnoreCase));
        if (cfgCmd is not null)
        {
            if (string.IsNullOrEmpty(cfgCmd.BuiltIn))
            {
                var sln = FindSolutionFile(path);
                var csproj = FindProjectFile(path);

                // If not found in current directory, check src/ folder
                if (sln == null && csproj == null)
                {
                    var srcDir = Path.Combine(path, "src");
                    if (Directory.Exists(srcDir))
                    {
                        sln = FindSolutionFile(srcDir);
                        csproj = FindProjectFile(srcDir);
                    }
                }

                var cmdLine = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? cfgCmd.Windows : cfgCmd.NonWindows;
                if (string.IsNullOrWhiteSpace(cmdLine))
                {
                    AnsiConsole.MarkupLine("[red]No command defined for the current environment.[/]");
                    return false;
                }

                cmdLine = ReplaceVariables(cmdLine, sln, csproj, path);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start("cmd.exe", $"/c {cmdLine}").WaitForExit();
                else
                    Process.Start("bash", $"-c \"{cmdLine}\"").WaitForExit();
                return true;
            }

            command = cfgCmd.BuiltIn;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Unknown command: {command}[/]");
            return false;
        }
    }

    if (command is "frontend")
    {
        AnsiConsole.MarkupLine("[green]Running Vidyano frontend builder...[/]");

        // NOTE: This expects a build-frontend.sh in the current folder, if it doesn't exist we should ask the user if we should create it.

        var buildFile = Path.Combine(path, "build-frontend.sh");
        if (!File.Exists(buildFile))
        {
            AnsiConsole.MarkupLine($"[red]No {Path.GetFileName(buildFile)} file found in the current directory.[/]");
            if (!AnsiConsole.Prompt(new ConfirmationPrompt("Do you want to create it?")))
            {
                AnsiConsole.MarkupLine("[red]Aborting.[/]");
                return false;
            }

            AnsiConsole.MarkupLine($"[green]Creating[/] {Path.GetFileName(buildFile)} [green]file in the current directory...[/]");

            // We need to find our if the script would need to enter the correct folder first, we'll assume that the folder is the same name as our current folder
            var folderName = Path.GetFileName(path);
            // NOTE: This needs to use LF as line endings, not CRLF.
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
        }
        else
        {
            // We want to make sure that no CRLF line endings are in the file, so we need to convert it to LF line endings.
            var content = File.ReadAllText(buildFile);
            if (content.Contains("\r\n"))
            {
                AnsiConsole.MarkupLine($"[yellow]Converting[/] {Path.GetFileName(buildFile)} [yellow]to LF line endings...[/]");
                content = content.Replace("\r\n", "\n");
                File.WriteAllText(buildFile, content);
            }
        }

        // NOTE: We should make sure that the .gitattributes is set up correctly to enforce LF line endings for bash files.
        var attributesFile = Path.Combine(path, ".gitattributes");
        if (!File.Exists(attributesFile))
        {
            AnsiConsole.MarkupLine($"[red]No {Path.GetFileName(attributesFile)} file found in the current directory.[/]");
            if (!AnsiConsole.Prompt(new ConfirmationPrompt("Do you want to create it?")))
            {
                AnsiConsole.MarkupLine("[yellow]Ignoring.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Creating[/] {Path.GetFileName(attributesFile)} [green]file in the current directory...[/]");
                // NOTE: This needs to use LF as line endings, not CRLF.
                File.WriteAllText(attributesFile, "# Set default behavior to automatically normalize line endings.\n* text=auto\n# Explicitly declare text files we want to always be normalized and converted to native line endings on checkout.\n*.sh text eol=lf");
            }
        }
        else
        {
            // Check if the file contains the line for bash files
            var content = File.ReadAllText(attributesFile);
            if (!content.Contains("*.sh text eol=lf")) // TODO: Might be as comment
            {
                // TODO: Make sure that the *.sh isn't already in the file.
                AnsiConsole.MarkupLine($"[yellow]Adding LF line endings for bash files to {Path.GetFileName(attributesFile)}...[/]");
                content += "\n*.sh text eol=lf";
                File.WriteAllText(attributesFile, content);
            }
        }

        var dockerCommand = $"docker run --rm -v \"{path}:/src\" -w /src ghcr.io/stevehansen/vidyano-frontend-builder:latest";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start("cmd.exe", $"/c {dockerCommand}").WaitForExit();
        else
            Process.Start("bash", $"-c \"{dockerCommand}\"").WaitForExit();
        return true;
    }

    if (command is "clean")
    {
        AnsiConsole.MarkupLine("[green]Cleaning current directory...[/]");

        // Clean the current directory by removing bin, obj, tmp-build, bin-windows, bin-linux, obj-windows and obj-linux folders
        var foldersToDelete = new[] { "bin", "obj", "tmp-build", "bin-windows", "bin-linux", "obj-windows", "obj-linux" };
        var allDirectories = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Where(dir => !dir.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
            .Where(dir => foldersToDelete.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase));

        foreach (var folderPath in allDirectories)
        {
            try
            {
                if (Directory.Exists(folderPath))
                {
                    AnsiConsole.MarkupLine($"[red]Deleting[/] {folderPath} [red]folder...[/]");
                    Directory.Delete(folderPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShowLinks);
            }
        }
        return true;
    }

    // Check if we have a .sln or .slnx file in the current directory.
    var slnFile = FindSolutionFile(path);
    var csprojFile = FindProjectFile(path);

    // If not found in current directory, check src/ folder
    if (slnFile == null && csprojFile == null)
    {
        var srcPath = Path.Combine(path, "src");
        if (Directory.Exists(srcPath))
        {
            slnFile = FindSolutionFile(srcPath);
            csprojFile = FindProjectFile(srcPath);
        }
    }

    // Process solution file if found
    if (slnFile != null)
    {
        if (command is "bump")
        {
            var subCommand = commandArgs.Length > 0 ? commandArgs[0] : "minor";

            // Will bump all versions inside all csproj files linked in the solution
            foreach (var projectPath in GetProjectPaths(slnFile, devConfig?.IgnoreProjects))
                BumpProjectVersion(projectPath, subCommand);

            return true;
        }

        if (command is "bump-commit")
        {
            var subCommand = commandArgs.Length > 0 ? commandArgs[0] : "minor";

            var newVersions = new HashSet<string>();
            foreach (var projectPath in GetProjectPaths(slnFile, devConfig?.IgnoreProjects))
            {
                var newVersion = BumpProjectVersion(projectPath, subCommand);

                if (newVersion != null)
                {
                    newVersions.Add(newVersion);
                    Process.Start("git", $"add \"{projectPath}\"").WaitForExit();
                }
            }

            switch (newVersions.Count)
            {
                case 0:
                    AnsiConsole.MarkupLine("[yellow]No versions found to bump.[/]");
                    break;

                case > 1:
                    AnsiConsole.MarkupLine("[red]Multiple versions found to bump. Please commit them separately.[/]");
                    break;

                default:
                    {
                        var newVersion = newVersions.First();
                        AnsiConsole.MarkupLine($"[green]Committing and tagging version {newVersion}...[/]");
                        Process.Start("git", $"commit -m \"build: {newVersion}\"").WaitForExit();
                        Process.Start("git", $"tag {newVersion}").WaitForExit();
                        break;
                    }
            }

            return true;
        }

        if (command is "build")
        {
            BuildSolutionOrProject(slnFile);
            return true;
        }

        if (command is "launch")
        {
            AnsiConsole.MarkupLine($"[green]Opening[/] {slnFile} [green]in default IDE...[/]");
            Process.Start(new ProcessStartInfo(slnFile) { UseShellExecute = true });
            return true;
        }

        if (command is "pack")
        {
            AnsiConsole.MarkupLine($"[green]Packing NuGet packages for[/] {slnFile} [green]...[/]");
            var nugetConfigPath = GetRootNugetConfigPath();
            var nugetConfig = XDocument.Load(nugetConfigPath);
            var sourceName = commandArgs[0];
            var nugetSourcePath = nugetConfig.Element("configuration")?.Element("packageSources")?.Elements("add")?
                .Select(x => new
                {
                    Name = x.Attribute("key")?.Value,
                    Source = x.Attribute("value")?.Value
                })?
                .FirstOrDefault(x => x.Name?.Equals(sourceName, StringComparison.InvariantCultureIgnoreCase) ?? false)?
                .Source;

            Process.Start("dotnet", $"pack \"{slnFile}\" -c Release -o \"{nugetSourcePath}\"").WaitForExit();
            AnsiConsole.MarkupLine($"[green]NuGet packages for[/] {slnFile} published to {nugetSourcePath}[green][/]");
            return true;
        }
    }

    // TODO: Check if we have a .devcontainer folder in the current directory. And if so, open it in Visual Studio Code as a dev container.

    // Process project file if found (and no solution was found)
    if (csprojFile != null)
    {
        if (command is "bump")
        {
            // Will bump the version inside the current csproj file
            BumpProjectVersion(csprojFile, commandArgs.Length > 0 ? commandArgs[0] : "minor");
            return true;
        }

        if (command is "bump-commit")
        {
            var subCommand = commandArgs.Length > 0 ? commandArgs[0] : "minor";

            var newVersion = BumpProjectVersion(csprojFile, subCommand);

            if (newVersion != null)
            {
                AnsiConsole.MarkupLine($"[green]Committing and tagging version {newVersion}...[/]");
                Process.Start("git", $"add \"{csprojFile}\"").WaitForExit();
                Process.Start("git", $"commit -m \"build: {newVersion}\"").WaitForExit();
                Process.Start("git", $"tag {newVersion}").WaitForExit();
            }

            return true;
        }

        if (command is "build")
        {
            BuildSolutionOrProject(csprojFile);
            return true;
        }

        if (command is "launch")
        {
            AnsiConsole.MarkupLine($"[green]Opening[/] {csprojFile} [green]in Visual Studio Code...[/]");
            Process.Start("code", csprojFile);
            return true;
        }
    }

    // Nothing to do.
    AnsiConsole.MarkupLine("[red]No .sln, .slnx or .csproj file found in the current directory or src/ folder.[/]");
    return false;
}

static string? BumpProjectVersion(string projectPath, string subCommand)
{
    if (!File.Exists(projectPath))
    {
        AnsiConsole.MarkupLine($"[red]Project {Path.GetFileNameWithoutExtension(projectPath)} not found, skipping.[/]");
        return null;
    }

    var csproj = File.ReadAllText(projectPath);
    var versionMatch = VersionRegex().Match(csproj);
    if (versionMatch.Success)
    {
        var version = versionMatch.Groups["version"].Value;
        var semver = new SemVer(version);
        // Bump based on subCommand (major, minor, patch or revision)
        var newVersion = subCommand switch
        {
            "major" => new(semver.Major + 1, 0, 0, semver.Fix is null ? semver.Fix : 0, semver.Suffix, semver.BuildVariables),
            "minor" => new(semver.Major, semver.Minor + 1, 0, semver.Fix is null ? semver.Fix : 0, semver.Suffix, semver.BuildVariables),
            "patch" => new(semver.Major, semver.Minor, semver.Build + 1, semver.Fix is null ? semver.Fix : 0, semver.Suffix, semver.BuildVariables),
            _ => new SemVer(semver.Major, semver.Minor, semver.Build, semver.Fix + 1, semver.Suffix, semver.BuildVariables),
        };

        AnsiConsole.MarkupLine($"[green]Bumping[/] {Path.GetFileNameWithoutExtension(projectPath)} [green]from[/] [blue]{version}[/] [green]to[/] [yellow]{newVersion}[/]");
        csproj = VersionRegex().Replace(csproj, $"<Version>{newVersion}</Version>");
        File.WriteAllText(projectPath, csproj);

        return newVersion.ToString();
    }

    AnsiConsole.MarkupLine($"[red]No version found in {Path.GetFileNameWithoutExtension(projectPath)}.[/]");
    return null;
}

static void BuildSolutionOrProject(string path)
{
    // Check if we have a build.cmd or build.sh file in the same folder depending on the OS and launch that instead
    var buildFile = Path.Combine(Path.GetDirectoryName(path) ?? ".", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "build.cmd" : "build.sh");
    if (File.Exists(buildFile))
    {
        AnsiConsole.MarkupLine($"[green]Building[/] {Path.GetFileName(path)} [green]using[/] {Path.GetFileName(buildFile)}[green]...[/]");
        Process.Start(buildFile).WaitForExit();
        return;
    }

    // Use dotnet build to build the solution or project in Release mode
    AnsiConsole.MarkupLine($"[green]Building[/] {Path.GetFileName(path)} [green]in Release mode...[/]");
    Process.Start("dotnet", $"build \"{path}\" -c Release").WaitForExit();
}

static bool IsInGitSubmodule(string filePath)
{
    try
    {
        // Get the directory containing the file
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return false;

        // Start from the file's directory and walk up to find .git
        var currentDir = new DirectoryInfo(directory);
        while (currentDir != null)
        {
            var gitPath = Path.Combine(currentDir.FullName, ".git");

            // Check if .git exists
            if (File.Exists(gitPath))
            {
                // If .git is a file (not a directory), it's a submodule
                // Submodules have a .git file that points to the actual git directory
                return true;
            }
            else if (Directory.Exists(gitPath))
            {
                // Found the main repository's .git directory
                return false;
            }

            currentDir = currentDir.Parent;
        }

        return false;
    }
    catch
    {
        // If we can't determine, assume it's not a submodule to be safe
        return false;
    }
}

static IReadOnlyCollection<string> GetProjectPaths(string slnFile, IReadOnlyCollection<string>? ignoreProjects = null)
{
    // Get the project paths from the solution file
    var serializer = SolutionSerializers.GetSerializerByMoniker(slnFile);
    if (serializer is null)
    {
        AnsiConsole.MarkupLine($"[red]Unable to find a serializer for {slnFile}[/]");
        return [];
    }

    SolutionModel solution;
    try
    {
        solution = serializer.OpenAsync(slnFile, CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (SolutionException ex)
    {
        AnsiConsole.MarkupLine($"[red]Error opening solution file:[/] {ex.Message}");
        return [];
    }

    var projectPaths = new List<string>();
    foreach (var solutionProject in solution.SolutionProjects)
    {
        var projectPath = solutionProject.FilePath?.Replace('\\', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(projectPath))
            continue;

        // Convert to absolute path if needed
        if (!Path.IsPathRooted(projectPath))
            projectPath = Path.Combine(Path.GetDirectoryName(slnFile) ?? "", projectPath);

        // Skip projects inside git submodules
        if (IsInGitSubmodule(projectPath))
        {
            AnsiConsole.MarkupLine($"[yellow]Skipping[/] {Path.GetFileNameWithoutExtension(projectPath)} [yellow](inside git submodule)[/]");
            continue;
        }

        // Skip ignored projects
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        if (ignoreProjects?.Any(p => string.Equals(p, projectName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            AnsiConsole.MarkupLine($"[yellow]Skipping[/] {projectName} [yellow](configured in dev.json)[/]");
            continue;
        }

        projectPaths.Add(projectPath);
    }

    return projectPaths;
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
        .FirstOrDefault(); // Prefer the shortest path
}

static string? FindProjectFile(string searchPath)
{
    return Directory.GetFiles(searchPath, "*.csproj").FirstOrDefault();
}

static string GetRootNugetConfigPath()
{
    var userConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "nuget.config");
    var linuxMacConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet", "NuGet.Config");

    var configPath = File.Exists(userConfig) ? userConfig
        : File.Exists(linuxMacConfig) ? linuxMacConfig
        : throw new FileNotFoundException("Could not find user NuGet configuration file.");

    return configPath;
}

partial class Program
{
    [GeneratedRegex("<Version>(?<version>.*)</Version>")]
    private static partial Regex VersionRegex();
}

internal sealed class ConfigCommand
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