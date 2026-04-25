// Version-bump pipeline: pure SemVer math (in SemVer.cs) is composed here with a
// file-mutation port (IProjectVersionFile) and a git port (IGitPort). The pipeline
// owns the cross-project version-agreement invariant and the commit-then-tag
// sequencing; call sites translate the BumpOutcome into the CLI envelope shape.

using System.Text.RegularExpressions;

namespace Dev;

internal interface IProjectVersionFile
{
    ProjectVersionRead Read(string projectPath);
    void Write(string projectPath, SemVer newVersion);
}

internal readonly record struct ProjectVersionRead(
    string Path, bool Exists, string? RawVersion, SemVer? Version, string? SkipReason);

internal sealed partial class CsprojVersionFile : IProjectVersionFile
{
    public ProjectVersionRead Read(string projectPath)
    {
        if (!File.Exists(projectPath))
            return new ProjectVersionRead(projectPath, false, null, null, "not_found");

        var csproj = File.ReadAllText(projectPath);
        var match = VersionTagRegex().Match(csproj);
        if (!match.Success)
            return new ProjectVersionRead(projectPath, true, null, null, "no_version_tag");

        var raw = match.Groups["version"].Value;
        return new ProjectVersionRead(projectPath, true, raw, new SemVer(raw), null);
    }

    public void Write(string projectPath, SemVer newVersion)
    {
        var csproj = File.ReadAllText(projectPath);
        csproj = VersionTagRegex().Replace(csproj, $"<Version>{newVersion}</Version>");
        File.WriteAllText(projectPath, csproj);
    }

    [GeneratedRegex("<Version>(?<version>.*)</Version>")]
    private static partial Regex VersionTagRegex();
}

internal interface IGitPort
{
    (int Exit, string[] Stderr, string[] Stdout) Add(string path);
    (int Exit, string[] Stderr, string[] Stdout) Commit(string message);
    (int Exit, string[] Stderr, string[] Stdout) Tag(string name);
}

internal sealed class ProcessGitPort : IGitPort
{
    private readonly IProcessRunner _runner;
    private readonly RunContext _ctx;

    public ProcessGitPort(IProcessRunner runner, RunContext ctx)
    {
        _runner = runner;
        _ctx = ctx;
    }

    public (int Exit, string[] Stderr, string[] Stdout) Add(string path)
    {
        var r = _runner.Run(ProcSpec.Exec("git", $"add \"{path}\""), _ctx);
        return (r.ExitCode, r.StderrTail, r.StdoutTail);
    }

    public (int Exit, string[] Stderr, string[] Stdout) Commit(string message)
    {
        var r = _runner.Run(ProcSpec.Exec("git", $"commit -m \"{message}\""), _ctx);
        return (r.ExitCode, r.StderrTail, r.StdoutTail);
    }

    public (int Exit, string[] Stderr, string[] Stdout) Tag(string name)
    {
        var r = _runner.Run(ProcSpec.Exec("git", $"tag {name}"), _ctx);
        return (r.ExitCode, r.StderrTail, r.StdoutTail);
    }
}

internal sealed record BumpPlan(
    IReadOnlyList<BumpTarget> Projects,
    BumpSpec Spec,
    BumpOptions Options);

internal sealed record BumpOptions(
    bool DryRun = false,
    bool StageChanges = false,
    bool Commit = false,
    bool Tag = false,
    Func<string, string>? CommitMessage = null)
{
    public static BumpOptions CommitAndTag() => new(StageChanges: true, Commit: true, Tag: true);
}

internal sealed record BumpOutcome(
    IReadOnlyList<ProjectBumpResult> Projects,
    GitOutcome Git);

internal sealed record GitOutcome(
    bool Committed,
    string? Tag,
    string? Reason,
    int? CommitExit = null,
    int? TagExit = null,
    string[]? StderrTail = null,
    string[]? StdoutTail = null);

internal sealed class BumpPipeline
{
    private readonly IProjectVersionFile _files;
    private readonly IGitPort _git;
    private readonly Action<string>? _log;

    public BumpPipeline(IProjectVersionFile files, IGitPort git, Action<string>? log = null)
    {
        _files = files;
        _git = git;
        _log = log;
    }

    public BumpOutcome Execute(BumpPlan plan)
    {
        var results = new List<ProjectBumpResult>();
        var newVersions = new HashSet<string>();

        foreach (var c in plan.Projects)
        {
            if (!c.Include)
            {
                results.Add(new ProjectBumpResult(c.Path, null, null, false, c.SkipReason));
                continue;
            }

            var read = _files.Read(c.Path);
            if (!read.Exists)
            {
                _log?.Invoke($"[red]Project {Path.GetFileNameWithoutExtension(c.Path)} not found, skipping.[/]");
                results.Add(new ProjectBumpResult(c.Path, null, null, false, "not_found"));
                continue;
            }
            if (read.Version is null)
            {
                _log?.Invoke($"[red]No version found in {Path.GetFileNameWithoutExtension(c.Path)}.[/]");
                results.Add(new ProjectBumpResult(c.Path, null, null, false, "no_version_tag"));
                continue;
            }

            var bumped = read.Version.Bump(plan.Spec);
            var bumpedStr = bumped.ToString();
            _log?.Invoke($"[green]Bumping[/] {Path.GetFileNameWithoutExtension(c.Path)} [green]from[/] [blue]{read.RawVersion}[/] [green]to[/] [yellow]{bumpedStr}[/]");

            if (!plan.Options.DryRun)
                _files.Write(c.Path, bumped);

            results.Add(new ProjectBumpResult(c.Path, read.RawVersion, bumpedStr, true, null));
            newVersions.Add(bumpedStr);

            if (plan.Options.StageChanges && !plan.Options.DryRun)
                _git.Add(c.Path);
        }

        return new BumpOutcome(results, ResolveGit(plan, newVersions));
    }

    private GitOutcome ResolveGit(BumpPlan plan, HashSet<string> newVersions)
    {
        if (!plan.Options.Commit && !plan.Options.Tag)
            return new GitOutcome(false, null, null);

        if (newVersions.Count == 0)
        {
            _log?.Invoke("[yellow]No versions found to bump.[/]");
            return new GitOutcome(false, null, "no_bump");
        }
        if (newVersions.Count > 1)
        {
            _log?.Invoke("[red]Multiple versions found to bump. Please commit them separately.[/]");
            return new GitOutcome(false, null, "multiple_versions");
        }

        var nv = newVersions.First();
        var msg = plan.Options.CommitMessage?.Invoke(nv) ?? $"build: {nv}";
        _log?.Invoke($"[green]Committing and tagging version {nv}...[/]");

        if (plan.Options.DryRun)
            return new GitOutcome(true, nv, null);

        var commit = plan.Options.Commit
            ? _git.Commit(msg)
            : (Exit: 0, Stderr: Array.Empty<string>(), Stdout: Array.Empty<string>());
        var tag = plan.Options.Tag
            ? _git.Tag(nv)
            : (Exit: 0, Stderr: Array.Empty<string>(), Stdout: Array.Empty<string>());

        if (commit.Exit == 0 && tag.Exit == 0)
            return new GitOutcome(true, nv, null, commit.Exit, tag.Exit);

        return new GitOutcome(
            false, null, "git_failed",
            commit.Exit, tag.Exit,
            commit.Stderr.Concat(tag.Stderr).ToArray(),
            commit.Stdout.Concat(tag.Stdout).ToArray());
    }
}

internal sealed record ProjectBumpResult(string Path, string? From, string? To, bool Bumped, string? Reason);
