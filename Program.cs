// 'Dev' tool can be used to do some development tasks based on the current directory.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dev;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Spectre.Console;

var informationalVersion = ThisAssembly.Info.InformationalVersion.Split('+', 2);
AnsiConsole.MarkupLine($"[bold green]{ThisAssembly.Info.Product}[/] v[blue]{informationalVersion[0]}[/]+{informationalVersion[1][..7]}");

var path = Environment.CurrentDirectory;

var command = "launch";
if (args.Length > 0)
{
    command = args[0];

    // Map aliases to full commands
    command = command switch
    {
        "b" => "build",
        "h" or "?" => "help",
        "f" => "frontend",
        "v" => "bump",
        "vc" => "bump-commit",
        _ => command,
    };

    if (command is "help")
    {
        var table = new Table()
            .Title("[yellow]Dev Tool Commands[/]")
            .AddColumn(new TableColumn("[green]Command[/]").LeftAligned())
            .AddColumn(new TableColumn("[blue]Description[/]").LeftAligned());

        table.AddRow("launch (default)", "Launches the current solution in your default IDE or project in Visual Studio Code.");
        table.AddRow("bump (v) [major|minor|patch|revision]".EscapeMarkup(), "Bumps the version of all projects in the current solution or the current project. Defaults to minor.");
        table.AddRow("bump-commit (vc) [major|minor|patch|revision]".EscapeMarkup(), "Bumps the version and commits/tag the change in the current solution or project. Defaults to minor.");
        table.AddRow("build (b)", "Builds the current solution or project in Release mode.");
        table.AddRow("frontend (f)", "Runs the Vidyano frontend builder in the current directory.");
        table.AddRow("help (h)", "Displays this help message.");

        AnsiConsole.Write(table);
        return;
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
                return;
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

                             # Generate vidyano libman files
                             dotnet restore
                                 
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
        return;
    }
}

// Check if we have a .sln or .slnx file in the current directory.
var slnFile = Directory.GetFiles(path, "*.sln*")
    .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f.Length)
    .FirstOrDefault(); // Prefer the shortest path
if (slnFile != null)
{
    if (command is "bump")
    {
        var subCommand = args.Length > 1 ? args[1] : "minor";

        // Will bump all versions inside all csproj files linked in the solution
        foreach (var projectPath in GetProjectPaths(slnFile))
            BumpProjectVersion(projectPath, subCommand);

        return;
    }

    if (command is "bump-commit")
    {
        var subCommand = args.Length > 1 ? args[1] : "minor";

        var newVersions = new HashSet<string>();
        foreach (var projectPath in GetProjectPaths(slnFile))
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

        return;
    }

    if (command is "build")
    {
        BuildSolutionOrProject(slnFile);
        return;
    }

    AnsiConsole.MarkupLine($"[green]Opening[/] {slnFile} [green]in default IDE...[/]");
    Process.Start(new ProcessStartInfo(slnFile) { UseShellExecute = true });
    return;
}

// TODO: Check if we have a .devcontainer folder in the current directory. And if so, open it in Visual Studio Code as a dev container.

// Check if we have a .csproj file in the current directory. And if so, open it in Visual Studio Code.
var csprojFile = Directory.GetFiles(path, "*.csproj").FirstOrDefault();
if (csprojFile != null)
{
    if (command is "bump")
    {
        // Will bump the version inside the current csproj file
        BumpProjectVersion(csprojFile, args.Length > 1 ? args[1] : "minor");
        return;
    }

    if (command is "bump-commit")
    {
        var subCommand = args.Length > 1 ? args[1] : "minor";

        var newVersion = BumpProjectVersion(csprojFile, subCommand);

        if (newVersion != null)
        {
            AnsiConsole.MarkupLine($"[green]Committing and tagging version {newVersion}...[/]");
            Process.Start("git", $"add \"{csprojFile}\"").WaitForExit();
            Process.Start("git", $"commit -m \"build: {newVersion}\"").WaitForExit();
            Process.Start("git", $"tag {newVersion}").WaitForExit();
        }

        return;
    }

    if (command is "build")
    {
        BuildSolutionOrProject(csprojFile);
        return;
    }

    AnsiConsole.MarkupLine($"[green]Opening[/] {csprojFile} [green]in Visual Studio Code...[/]");
    Process.Start("code", csprojFile);
    return;
}

// Nothing to do.
AnsiConsole.MarkupLine("[red]No .sln, .slnx or .csproj file found in the current directory.[/]");

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
    AnsiConsole.WriteLine($"[green]Building[/] {Path.GetFileName(path)} [green]in Release mode...[/]");
    Process.Start("dotnet", $"build \"{path}\" -c Release").WaitForExit();
}

static IReadOnlyCollection<string> GetProjectPaths(string slnFile)
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

        projectPaths.Add(projectPath);
    }

    return projectPaths;
}

partial class Program
{
    [GeneratedRegex("<Version>(?<version>.*)</Version>")]
    private static partial Regex VersionRegex();
}
