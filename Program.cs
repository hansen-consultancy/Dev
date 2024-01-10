// 'Dev' tool can be used to do some development tasks based on the current directory.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Dev;

var path = Environment.CurrentDirectory;

var command = "launch";
if (args.Length > 0)
{
    command = args[0];

    if (command is "help")
    {
        Console.WriteLine("Usage: dev [command]");
        Console.WriteLine("Commands:");
        Console.WriteLine("  launch (default) - Launches the current solution in Visual Studio or project in Visual Studio Code.");
        Console.WriteLine("  bump [major|minor|patch|revision] - Bumps the version of all projects in the current solution or the current project. Defaults to patch.");
        Console.WriteLine("  build - Builds the current solution or project in Release mode.");
        Console.WriteLine("  help - Displays this help message.");
        return;
    }
}

// Check if we have a .sln file in the current directory. And if so, open it in Visual Studio.
var slnFile = Directory.GetFiles(path, "*.sln").FirstOrDefault();
if (slnFile != null)
{
    if (command is "bump")
    {
        var subCommand = args.Length > 1 ? args[1] : "patch";

        // Will bump all versions inside all csproj files linked in the solution
        var sln = File.ReadAllText(slnFile);
        var matches = ProjectRegex().Matches(sln);
        foreach (Match match in matches)
            BumpProjectVersion(match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar), subCommand);

        return;
    }

    if (command is "build")
    {
        BuildSolutionOrProject(slnFile);
        return;
    }

    Console.WriteLine($"Opening {slnFile} in Visual Studio...");
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
        BumpProjectVersion(csprojFile, args.Length > 1 ? args[1] : "patch");
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

static void BumpProjectVersion(string projectPath, string subCommand)
{
    if (!File.Exists(projectPath))
    {
        Console.WriteLine($"Project {Path.GetFileNameWithoutExtension(projectPath)} not found, skipping.");
        return;
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
    }
    else
        Console.WriteLine($"No version found in {Path.GetFileNameWithoutExtension(projectPath)}.");
}

static void BuildSolutionOrProject(string path)
{
    // Use dotnet build to build the solution or project in Release mode
    Console.WriteLine($"Building {Path.GetFileName(path)} in Release mode...");
    Process.Start("dotnet", $"build \"{path}\" -c Release");
}

partial class Program
{
    [GeneratedRegex("Project\\(\"\\{.*\\}\"\\)\\s*=\\s*\"(?<name>.*)\",\\s*\"(?<path>.*)\",\\s*\"\\{.*\\}\"")]
    private static partial Regex ProjectRegex();

    [GeneratedRegex("<Version>(?<version>.*)</Version>")]
    private static partial Regex VersionRegex();
}