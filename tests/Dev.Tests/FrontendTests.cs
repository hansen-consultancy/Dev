using Xunit;

namespace Dev.Tests;

public sealed class FrontendEnvironmentTests
{
    private const string Repo = "/repo";

    private static ScaffoldOptions Opts(
        FakeFileSystem fs,
        ScriptedPrompter? prompter = null,
        bool autoYes = false,
        bool jsonMode = false,
        bool dryRun = false)
        => new(autoYes, jsonMode, dryRun,
               prompter ?? new ScriptedPrompter(),
               fs,
               _ => { });

    [Fact]
    public void Missing_script_in_json_without_yes_blocks_with_interaction_required()
    {
        var fs = new FakeFileSystem().WithDir(Repo);
        var env = new FrontendEnvironment();

        var outcome = env.Prepare(Repo, Opts(fs, jsonMode: true));

        Assert.Equal(ScaffoldState.BlockedByPrompt, outcome.State);
        Assert.Equal("interaction_required", outcome.BlockingCode);
        Assert.Contains("build-frontend.sh", outcome.BlockingMessage);
        Assert.Empty(outcome.Mutations);
    }

    [Fact]
    public void Missing_script_with_interactive_decline_yields_user_aborted()
    {
        var fs = new FakeFileSystem().WithDir(Repo);
        var env = new FrontendEnvironment();

        var outcome = env.Prepare(Repo, Opts(fs, prompter: new ScriptedPrompter(false)));

        Assert.Equal(ScaffoldState.UserDeclined, outcome.State);
        Assert.Equal("user_aborted", outcome.BlockingCode);
    }

    [Fact]
    public void Missing_script_with_yes_creates_with_default_template()
    {
        var fs = new FakeFileSystem().WithDir(Repo);
        var env = new FrontendEnvironment();

        var outcome = env.Prepare(Repo, Opts(fs, autoYes: true));

        Assert.Equal(ScaffoldState.Ready, outcome.State); // .gitattributes also created via AutoYes
        var scriptMutation = outcome.Mutations.First(m => m.File.Contains("build-frontend.sh"));
        Assert.Equal("created", scriptMutation.Action);

        var written = fs.GetWritten(System.IO.Path.Combine(Repo, "build-frontend.sh"));
        Assert.NotNull(written);
        Assert.Contains("#!/usr/bin/env bash", written);
        Assert.Contains("cd repo/", written); // folder name from /repo
        Assert.DoesNotContain("\r\n", written); // CRLF normalized
    }

    [Fact]
    public void Existing_script_with_crlf_is_normalized_to_lf()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/build-frontend.sh", "#!/bin/bash\r\necho hi\r\n")
            .WithFile("/repo/.gitattributes", "* text=auto\n*.sh text eol=lf");
        var env = new FrontendEnvironment();

        var outcome = env.Prepare(Repo, Opts(fs, autoYes: true));

        Assert.Equal(ScaffoldState.Ready, outcome.State);
        var mutation = Assert.Single(outcome.Mutations);
        Assert.Equal("crlf_to_lf", mutation.Action);
        var written = fs.GetWritten("/repo/build-frontend.sh");
        Assert.DoesNotContain("\r\n", written);
        Assert.Equal("#!/bin/bash\necho hi\n", written);
    }

    [Fact]
    public void Existing_script_with_lf_only_yields_no_mutations()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/build-frontend.sh", "#!/bin/bash\necho hi\n")
            .WithFile("/repo/.gitattributes", "* text=auto\n*.sh text eol=lf");
        var env = new FrontendEnvironment();

        var outcome = env.Prepare(Repo, Opts(fs));

        Assert.Equal(ScaffoldState.Ready, outcome.State);
        Assert.Empty(outcome.Mutations);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public void Missing_gitattributes_with_interactive_decline_emits_warning_not_block()
    {
        var fs = new FakeFileSystem().WithFile("/repo/build-frontend.sh", "#!/bin/bash\n");
        var env = new FrontendEnvironment();
        // Script exists, so only one prompt fires (for .gitattributes). Decline it.
        var outcome = env.Prepare(Repo, Opts(fs, prompter: new ScriptedPrompter(false)));

        Assert.Equal(ScaffoldState.Warning, outcome.State);
        Assert.Contains("gitattributes_missing", outcome.Warnings);
        Assert.Empty(outcome.Mutations);
        Assert.Null(outcome.BlockingCode);
    }

    [Fact]
    public void Missing_gitattributes_in_json_without_yes_emits_warning()
    {
        var fs = new FakeFileSystem().WithFile("/repo/build-frontend.sh", "#!/bin/bash\n");
        var env = new FrontendEnvironment();
        var outcome = env.Prepare(Repo, Opts(fs, jsonMode: true));

        Assert.Equal(ScaffoldState.Warning, outcome.State);
        Assert.Contains("gitattributes_missing", outcome.Warnings);
    }

    [Fact]
    public void Existing_gitattributes_lacking_sh_rule_appends_it()
    {
        var fs = new FakeFileSystem()
            .WithFile("/repo/build-frontend.sh", "#!/bin/bash\n")
            .WithFile("/repo/.gitattributes", "* text=auto");
        var env = new FrontendEnvironment();
        var outcome = env.Prepare(Repo, Opts(fs));

        Assert.Equal(ScaffoldState.Ready, outcome.State);
        var mutation = Assert.Single(outcome.Mutations);
        Assert.Equal("appended", mutation.Action);
        Assert.Equal("*.sh text eol=lf", mutation.Detail);

        var written = fs.GetWritten("/repo/.gitattributes");
        Assert.Contains("* text=auto", written);
        Assert.Contains("*.sh text eol=lf", written);
    }

    [Fact]
    public void DryRun_produces_full_mutations_list_with_zero_writes()
    {
        var fs = new FakeFileSystem().WithDir(Repo);
        var env = new FrontendEnvironment();

        var outcome = env.Prepare(Repo, Opts(fs, autoYes: true, dryRun: true));

        Assert.Equal(ScaffoldState.Ready, outcome.State);
        Assert.Equal(2, outcome.Mutations.Count); // both files would be created
        Assert.All(outcome.Mutations, m => Assert.Equal("created", m.Action));

        // No actual writes happened.
        Assert.Null(fs.GetWritten("/repo/build-frontend.sh"));
        Assert.Null(fs.GetWritten("/repo/.gitattributes"));
    }
}

public sealed class FrontendBuildTests
{
    [Fact]
    public void Run_composes_docker_argv_with_workspace_volume_mount_and_image()
    {
        var runner = new FakeProcessRunner();
        var build = new FrontendBuild();
        var ctx = new RunContext();

        var outcome = build.Run("/work/dir", new BuildOptions(
            "ghcr.io/example/builder:latest", runner, ctx));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("ghcr.io/example/builder:latest", outcome.Image);
        var spec = Assert.Single(runner.Invocations);
        Assert.Contains("docker run --rm", spec.DisplayCommandLine);
        Assert.Contains("-v \"/work/dir:/src\"", spec.DisplayCommandLine);
        Assert.Contains("-w /src", spec.DisplayCommandLine);
        Assert.Contains("ghcr.io/example/builder:latest", spec.DisplayCommandLine);
    }

    [Fact]
    public void Run_propagates_non_zero_exit_with_tails()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcRunResult(
            42,
            new[] { "stderr line" },
            new[] { "stdout line" },
            "shell",
            "docker run ..."));
        var build = new FrontendBuild();

        var outcome = build.Run("/work", new BuildOptions(
            "image:tag", runner, new RunContext()));

        Assert.Equal(42, outcome.ExitCode);
        Assert.Equal(new[] { "stderr line" }, outcome.StderrTail);
        Assert.Equal(new[] { "stdout line" }, outcome.StdoutTail);
    }
}
