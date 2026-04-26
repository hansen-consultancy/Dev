using System.Runtime.InteropServices;
using Xunit;

namespace Dev.Tests;

public sealed class ProcSpecTests
{
    [Fact]
    public void Exec_records_file_and_args_verbatim()
    {
        var spec = ProcSpec.Exec("git", "commit -m \"build: 1.2.3\"");
        Assert.Equal("git", spec.FileName);
        Assert.Equal("commit -m \"build: 1.2.3\"", spec.Arguments);
        Assert.Equal("exec", spec.DisplayShell);
        Assert.Equal("git commit -m \"build: 1.2.3\"", spec.DisplayCommandLine);
    }

    [Fact]
    public void Exec_with_empty_args_displays_file_only()
    {
        var spec = ProcSpec.Exec("build.cmd", "");
        Assert.Equal("build.cmd", spec.DisplayCommandLine);
    }

    [Fact]
    public void Shell_wraps_command_line_for_current_os()
    {
        var spec = ProcSpec.Shell("docker run --rm hello-world");

        Assert.Empty(spec.Arguments);
        Assert.NotNull(spec.ArgList);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("cmd.exe", spec.FileName);
            Assert.Equal(new[] { "/S", "/C", "docker run --rm hello-world" }, spec.ArgList);
            Assert.Equal("cmd", spec.DisplayShell);
        }
        else
        {
            Assert.Equal("bash", spec.FileName);
            Assert.Equal(new[] { "-c", "docker run --rm hello-world" }, spec.ArgList);
            Assert.Equal("bash", spec.DisplayShell);
        }

        Assert.Equal("docker run --rm hello-world", spec.DisplayCommandLine);
    }

    [Fact]
    public void Shell_passes_command_line_verbatim_through_argument_list()
    {
        // Hardens against quote-escape injection: a command line containing
        // double quotes or shell metacharacters must be delivered to the
        // interpreter as a single argv element, not embedded in a quoted wrapper.
        var malicious = "echo \"hi\"; rm -rf $(pwd)";

        var spec = ProcSpec.Shell(malicious);

        Assert.NotNull(spec.ArgList);
        Assert.Equal(malicious, spec.ArgList![spec.ArgList.Count - 1]);
        Assert.Empty(spec.Arguments);
    }

    [Fact]
    public void ExecArgs_uses_argument_list_and_renders_display_command_line()
    {
        var spec = ProcSpec.ExecArgs("docker", new[] { "run", "--rm", "-v", "/tmp dir:/src", "hello" });

        Assert.Equal("docker", spec.FileName);
        Assert.Empty(spec.Arguments);
        Assert.Equal(new[] { "run", "--rm", "-v", "/tmp dir:/src", "hello" }, spec.ArgList);
        Assert.Equal("docker run --rm -v /tmp dir:/src hello", spec.DisplayCommandLine);
    }
}

public sealed class RunOrFailTests
{
    [Fact]
    public void Returns_true_and_leaves_step_intact_on_success()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcRunResult(0, Array.Empty<string>(), Array.Empty<string>(), "exec", "git --version"));
        var step = new StepResult { Command = "bump-commit" };
        var ctx = new RunContext();

        var ok = runner.RunOrFail(ProcSpec.Exec("git", "--version"), step, ctx, "git_failed", "git failed", out var result);

        Assert.True(ok);
        Assert.True(result.Ok);
        Assert.Equal("ok", step.Status);
        Assert.Equal(0, step.ExitCode);
        Assert.Null(step.Error);
    }

    [Fact]
    public void Returns_false_and_populates_step_error_on_non_zero_exit()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcRunResult(128, new[] { "fatal: not a repo" }, Array.Empty<string>(), "exec", "git status"));
        var step = new StepResult { Command = "bump-commit" };
        var ctx = new RunContext();

        var ok = runner.RunOrFail(ProcSpec.Exec("git", "status"), step, ctx, "git_failed", "git status failed", out var result);

        Assert.False(ok);
        Assert.False(result.Ok);
        Assert.Equal(128, result.ExitCode);
        Assert.Equal("failed", step.Status);
        Assert.Equal(1, step.ExitCode);
        Assert.NotNull(step.Error);
        Assert.Equal("git_failed", step.Error!.Code);
        Assert.Equal("git status failed", step.Error.Message);

        var detail = Assert.IsType<Dictionary<string, object?>>(step.Error.Detail);
        Assert.Equal(128, detail["exitCode"]);
        Assert.Equal(new[] { "fatal: not a repo" }, detail["stderrTail"]);
    }

    [Fact]
    public void Passes_spec_through_to_runner_unmodified()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcRunResult(0, Array.Empty<string>(), Array.Empty<string>(), "exec", "git tag 1.0.0"));
        var step = new StepResult();
        var ctx = new RunContext();
        var spec = ProcSpec.Exec("git", "tag 1.0.0");

        runner.RunOrFail(spec, step, ctx, "git_failed", null, out _);

        var invoked = Assert.Single(runner.Invocations);
        Assert.Equal(spec, invoked);
    }

    [Fact]
    public void Short_circuit_skips_second_call_and_preserves_first_call_error()
    {
        // Production callers chain RunOrFail with `&&` so a failed first call
        // does not run the second AND step.Error keeps the first call's code +
        // message instead of being clobbered by the second call.
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcRunResult(1, new[] { "commit err" }, new[] { "commit out" }, "exec", "git commit"));
        runner.Enqueue(new ProcRunResult(1, new[] { "tag err" }, new[] { "tag out" }, "exec", "git tag"));
        var step = new StepResult { Command = "bump-commit" };
        var ctx = new RunContext();

        var bothOk = runner.RunOrFail(ProcSpec.Exec("git", "commit"), step, ctx, "git_commit_failed", "git commit failed", out var commit)
                  && runner.RunOrFail(ProcSpec.Exec("git", "tag"), step, ctx, "git_tag_failed", "git tag failed", out _);

        Assert.False(bothOk);
        Assert.Single(runner.Invocations); // tag was skipped
        Assert.Equal(1, commit.ExitCode);
        Assert.Equal("git_commit_failed", step.Error!.Code);
        Assert.Equal("git commit failed", step.Error.Message);
    }
}

