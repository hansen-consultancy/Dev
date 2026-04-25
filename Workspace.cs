// Workspace: collapses sln/csproj/src/dev.json discovery into a single value object.
// Owns .sln vs .slnx precedence, the src/ fallback, submodule detection,
// dev.json parent-directory inheritance, and ignore-list filtering. Callers
// receive a Workspace and use semantic queries (BuildTarget, EnumerateBumpTargets);
// raw SolutionPath/ProjectPath are reserved for envelope serialization.

using System.Text.Json;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Dev;

public sealed class Workspace
{
    public string RootPath { get; }
    public string? SolutionPath { get; }
    public string? ProjectPath { get; }
    public BuildTarget BuildTarget { get; }

    private readonly IReadOnlyList<BumpTarget> _bumpTargets;

    private Workspace(
        string rootPath,
        string? solutionPath,
        string? projectPath,
        BuildTarget buildTarget,
        IReadOnlyList<BumpTarget> bumpTargets)
    {
        RootPath = rootPath;
        SolutionPath = solutionPath;
        ProjectPath = projectPath;
        BuildTarget = buildTarget;
        _bumpTargets = bumpTargets;
    }

    public IReadOnlyList<BumpTarget> EnumerateBumpTargets() => _bumpTargets;

    public static Workspace? Discover(string startPath, Action<string>? log = null)
        => Discover(startPath, log, new RealFileSystem(), new SolutionPersistenceReader());

    internal static Workspace? Discover(string startPath, Action<string>? log, IFileSystem fs, ISolutionReader sln)
    {
        var ignoreProjects = LoadDevConfig(startPath, fs)?.IgnoreProjects;

        var (slnFile, csprojFile) = ResolveBuildFiles(startPath, fs);
        if (slnFile is null && csprojFile is null)
            return null;

        var buildTargetPath = slnFile ?? csprojFile!;
        var buildTarget = new BuildTarget(
            buildTargetPath,
            Path.GetFileName(buildTargetPath),
            slnFile is not null ? BuildTargetKind.Solution : BuildTargetKind.Project);

        var bumpTargets = slnFile is not null
            ? EnumerateFromSolution(slnFile, ignoreProjects, fs, sln, log)
            : new List<BumpTarget>
            {
                new(csprojFile!, Path.GetFileNameWithoutExtension(csprojFile!), true, null),
            };

        return new Workspace(startPath, slnFile, csprojFile, buildTarget, bumpTargets);
    }

    private static (string? Sln, string? Csproj) ResolveBuildFiles(string startPath, IFileSystem fs)
    {
        var sln = FindSolutionFile(startPath, fs);
        var csproj = FindProjectFile(startPath, fs);
        if (sln is null && csproj is null)
        {
            var srcDir = Path.Combine(startPath, "src");
            if (fs.DirExists(srcDir))
            {
                sln = FindSolutionFile(srcDir, fs);
                csproj = FindProjectFile(srcDir, fs);
            }
        }
        return (sln, csproj);
    }

    private static DevConfig? LoadDevConfig(string startPath, IFileSystem fs)
    {
        var devConfigFile = Path.Combine(startPath, "dev.json");
        if (!fs.FileExists(devConfigFile))
        {
            var parent = fs.GetParentDir(startPath);
            if (parent is null) return null;
            var parentConfigFile = Path.Combine(parent, "dev.json");
            if (!fs.FileExists(parentConfigFile)) return null;
            devConfigFile = parentConfigFile;
        }

        try
        {
            return JsonSerializer.Deserialize<DevConfig>(
                fs.ReadAllText(devConfigFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            throw new WorkspaceDiscoveryException("config_parse_error", "Error reading dev.json", ex);
        }
    }

    private static string? FindSolutionFile(string searchPath, IFileSystem fs) =>
        fs.GetFiles(searchPath, "*.sln*")
          .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                   || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
          .OrderBy(f => f.Length)
          .FirstOrDefault();

    private static string? FindProjectFile(string searchPath, IFileSystem fs) =>
        fs.GetFiles(searchPath, "*.csproj").FirstOrDefault();

    private static List<BumpTarget> EnumerateFromSolution(
        string slnFile,
        IReadOnlyCollection<string>? ignoreProjects,
        IFileSystem fs,
        ISolutionReader sln,
        Action<string>? log)
    {
        var targets = new List<BumpTarget>();
        List<string> projectPaths;
        try
        {
            projectPaths = sln.ReadProjectPaths(slnFile).ToList();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[red]Error opening solution file:[/] {ex.Message}");
            return targets;
        }

        foreach (var projectPath in projectPaths)
        {
            var displayName = Path.GetFileNameWithoutExtension(projectPath);

            if (IsInGitSubmodule(projectPath, fs))
            {
                log?.Invoke($"[yellow]Skipping[/] {displayName} [yellow](inside git submodule)[/]");
                targets.Add(new BumpTarget(projectPath, displayName, false, "submodule"));
                continue;
            }

            if (ignoreProjects?.Any(p => string.Equals(p, displayName, StringComparison.OrdinalIgnoreCase)) == true)
            {
                log?.Invoke($"[yellow]Skipping[/] {displayName} [yellow](configured in dev.json)[/]");
                targets.Add(new BumpTarget(projectPath, displayName, false, "ignored"));
                continue;
            }

            targets.Add(new BumpTarget(projectPath, displayName, true, null));
        }
        return targets;
    }

    private static bool IsInGitSubmodule(string filePath, IFileSystem fs)
    {
        try
        {
            var current = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(current))
            {
                var gitPath = Path.Combine(current, ".git");
                if (fs.FileExists(gitPath)) return true;
                if (fs.DirExists(gitPath)) return false;
                current = fs.GetParentDir(current);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}

public readonly record struct BuildTarget(string Path, string DisplayName, BuildTargetKind Kind);

public enum BuildTargetKind { Solution, Project }

public readonly record struct BumpTarget(string Path, string DisplayName, bool Include, string? SkipReason);

public sealed class WorkspaceDiscoveryException : Exception
{
    public string Code { get; }
    public WorkspaceDiscoveryException(string code, string message, Exception? inner = null)
        : base(message, inner)
        => Code = code;
}

internal interface IFileSystem
{
    bool FileExists(string path);
    bool DirExists(string path);
    string[] GetFiles(string dir, string pattern);
    string ReadAllText(string path);
    string? GetParentDir(string path);
}

internal sealed class RealFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirExists(string path) => Directory.Exists(path);
    public string[] GetFiles(string dir, string pattern) => Directory.GetFiles(dir, pattern);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public string? GetParentDir(string path) => Directory.GetParent(path)?.FullName;
}

internal interface ISolutionReader
{
    // Returns project paths already absolute-normalized (backslash → platform separator,
    // relative-to-sln → absolute).
    IEnumerable<string> ReadProjectPaths(string slnPath);
}

internal sealed class SolutionPersistenceReader : ISolutionReader
{
    public IEnumerable<string> ReadProjectPaths(string slnPath)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(slnPath)
            ?? throw new InvalidOperationException($"Unable to find a serializer for {slnPath}");

        var solution = serializer.OpenAsync(slnPath, CancellationToken.None).GetAwaiter().GetResult();
        var slnDir = Path.GetDirectoryName(slnPath) ?? "";
        var result = new List<string>();
        foreach (var project in solution.SolutionProjects)
        {
            var projectPath = project.FilePath?.Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(projectPath)) continue;
            if (!Path.IsPathRooted(projectPath))
                projectPath = Path.Combine(slnDir, projectPath);
            result.Add(projectPath);
        }
        return result;
    }
}
