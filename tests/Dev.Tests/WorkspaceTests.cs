using Xunit;

namespace Dev.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void Discover_returns_null_when_no_sln_or_csproj_in_cwd_or_src()
    {
        var fs = new FakeFileSystem();
        var sln = new FakeSolutionReader();

        var ws = Workspace.Discover("/empty", log: null, fs, sln);

        Assert.Null(ws);
    }

    [Fact]
    public void Discover_finds_sln_in_cwd()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/MyApp.sln", "")
            .WithFile("/repo/MyApp.csproj", "");
        var sln = new FakeSolutionReader();
        sln.ReturnsForSolution("/repo/MyApp.sln", new[] { "/repo/MyApp.csproj" });

        var ws = Workspace.Discover("/repo", log: null, fs, sln);

        Assert.NotNull(ws);
        Assert.Equal("/repo/MyApp.sln", ws!.SolutionPath);
        Assert.Equal("/repo/MyApp.csproj", ws.ProjectPath);
        Assert.Equal(BuildTargetKind.Solution, ws.BuildTarget.Kind);
        Assert.Equal("/repo/MyApp.sln", ws.BuildTarget.Path);
        Assert.Equal("MyApp.sln", ws.BuildTarget.DisplayName);
    }

    [Fact]
    public void Discover_falls_back_to_src_when_cwd_has_neither_sln_nor_csproj()
    {
        var fs = new FakeFileSystem()
            .WithDir("/repo/src")
            .WithFile("/repo/src/MyApp.sln", "");
        var sln = new FakeSolutionReader();
        sln.ReturnsForSolution("/repo/src/MyApp.sln", Array.Empty<string>());

        var ws = Workspace.Discover("/repo", log: null, fs, sln);

        Assert.NotNull(ws);
        Assert.Equal("/repo/src/MyApp.sln", ws!.SolutionPath);
    }

    [Fact]
    public void Discover_does_not_fall_back_to_src_when_cwd_has_a_csproj()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/Cwd.csproj", "")
            .WithDir("/repo/src")
            .WithFile("/repo/src/Other.sln", "");
        var sln = new FakeSolutionReader();
        sln.ReturnsForSolution("/repo/src/Other.sln", Array.Empty<string>());

        var ws = Workspace.Discover("/repo", log: null, fs, sln);

        Assert.NotNull(ws);
        Assert.Null(ws!.SolutionPath);
        Assert.Equal("/repo/Cwd.csproj", ws.ProjectPath);
        Assert.Equal(BuildTargetKind.Project, ws.BuildTarget.Kind);
    }

    [Fact]
    public void Discover_picks_shorter_filename_when_both_sln_and_slnx_exist()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/Project.sln", "")
            .WithFile("/repo/Project.slnx", "");
        var sln = new FakeSolutionReader();
        sln.ReturnsForSolution("/repo/Project.sln", Array.Empty<string>());

        var ws = Workspace.Discover("/repo", log: null, fs, sln);

        Assert.Equal("/repo/Project.sln", ws!.SolutionPath); // shorter wins
    }

    [Fact]
    public void Discover_single_csproj_yields_exactly_one_bumptarget_with_include_true()
    {
        var fs = new FakeFileSystem().WithFile("/repo/Lone.csproj", "");
        var ws = Workspace.Discover("/repo", log: null, fs, new FakeSolutionReader());

        var only = Assert.Single(ws!.EnumerateBumpTargets());
        Assert.True(only.Include);
        Assert.Null(only.SkipReason);
        Assert.Equal("/repo/Lone.csproj", only.Path);
        Assert.Equal("Lone", only.DisplayName);
    }

    [Fact]
    public void EnumerateBumpTargets_marks_submodule_projects_with_skip_reason()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/App.sln", "")
            .WithDir("/repo")
            .WithDir("/repo/.git") // top-level repo
            .WithDir("/repo/sub")
            .WithFile("/repo/sub/.git", ""); // submodule marker (file, not dir)
        var sln = new FakeSolutionReader();
        sln.ReturnsForSolution("/repo/App.sln", new[]
        {
            "/repo/MainProject.csproj",
            "/repo/sub/SubProject.csproj",
        });

        var ws = Workspace.Discover("/repo", log: null, fs, sln);
        var targets = ws!.EnumerateBumpTargets();

        Assert.Equal(2, targets.Count);
        Assert.True(targets[0].Include);
        Assert.False(targets[1].Include);
        Assert.Equal("submodule", targets[1].SkipReason);
    }

    [Fact]
    public void EnumerateBumpTargets_filters_ignored_projects_case_insensitively()
    {
        var devJson = """{ "IgnoreProjects": ["IGNOREDONE", "ignoredtwo"] }""";
        var fs = new FakeFileSystem()
            .WithFile("/repo/App.sln", "")
            .WithFile("/repo/dev.json", devJson)
            .WithDir("/repo/.git"); // a top-level git dir so submodule walk terminates as "not submodule"
        var sln = new FakeSolutionReader();
        sln.ReturnsForSolution("/repo/App.sln", new[]
        {
            "/repo/Kept.csproj",
            "/repo/IgnoredOne.csproj",
            "/repo/IgnoredTwo.csproj",
        });

        var ws = Workspace.Discover("/repo", log: null, fs, sln);
        var targets = ws!.EnumerateBumpTargets();

        Assert.True(targets[0].Include);
        Assert.False(targets[1].Include);
        Assert.Equal("ignored", targets[1].SkipReason);
        Assert.False(targets[2].Include);
        Assert.Equal("ignored", targets[2].SkipReason);
    }

    [Fact]
    public void Discover_inherits_dev_json_from_parent_when_absent_in_cwd()
    {
        var devJson = """{ "IgnoreProjects": ["Skipped"] }""";
        var fs = new FakeFileSystem()
            .WithFile("/parent/dev.json", devJson)
            .WithFile("/parent/repo/App.sln", "")
            .WithDir("/parent/repo/.git")
            .WithParent("/parent/repo", "/parent");
        var sln = new FakeSolutionReader();
        sln.ReturnsForSolution("/parent/repo/App.sln", new[]
        {
            "/parent/repo/Kept.csproj",
            "/parent/repo/Skipped.csproj",
        });

        var ws = Workspace.Discover("/parent/repo", log: null, fs, sln);
        var targets = ws!.EnumerateBumpTargets();

        Assert.True(targets[0].Include);
        Assert.False(targets[1].Include);
        Assert.Equal("ignored", targets[1].SkipReason);
    }

    [Fact]
    public void Discover_throws_WorkspaceDiscoveryException_on_invalid_dev_json()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/dev.json", "{not valid json")
            .WithFile("/repo/App.csproj", "");

        var ex = Assert.Throws<WorkspaceDiscoveryException>(
            () => Workspace.Discover("/repo", log: null, fs, new FakeSolutionReader()));

        Assert.Equal("config_parse_error", ex.Code);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Solution_reader_failure_yields_empty_bump_targets_and_logs()
    {
        var fs = new FakeFileSystem().WithFile("/repo/Broken.sln", "");
        var sln = new FakeSolutionReader();
        sln.ThrowsForSolution("/repo/Broken.sln", new InvalidOperationException("bad sln"));
        var logs = new List<string>();

        var ws = Workspace.Discover("/repo", logs.Add, fs, sln);

        Assert.NotNull(ws);
        Assert.Empty(ws!.EnumerateBumpTargets());
        Assert.Contains(logs, l => l.Contains("Error opening solution file"));
    }
}

internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new();
    private readonly HashSet<string> _dirs = new();
    private readonly Dictionary<string, string> _parents = new();

    // Always normalize to forward-slash internally so tests stay portable across
    // Windows (where Path.Combine yields backslashes) and Linux/macOS.
    private static string Norm(string path) => path.Replace('\\', '/');

    public FakeFileSystem WithFile(string path, string content)
    {
        var n = Norm(path);
        _files[n] = content;
        var dir = System.IO.Path.GetDirectoryName(n);
        if (!string.IsNullOrEmpty(dir)) _dirs.Add(dir);
        return this;
    }

    public FakeFileSystem WithDir(string path)
    {
        _dirs.Add(Norm(path));
        return this;
    }

    public FakeFileSystem WithParent(string child, string parent)
    {
        _parents[Norm(child)] = Norm(parent);
        return this;
    }

    public bool FileExists(string path) => _files.ContainsKey(Norm(path));
    public bool DirExists(string path) => _dirs.Contains(Norm(path));

    public string[] GetFiles(string dir, string pattern)
    {
        var prefix = Norm(dir);
        if (!prefix.EndsWith('/')) prefix += '/';
        return _files.Keys
            .Where(p => p.StartsWith(prefix) && !p[prefix.Length..].Contains('/'))
            .Where(p => MatchesGlob(System.IO.Path.GetFileName(p), pattern))
            .ToArray();
    }

    public string ReadAllText(string path)
        => _files.TryGetValue(Norm(path), out var c) ? c : throw new FileNotFoundException(path);

    public string? GetParentDir(string path)
    {
        var n = Norm(path);
        if (_parents.TryGetValue(n, out var p)) return p;
        var idx = n.LastIndexOf('/');
        if (idx <= 0) return null;
        return n[..idx];
    }

    private static bool MatchesGlob(string name, string pattern)
    {
        // Tiny matcher: supports "*.ext" and "*.sln*".
        if (pattern == "*") return true;
        if (pattern.StartsWith("*."))
        {
            var rest = pattern[1..];
            if (rest.EndsWith('*'))
            {
                var stem = rest[..^1];
                return name.Contains(stem, StringComparison.OrdinalIgnoreCase);
            }
            return name.EndsWith(rest, StringComparison.OrdinalIgnoreCase);
        }
        return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class FakeSolutionReader : ISolutionReader
{
    private readonly Dictionary<string, IEnumerable<string>> _responses = new();
    private readonly Dictionary<string, Exception> _throws = new();

    public void ReturnsForSolution(string slnPath, IEnumerable<string> projects)
        => _responses[slnPath] = projects;

    public void ThrowsForSolution(string slnPath, Exception ex)
        => _throws[slnPath] = ex;

    public IEnumerable<string> ReadProjectPaths(string slnPath)
    {
        if (_throws.TryGetValue(slnPath, out var ex)) throw ex;
        return _responses.TryGetValue(slnPath, out var p) ? p : Array.Empty<string>();
    }
}
