using Xunit;

namespace Dev.Tests;

public sealed class BumpPipelineTests
{
    [Fact]
    public void Skipped_workspace_candidate_passes_through_with_its_reason_untouched()
    {
        var files = new InMemoryProjectVersionFile();
        var git = new FakeGitPort();
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[] { new ProjectCandidate("a.csproj", false, "submodule") },
            new BumpSpec(BumpPart.Minor),
            new BumpOptions()));

        var only = Assert.Single(outcome.Projects);
        Assert.Equal("a.csproj", only.Path);
        Assert.False(only.Bumped);
        Assert.Equal("submodule", only.Reason);
        Assert.Empty(git.AddCalls);
    }

    [Fact]
    public void File_port_skip_reason_propagates_to_result()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetMissing("missing.csproj");
        files.SetUntagged("untagged.csproj");

        var pipeline = new BumpPipeline(files, new FakeGitPort());
        var outcome = pipeline.Execute(new BumpPlan(
            new[]
            {
                new ProjectCandidate("missing.csproj", true, null),
                new ProjectCandidate("untagged.csproj", true, null),
            },
            new BumpSpec(BumpPart.Patch),
            new BumpOptions()));

        Assert.Equal("not_found", outcome.Projects[0].Reason);
        Assert.Equal("no_version_tag", outcome.Projects[1].Reason);
        Assert.False(outcome.Projects[0].Bumped);
        Assert.False(outcome.Projects[1].Bumped);
    }

    [Fact]
    public void All_match_commits_and_tags_a_single_version()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.2.3");
        files.SetVersion("b.csproj", "1.2.3");
        var git = new FakeGitPort();
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[]
            {
                new ProjectCandidate("a.csproj", true, null),
                new ProjectCandidate("b.csproj", true, null),
            },
            new BumpSpec(BumpPart.Minor),
            BumpOptions.CommitAndTag()));

        Assert.True(outcome.Git.Committed);
        Assert.Equal("1.3.0", outcome.Git.Tag);
        Assert.Null(outcome.Git.Reason);
        Assert.Equal(new[] { "a.csproj", "b.csproj" }, git.AddCalls);
        Assert.Equal("build: 1.3.0", git.CommitCalls.Single());
        Assert.Equal("1.3.0", git.TagCalls.Single());

        Assert.Equal("1.3.0", files.WrittenVersion("a.csproj"));
        Assert.Equal("1.3.0", files.WrittenVersion("b.csproj"));
    }

    [Fact]
    public void Multi_project_mismatched_start_versions_block_commit_with_multiple_versions_reason()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.0.0");
        files.SetVersion("b.csproj", "2.0.0");
        var git = new FakeGitPort();
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[]
            {
                new ProjectCandidate("a.csproj", true, null),
                new ProjectCandidate("b.csproj", true, null),
            },
            new BumpSpec(BumpPart.Patch),
            BumpOptions.CommitAndTag()));

        Assert.False(outcome.Git.Committed);
        Assert.Equal("multiple_versions", outcome.Git.Reason);
        Assert.Empty(git.CommitCalls);
        Assert.Empty(git.TagCalls);
        // Invariant gate fires before any disk writes — neither file is mutated
        // and neither was staged. Each project's result reports the planned new
        // version with reason `multiple_versions_blocked` for transparency.
        Assert.Null(files.WrittenVersion("a.csproj"));
        Assert.Null(files.WrittenVersion("b.csproj"));
        Assert.Empty(git.AddCalls);
        Assert.All(outcome.Projects, p =>
        {
            Assert.False(p.Bumped);
            Assert.Equal("multiple_versions_blocked", p.Reason);
        });
    }

    [Fact]
    public void Zero_bumped_projects_yields_no_bump_reason()
    {
        var pipeline = new BumpPipeline(new InMemoryProjectVersionFile(), new FakeGitPort());
        var outcome = pipeline.Execute(new BumpPlan(
            Array.Empty<ProjectCandidate>(),
            new BumpSpec(BumpPart.Minor),
            BumpOptions.CommitAndTag()));

        Assert.False(outcome.Git.Committed);
        Assert.Equal("no_bump", outcome.Git.Reason);
    }

    [Fact]
    public void Commit_failure_short_circuits_and_does_not_run_tag()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.0.0");
        var git = new FakeGitPort();
        git.NextCommit = (1, new[] { "commit err" }, new[] { "commit out" });
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[] { new ProjectCandidate("a.csproj", true, null) },
            new BumpSpec(BumpPart.Patch),
            BumpOptions.CommitAndTag()));

        Assert.False(outcome.Git.Committed);
        Assert.Equal("git_commit_failed", outcome.Git.Reason);
        Assert.Equal(1, outcome.Git.CommitExit);
        Assert.Null(outcome.Git.TagExit);
        Assert.Single(git.CommitCalls);
        Assert.Empty(git.TagCalls); // tag skipped: would otherwise land on previous commit
        Assert.Equal(new[] { "commit err" }, outcome.Git.StderrTail);
        Assert.Equal(new[] { "commit out" }, outcome.Git.StdoutTail);
    }

    [Fact]
    public void Tag_failure_when_commit_succeeds_reports_git_tag_failed_with_committed_true()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.0.0");
        var git = new FakeGitPort();
        git.NextTag = (1, new[] { "tag err" }, Array.Empty<string>());
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[] { new ProjectCandidate("a.csproj", true, null) },
            new BumpSpec(BumpPart.Patch),
            BumpOptions.CommitAndTag()));

        Assert.True(outcome.Git.Committed); // commit landed; only the tag failed
        Assert.Equal("git_tag_failed", outcome.Git.Reason);
        Assert.Equal(0, outcome.Git.CommitExit);
        Assert.Equal(1, outcome.Git.TagExit);
    }

    [Fact]
    public void DryRun_produces_full_From_To_list_without_writes_or_git_calls()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.2.3");
        var git = new FakeGitPort();
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[] { new ProjectCandidate("a.csproj", true, null) },
            new BumpSpec(BumpPart.Minor),
            new BumpOptions(DryRun: true, StageChanges: true, Commit: true, Tag: true)));

        var only = Assert.Single(outcome.Projects);
        Assert.True(only.Bumped);
        Assert.Equal("1.2.3", only.From);
        Assert.Equal("1.3.0", only.To);

        Assert.True(outcome.Git.Committed);
        Assert.Equal("1.3.0", outcome.Git.Tag);

        Assert.Null(files.WrittenVersion("a.csproj"));
        Assert.Empty(git.AddCalls);
        Assert.Empty(git.CommitCalls);
        Assert.Empty(git.TagCalls);
    }

    [Fact]
    public void Custom_commit_message_is_passed_through_and_returned_in_outcome()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.0.0");
        var git = new FakeGitPort();
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[] { new ProjectCandidate("a.csproj", true, null) },
            new BumpSpec(BumpPart.Patch),
            new BumpOptions(StageChanges: true, Commit: true, Tag: true,
                            CommitMessage: v => $"chore(release): {v}")));

        Assert.True(outcome.Git.Committed);
        Assert.Equal("chore(release): 1.0.1", git.CommitCalls.Single());
        Assert.Equal("chore(release): 1.0.1", outcome.Git.Message);
    }

    [Fact]
    public void Default_commit_message_is_returned_in_outcome()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.0.0");
        var pipeline = new BumpPipeline(files, new FakeGitPort());

        var outcome = pipeline.Execute(new BumpPlan(
            new[] { new ProjectCandidate("a.csproj", true, null) },
            new BumpSpec(BumpPart.Patch),
            BumpOptions.CommitAndTag()));

        Assert.Equal("build: 1.0.1", outcome.Git.Message);
    }

    [Fact]
    public void Malformed_version_string_is_skipped_with_dedicated_reason()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetMalformed("bad.csproj", "not.a.version");
        files.SetVersion("ok.csproj", "1.0.0");
        var git = new FakeGitPort();
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[]
            {
                new ProjectCandidate("bad.csproj", true, null),
                new ProjectCandidate("ok.csproj", true, null),
            },
            new BumpSpec(BumpPart.Patch),
            BumpOptions.CommitAndTag()));

        Assert.Equal("malformed_version", outcome.Projects[0].Reason);
        Assert.False(outcome.Projects[0].Bumped);
        Assert.True(outcome.Projects[1].Bumped);
        Assert.Equal("1.0.1", outcome.Projects[1].To);
        // Pipeline does not abort the run — the malformed project is skipped and
        // the rest proceed normally.
        Assert.True(outcome.Git.Committed);
    }

    [Fact]
    public void StageChanges_false_skips_git_add_but_still_writes_file()
    {
        var files = new InMemoryProjectVersionFile();
        files.SetVersion("a.csproj", "1.0.0");
        var git = new FakeGitPort();
        var pipeline = new BumpPipeline(files, git);

        var outcome = pipeline.Execute(new BumpPlan(
            new[] { new ProjectCandidate("a.csproj", true, null) },
            new BumpSpec(BumpPart.Patch),
            new BumpOptions()));

        Assert.True(outcome.Projects.Single().Bumped);
        Assert.Equal("1.0.1", files.WrittenVersion("a.csproj"));
        Assert.Empty(git.AddCalls);
    }
}

internal sealed class InMemoryProjectVersionFile : IProjectVersionFile
{
    private readonly Dictionary<string, string?> _versions = new();
    private readonly Dictionary<string, string> _malformed = new();
    private readonly HashSet<string> _missing = new();
    private readonly HashSet<string> _untagged = new();
    private readonly Dictionary<string, string> _written = new();

    public void SetVersion(string path, string version) => _versions[path] = version;
    public void SetMalformed(string path, string raw) => _malformed[path] = raw;
    public void SetMissing(string path) => _missing.Add(path);
    public void SetUntagged(string path) => _untagged.Add(path);
    public string? WrittenVersion(string path) => _written.TryGetValue(path, out var v) ? v : null;

    public ProjectVersionRead Read(string projectPath)
    {
        if (_missing.Contains(projectPath))
            return new ProjectVersionRead(projectPath, false, null, null, "not_found");
        if (_untagged.Contains(projectPath))
            return new ProjectVersionRead(projectPath, true, null, null, "no_version_tag");
        if (_malformed.TryGetValue(projectPath, out var bad))
            return new ProjectVersionRead(projectPath, true, bad, null, "malformed_version");
        if (_versions.TryGetValue(projectPath, out var raw) && raw is not null)
            return new ProjectVersionRead(projectPath, true, raw, new SemVer(raw), null);
        return new ProjectVersionRead(projectPath, false, null, null, "not_found");
    }

    public void Write(string projectPath, SemVer newVersion)
    {
        _written[projectPath] = newVersion.ToString();
    }
}

internal sealed class FakeGitPort : IGitPort
{
    public List<string> AddCalls { get; } = new();
    public List<string> CommitCalls { get; } = new();
    public List<string> TagCalls { get; } = new();

    public (int Exit, string[] Stderr, string[] Stdout) NextCommit { get; set; }
        = (0, Array.Empty<string>(), Array.Empty<string>());
    public (int Exit, string[] Stderr, string[] Stdout) NextTag { get; set; }
        = (0, Array.Empty<string>(), Array.Empty<string>());

    public (int Exit, string[] Stderr, string[] Stdout) Add(string path)
    {
        AddCalls.Add(path);
        return (0, Array.Empty<string>(), Array.Empty<string>());
    }

    public (int Exit, string[] Stderr, string[] Stdout) Commit(string message)
    {
        CommitCalls.Add(message);
        return NextCommit;
    }

    public (int Exit, string[] Stderr, string[] Stdout) Tag(string name)
    {
        TagCalls.Add(name);
        return NextTag;
    }
}
