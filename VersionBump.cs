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
        try
        {
            return new ProjectVersionRead(projectPath, true, raw, new SemVer(raw), null);
        }
        catch (FormatException)
        {
            return new ProjectVersionRead(projectPath, true, raw, null, "malformed_version");
        }
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

    // Every value here (project path, commit message, tag name) is delivered as a
    // distinct ArgumentList element via ExecArgs — never concatenated into an argv
    // string. The tag/message carry version data whose suffix/build-metadata comes
    // from a permissive csproj <Version> regex, so an interpolated string would let
    // a crafted version inject extra git arguments (split on whitespace) or break
    // out of -m "…" quoting. argv form makes each value one opaque token.
    //
    // The leading `--` on `add` stops a path beginning with `-` from being read as
    // an option. `tag` and `commit -m` need no such guard: the tag name always
    // starts with a numeric major component (SemVer.ToString), and `-m` always
    // consumes the very next token as its value regardless of content.
    public (int Exit, string[] Stderr, string[] Stdout) Add(string path)
    {
        var r = _runner.Run(ProcSpec.ExecArgs("git", new[] { "add", "--", path }), _ctx);
        return (r.ExitCode, r.StderrTail, r.StdoutTail);
    }

    public (int Exit, string[] Stderr, string[] Stdout) Commit(string message)
    {
        var r = _runner.Run(ProcSpec.ExecArgs("git", new[] { "commit", "-m", message }), _ctx);
        return (r.ExitCode, r.StderrTail, r.StdoutTail);
    }

    public (int Exit, string[] Stderr, string[] Stdout) Tag(string name)
    {
        var r = _runner.Run(ProcSpec.ExecArgs("git", new[] { "tag", name }), _ctx);
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
    string? Message = null,
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
        // Phase 1 (pure): read every project, compute the new version. No writes,
        // no git side-effects yet — so the cross-project agreement invariant can
        // be checked before any disk state changes.
        var planned = new List<PlannedBump>(plan.Projects.Count);
        var newVersions = new HashSet<string>();
        foreach (var c in plan.Projects)
            planned.Add(PlanOne(c, plan.Spec, newVersions));

        // Phase 2: invariant gate. If projects disagree, abort BEFORE writing.
        if (newVersions.Count > 1)
        {
            _log?.Invoke("[red]Multiple versions found to bump. Please commit them separately.[/]");
            var aborted = planned.Select(p => p.Bumped is not null
                ? new ProjectBumpResult(p.Cand.Path, p.Read.RawVersion, p.Bumped.ToString(), false, "multiple_versions_blocked")
                : new ProjectBumpResult(p.Cand.Path, null, null, false, p.Reason)).ToList();
            return new BumpOutcome(aborted, new GitOutcome(false, null, "multiple_versions"));
        }

        // Phase 3: apply (write + stage). Safe now — invariant holds.
        var results = new List<ProjectBumpResult>(planned.Count);
        foreach (var p in planned)
        {
            if (p.Bumped is null)
            {
                results.Add(new ProjectBumpResult(p.Cand.Path, null, null, false, p.Reason));
                continue;
            }
            var bumpedStr = p.Bumped.ToString();
            _log?.Invoke($"[green]Bumping[/] {Path.GetFileNameWithoutExtension(p.Cand.Path)} [green]from[/] [blue]{p.Read.RawVersion}[/] [green]to[/] [yellow]{bumpedStr}[/]");
            if (!plan.Options.DryRun)
                _files.Write(p.Cand.Path, p.Bumped);
            results.Add(new ProjectBumpResult(p.Cand.Path, p.Read.RawVersion, bumpedStr, true, null));
            if (plan.Options.StageChanges && !plan.Options.DryRun)
                _git.Add(p.Cand.Path);
        }

        return new BumpOutcome(results, ResolveGit(plan, newVersions));
    }

    private PlannedBump PlanOne(BumpTarget c, BumpSpec spec, HashSet<string> newVersions)
    {
        if (!c.Include)
            return new PlannedBump(c, default, null, c.SkipReason);

        var read = _files.Read(c.Path);
        if (!read.Exists)
        {
            _log?.Invoke($"[red]Project {Path.GetFileNameWithoutExtension(c.Path)} not found, skipping.[/]");
            return new PlannedBump(c, read, null, read.SkipReason ?? "not_found");
        }
        if (read.Version is null)
        {
            var reason = read.SkipReason ?? "no_version_tag";
            _log?.Invoke($"[red]Cannot bump {Path.GetFileNameWithoutExtension(c.Path)} ({reason}).[/]");
            return new PlannedBump(c, read, null, reason);
        }

        var bumped = read.Version.Bump(spec);
        newVersions.Add(bumped.ToString());
        return new PlannedBump(c, read, bumped, null);
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

        var nv = newVersions.First();
        var msg = plan.Options.CommitMessage?.Invoke(nv) ?? $"build: {nv}";
        _log?.Invoke($"[green]Committing and tagging version {nv}...[/]");

        if (plan.Options.DryRun)
            return new GitOutcome(true, nv, null, msg);

        var commit = plan.Options.Commit
            ? _git.Commit(msg)
            : (Exit: 0, Stderr: Array.Empty<string>(), Stdout: Array.Empty<string>());

        // Short-circuit: if `git commit` fails, do not run `git tag`. Tagging
        // would otherwise land on the previous commit, not the intended one.
        if (commit.Exit != 0)
        {
            return new GitOutcome(
                false, null, "git_commit_failed", msg,
                commit.Exit, null,
                commit.Stderr, commit.Stdout);
        }

        var tag = plan.Options.Tag
            ? _git.Tag(nv)
            : (Exit: 0, Stderr: Array.Empty<string>(), Stdout: Array.Empty<string>());

        if (tag.Exit != 0)
        {
            return new GitOutcome(
                true, null, "git_tag_failed", msg,
                commit.Exit, tag.Exit,
                tag.Stderr, tag.Stdout);
        }

        return new GitOutcome(true, nv, null, msg, commit.Exit, tag.Exit);
    }

    private readonly record struct PlannedBump(
        BumpTarget Cand, ProjectVersionRead Read, SemVer? Bumped, string? Reason);
}

internal sealed record ProjectBumpResult(string Path, string? From, string? To, bool Bumped, string? Reason);
