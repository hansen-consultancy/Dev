// 'Dev' tool can be used to do some development tasks based on the current directory.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dev;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

Console.WriteLine($"{ThisAssembly.Info.Product} v{ThisAssembly.Info.InformationalVersion}");

var path = Environment.CurrentDirectory;

var command = "launch";
if (args.Length > 0)
{
    command = args[0];

    // Map aliases to full commands
    command = command switch
    {
        "b" => "build",
        "h" => "help",
        "f" => "frontend",
        "v" => "bump",
        "vc" => "bump-commit",
        _ => command,
    };

    if (command is "help")
    {
        Console.WriteLine("Usage: dev [command]");
        Console.WriteLine("Commands:");
        Console.WriteLine("  launch (default) - Launches the current solution in your default IDE or project in Visual Studio Code.");
        Console.WriteLine("  bump (v) [major|minor|patch|revision] - Bumps the version of all projects in the current solution or the current project. Defaults to minor.");
        Console.WriteLine("  bump-commit (vc) [major|minor|patch|revision] - Bumps the version and commits/tag the change in the current solution or project. Defaults to minor.");
        Console.WriteLine("  build (b) - Builds the current solution or project in Release mode.");
        Console.WriteLine("  frontend (f) - Runs the Vidyano frontend builder in the current directory.");
        Console.WriteLine("  help (h) - Displays this help message.");
        return;
    }

    if (command is "frontend")
    {
        Console.WriteLine("Running Vidyano frontend builder...");

        // NOTE: This expects a build-frontend.sh in the current folder, if it doesn't exist we should ask the user if we should create it.

        var buildFile = Path.Combine(path, "build-frontend.sh");
        if (!File.Exists(buildFile))
        {
            Console.WriteLine($"No {Path.GetFileName(buildFile)} file found in the current directory.");
            Console.Write("Do you want to create it? (y/N) ");
            var answer = Console.ReadLine();
            if (answer?.ToLower() != "y")
            {
                Console.WriteLine("Aborting.");
                return;
            }

            Console.WriteLine($"Creating {Path.GetFileName(buildFile)} file in the current directory...");

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
                Console.WriteLine($"Converting {Path.GetFileName(buildFile)} to LF line endings...");
                content = content.Replace("\r\n", "\n");
                File.WriteAllText(buildFile, content);
            }
        }

        // NOTE: We should make sure that the .gitattributes is set up correctly to enforce LF line endings for bash files.
        var attributesFile = Path.Combine(path, ".gitattributes");
        if (!File.Exists(attributesFile))
        {
            Console.WriteLine($"No {Path.GetFileName(attributesFile)} file found in the current directory.");
            Console.Write("Do you want to create it? (y/N) ");
            var answer = Console.ReadLine();
            if (answer?.ToLower() != "y")
            {
                Console.WriteLine("Ignoring.");
            }
            else
            {
                Console.WriteLine($"Creating {Path.GetFileName(attributesFile)} file in the current directory...");
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
                Console.WriteLine($"Adding LF line endings for bash files to {Path.GetFileName(attributesFile)}...");
                content += "\n*.sh text eol=lf";
                File.WriteAllText(attributesFile, content);
            }
        }

        var dockerCommand = $"docker run --rm -v \"{path}:/src\" -w /src ghcr.io/stevehansen/vidyano-frontend-builder:latest";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start("cmd.exe", $"/c {dockerCommand}");
        else
            Process.Start("bash", $"-c \"{dockerCommand}\"");
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
                Console.WriteLine("No versions found to bump.");
                break;

            case > 1:
                Console.WriteLine("Multiple versions found to bump. Please commit them separately.");
                break;

            default:
                {
                    var newVersion = newVersions.First();
                    Console.WriteLine($"Committing and tagging version {newVersion}...");
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

    Console.WriteLine($"Opening {slnFile} in default IDE...");
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
            Console.WriteLine($"Committing and tagging version {newVersion}...");
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

    Console.WriteLine($"Opening {csprojFile} in Visual Studio Code...");
    Process.Start("code", csprojFile);
    return;
}

// Nothing to do.
Console.WriteLine("No .sln or .csproj file found in the current directory.");

static string? BumpProjectVersion(string projectPath, string subCommand)
{
    if (!File.Exists(projectPath))
    {
        Console.WriteLine($"Project {Path.GetFileNameWithoutExtension(projectPath)} not found, skipping.");
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

        Console.WriteLine($"Bumping {Path.GetFileNameWithoutExtension(projectPath)} from {version} to {newVersion}...");
        csproj = VersionRegex().Replace(csproj, $"<Version>{newVersion}</Version>");
        File.WriteAllText(projectPath, csproj);

        return newVersion.ToString();
    }

    Console.WriteLine($"No version found in {Path.GetFileNameWithoutExtension(projectPath)}.");
    return null;
}

static void BuildSolutionOrProject(string path)
{
    // Check if we have a build.cmd or build.sh file in the same folder depending on the OS and launch that instead
    var buildFile = Path.Combine(Path.GetDirectoryName(path) ?? ".", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "build.cmd" : "build.sh");
    if (File.Exists(buildFile))
    {
        Console.WriteLine($"Building {Path.GetFileName(path)} using {Path.GetFileName(buildFile)}...");
        Process.Start(buildFile);
        return;
    }

    // Use dotnet build to build the solution or project in Release mode
    Console.WriteLine($"Building {Path.GetFileName(path)} in Release mode...");
    Process.Start("dotnet", $"build \"{path}\" -c Release");
}

static IReadOnlyCollection<string> GetProjectPaths(string slnFile)
{
    // Get the project paths from the solution file
    var serializer = SolutionSerializers.GetSerializerByMoniker(slnFile);
    if (serializer is null)
    {
        Console.WriteLine($"Unable to find a serializer for {slnFile}");
        return [];
    }

    SolutionModel solution;
    try
    {
        solution = serializer.OpenAsync(slnFile, CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (SolutionException ex)
    {
        Console.WriteLine($"Error opening solution file: {ex.Message}");
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