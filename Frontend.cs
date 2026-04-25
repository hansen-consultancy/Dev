// Frontend command, decomposed:
//   FrontendEnvironment owns scaffolding (build-frontend.sh template,
//     CRLF→LF normalization, .gitattributes patching) and the
//     (AutoYes × JsonMode × required) → (Create | Decline | Blocked) matrix.
//   FrontendBuild owns the docker invocation; OS-shell wrapping is the
//     process-runner port's concern.
//   The CLI composition layer translates outcomes into StepResult fields.

using Spectre.Console;

namespace Dev;

internal interface IFrontendEnvironment
{
    ScaffoldOutcome Prepare(string workspacePath, ScaffoldOptions options);
}

internal interface IFrontendBuild
{
    BuildOutcome Run(string workspacePath, BuildOptions options);
}

internal interface IConfirmationPrompt
{
    bool Confirm(string question);
}

internal sealed class AnsiConsolePrompter : IConfirmationPrompt
{
    public bool Confirm(string question) => AnsiConsole.Prompt(new ConfirmationPrompt(question));
}

internal enum ScaffoldState { Ready, BlockedByPrompt, UserDeclined, Warning }

internal sealed record Mutation(string File, string Action, string? Detail = null);

internal sealed record ScaffoldOutcome(
    ScaffoldState State,
    IReadOnlyList<Mutation> Mutations,
    IReadOnlyList<string> Warnings,
    string? BlockingCode,
    string? BlockingMessage);

internal sealed record ScaffoldOptions(
    bool AutoYes,
    bool JsonMode,
    bool DryRun,
    IConfirmationPrompt Prompter,
    IFileSystem Fs,
    Action<string> Log);

internal sealed record BuildOutcome(
    string Image,
    int ExitCode,
    IReadOnlyList<string> StdoutTail,
    IReadOnlyList<string> StderrTail);

internal sealed record BuildOptions(string Image, IProcessRunner Runner, RunContext Ctx);

internal sealed class FrontendEnvironment : IFrontendEnvironment
{
    public ScaffoldOutcome Prepare(string workspacePath, ScaffoldOptions options)
    {
        var mutations = new List<Mutation>();
        var warnings = new List<string>();

        var scriptOutcome = EnsureBuildScript(workspacePath, options, mutations);
        if (scriptOutcome is not null)
            return scriptOutcome;

        var attrsState = EnsureGitAttributes(workspacePath, options, mutations, warnings);

        return new ScaffoldOutcome(attrsState, mutations, warnings, null, null);
    }

    private static ScaffoldOutcome? EnsureBuildScript(
        string workspacePath, ScaffoldOptions opts, List<Mutation> mutations)
    {
        var buildFile = Path.Combine(workspacePath, "build-frontend.sh");

        if (opts.Fs.FileExists(buildFile))
        {
            var content = opts.Fs.ReadAllText(buildFile);
            if (content.Contains("\r\n"))
            {
                opts.Log($"[yellow]Converting[/] {Path.GetFileName(buildFile)} [yellow]to LF line endings...[/]");
                if (!opts.DryRun) opts.Fs.WriteAllText(buildFile, content.Replace("\r\n", "\n"));
                mutations.Add(new Mutation(buildFile, "crlf_to_lf"));
            }
            return null;
        }

        opts.Log($"[red]No {Path.GetFileName(buildFile)} file found in the current directory.[/]");
        switch (ResolveCreate(opts, required: true))
        {
            case CreateDecision.Blocked:
                return new ScaffoldOutcome(
                    ScaffoldState.BlockedByPrompt,
                    mutations, Array.Empty<string>(),
                    "interaction_required",
                    "build-frontend.sh is missing; re-run with --yes to create it.");

            case CreateDecision.Decline:
                opts.Log("[red]Aborting.[/]");
                return new ScaffoldOutcome(
                    ScaffoldState.UserDeclined,
                    mutations, Array.Empty<string>(),
                    "user_aborted",
                    "User declined to create build-frontend.sh.");

            case CreateDecision.Create:
            default:
                opts.Log($"[green]Creating[/] {Path.GetFileName(buildFile)} [green]file in the current directory...[/]");
                var folderName = Path.GetFileName(workspacePath);
                if (!opts.DryRun) opts.Fs.WriteAllText(buildFile, BuildScriptTemplate(folderName));
                mutations.Add(new Mutation(buildFile, "created"));
                return null;
        }
    }

    private static ScaffoldState EnsureGitAttributes(
        string workspacePath, ScaffoldOptions opts, List<Mutation> mutations, List<string> warnings)
    {
        var attributesFile = Path.Combine(workspacePath, ".gitattributes");

        if (opts.Fs.FileExists(attributesFile))
        {
            var content = opts.Fs.ReadAllText(attributesFile);
            if (!content.Contains("*.sh text eol=lf"))
            {
                opts.Log($"[yellow]Adding LF line endings for bash files to {Path.GetFileName(attributesFile)}...[/]");
                if (!opts.DryRun) opts.Fs.WriteAllText(attributesFile, content + "\n*.sh text eol=lf");
                mutations.Add(new Mutation(attributesFile, "appended", "*.sh text eol=lf"));
            }
            return ScaffoldState.Ready;
        }

        opts.Log($"[red]No {Path.GetFileName(attributesFile)} file found in the current directory.[/]");
        switch (ResolveCreate(opts, required: false))
        {
            case CreateDecision.Create:
                opts.Log($"[green]Creating[/] {Path.GetFileName(attributesFile)} [green]file in the current directory...[/]");
                if (!opts.DryRun) opts.Fs.WriteAllText(attributesFile, GitAttributesTemplate());
                mutations.Add(new Mutation(attributesFile, "created"));
                return ScaffoldState.Ready;

            default: // Decline (interactive decline or json without --yes)
                opts.Log("[yellow]Ignoring.[/]");
                warnings.Add("gitattributes_missing");
                return ScaffoldState.Warning;
        }
    }

    private enum CreateDecision { Create, Decline, Blocked }

    private static CreateDecision ResolveCreate(ScaffoldOptions opts, bool required)
    {
        if (opts.AutoYes) return CreateDecision.Create;
        if (opts.JsonMode) return required ? CreateDecision.Blocked : CreateDecision.Decline;
        return opts.Prompter.Confirm("Do you want to create it?")
            ? CreateDecision.Create
            : CreateDecision.Decline;
    }

    private static string BuildScriptTemplate(string folderName)
    {
        var script = $$"""
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
        return script.Replace("\r\n", "\n");
    }

    private static string GitAttributesTemplate()
        => "# Set default behavior to automatically normalize line endings.\n* text=auto\n# Explicitly declare text files we want to always be normalized and converted to native line endings on checkout.\n*.sh text eol=lf";
}

internal sealed class FrontendBuild : IFrontendBuild
{
    public BuildOutcome Run(string workspacePath, BuildOptions options)
    {
        var dockerCommand = $"docker run --rm -v \"{workspacePath}:/src\" -w /src {options.Image}";
        var result = options.Runner.Run(ProcSpec.Shell(dockerCommand), options.Ctx);
        return new BuildOutcome(options.Image, result.ExitCode, result.StdoutTail, result.StderrTail);
    }
}
