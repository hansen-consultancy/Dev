# Ubiquitous Language

Domain terminology for **HC.Dev** (`dev`), a .NET global CLI tool that orchestrates common development tasks against a working directory.

## Invocation

| Term | Definition | Aliases to avoid |
| ---- | ---------- | ---------------- |
| **Run** | A single end-to-end execution of `dev` from invocation to exit code. | Invocation, call |
| **Envelope** | The structured result of a **Run**, emitted as JSON when `--json` is set and containing every **Step**. | Output, result, payload |
| **Step** | The execution record of one **Command** within a **Run**, including status, exit code, duration, and data. | Stage, phase |
| **Command Chain** | A sequence of **Commands** combined with `+` and executed in order, stopping on the first failed **Step**. | Pipeline, sequence |
| **Working Directory** | The current directory `dev` runs against; root for **Solution**/**Project** detection and for configuration lookup. | CWD, path, folder |

## Commands

| Term | Definition | Aliases to avoid |
| ---- | ---------- | ---------------- |
| **Command** | A named unit of work the tool can execute (e.g. `build`, `bump`, `launch`). | Action, task, operation |
| **Built-in Command** | A **Command** whose canonical facts (name, **Aliases**, description, **Needs Workspace**, default-ness, **Arg Syntax**) are declared as a `BuiltinCommand` record in the **Command Catalog**: `launch`, `bump`, `bump-commit`, `build`, `frontend`, `clean`, `help`. Its dispatch arm lives in `Program.cs`, kept in sync with the catalog by an exhaustiveness (drift) guard. | Default command, native command |
| **Custom Command** | A **Command** defined in a **Commands Config** that invokes a shell command line instead of a **Built-in**. | Script command, shell command |
| **Alias** | A short synonym for a **Built-in Command** (`b`, `c`, `h`/`?`, `f`, `v`, `vc`). | Shortcut, abbreviation |
| **Default Command** | The **Command** executed when `dev` is called with no arguments; either the config entry marked `"default": true` or `launch` when no **Commands Config** exists. | Fallback command |
| **Placeholder** | A token (`{sln}`, `{project}`, `{dir}`) inside a **Custom Command** line that is substituted with a detected path at **Run** time. | Variable, macro |
| **Command Catalog** | The single source of truth (`CommandCatalog.cs`) for the **Built-in Command** vocabulary: each builtin's canonical facts (name, **Aliases**, description, **Needs Workspace**, default-ness, **Arg Syntax**), the derived **Alias** map, **Command** resolution, the **Unknown Command** **Suggestion** candidate set, and the render-ready **Help Model** — including the **Built-in**↔**Commands Config** merge policy. Pure (no I/O) and exhaustively testable, so no consumer re-encodes any of it. | Command registry, command table |
| **Needs Workspace** | Whether a **Built-in Command** requires a detected **Workspace** before it can run: `launch`, `bump`, `bump-commit`, and `build` do; `frontend`, `clean`, and `help` do not. A **Command** that needs a **Workspace** but finds none produces a failed **Step** with `Code: no_project_found` (exit 2). | Requires project, needs solution |
| **Arg Syntax** | The argument-usage hint shown beside a **Command** in help (e.g. `[major\|minor\|patch\|revision]` for `bump`); display-only — the actual **Bump Part** is parsed from the **Run**'s arguments, not from this string. | Usage, args |
| **Help Model** | The complete, ordered, render-ready list of **Help Rows** for the active **Commands Config** (or the **Built-in** set when none exists), produced by `CommandCatalog.HelpModel`. The single representation both the `--json` help payload and the human help table render, so the two cannot drift; the `help` row is always last. | Help data, help list |
| **Help Row** | One entry in a **Help Model**: a **Command**'s name, **Aliases**, description, default-ness, **Help Source**, backing **Built-in** name (if any), and **Arg Syntax**. | Help entry, help line |
| **Help Source** | Where a **Help Row** originates: `builtin` (a **Built-in Command**, including a **Commands Config** entry that backs a builtin via its `BuiltIn` field) or `config` (a **Custom Command** with its own shell line). Surfaced as each command's `source` field in the help **Envelope**. | Origin, kind |
| **Unknown Command** | A typed token that matches no **Command** valid in the current context; produces a failed **Step** with `Code: unknown_command` and, when a near match exists, a **Suggestion**. | Bad command, invalid command |
| **Suggestion** | The display-only nearest **Command** offered for an **Unknown Command** ("Did you mean `b` (build)?"), computed by case-insensitive edit distance over a context-dependent candidate set. Printed as a console hint and surfaced in the **Step** error's `suggestion` field; **never executed automatically and never prompts**. | Did-you-mean, autocorrect |

## Projects and versioning

| Term | Definition | Aliases to avoid |
| ---- | ---------- | ---------------- |
| **Workspace** | The build/version context detected for a **Run**: the **Solution** if present, otherwise a bare **Project**; `null` when neither is found. Owns `.sln`/`.slnx` precedence, the `src/` fallback, submodule detection, **Dev Config** parent-directory inheritance, and ignore-list filtering; yields the **Build Target** (what the **Builder** compiles or the **Launcher** opens) and the **Bump Targets**. | Project context, target |
| **Solution** | A `.sln` or `.slnx` file detected in the **Working Directory** (or its `src/` subfolder); enumerates **Projects** for bulk operations. | Sln, workspace |
| **Project** | A `.csproj` file, either standalone or enumerated from a **Solution**; the unit whose `<Version>` is read and rewritten. | Module, assembly |
| **Project Candidate** | A **Project** discovered in a **Solution** together with an inclusion decision and, if excluded, a **Skip Reason** (`submodule`, `ignored`). | Project entry |
| **Skip Reason** | Why a **Project Candidate** was excluded from a **Bump**: `submodule` (inside a nested git repo) or `ignored` (listed in **Dev Config**'s `IgnoreProjects`). | Exclusion cause |
| **SemVer** | The parsed version value of a **Project**, consisting of **Major**, **Minor**, **Build**, **Fix**, **Suffix**, and **Build Variables**. | Version string |
| **Bump Part** | Which **SemVer** component to increment: `major`, `minor`, `patch`, or `revision`. Defaults to `minor`. | Bump level, segment |
| **Bump** | The act of incrementing a **Project**'s **SemVer** in place by rewriting its `<Version>` element. | Version change, increment |
| **Bump Commit** | A **Bump** followed by `git add`, `git commit -m "build: {version}"`, and `git tag {version}` when exactly one new version was produced. | Tag release, release bump |

### SemVer anatomy

| Term | Definition | Aliases to avoid |
| ---- | ---------- | ---------------- |
| **Major** | First numeric component of a **SemVer** (e.g. `1` in `1.2.3.4-rc+sha`). | — |
| **Minor** | Second numeric component. | — |
| **Build** | Third numeric component. Called **Patch** externally (in CLI docs) but **Build** in code. | — |
| **Fix** | Fourth numeric component. Called **Revision** externally (in CLI docs) but **Fix** in code. | — |
| **Suffix** | Pre-release label following `-` (e.g. `rc.1`). | Pre-release, tag |
| **Build Variables** | Metadata following `+` (e.g. git sha). | Build metadata |

## Configuration

| Term | Definition | Aliases to avoid |
| ---- | ---------- | ---------------- |
| **Commands Config** | A `commands.json` file in the **Working Directory** that declares the available **Commands** for this project. | Commands file |
| **Dev Config** | A `dev.json` file in the **Working Directory** or its immediate parent that holds tool-wide settings such as `IgnoreProjects`. | Tool config |
| **Trust Store** | A per-user file at `%APPDATA%/hc-dev/trust.json` mapping the absolute path of each approved **Commands Config** to its SHA-256 hash. | Approval file |
| **Trusted Config** | A **Commands Config** whose current SHA-256 hash matches the entry recorded in the **Trust Store** for its full path. | Approved config |
| **Trust Prompt** | The interactive confirmation shown when a **Commands Config** is new or has changed since its last approval; defaults to "no". | Approval prompt |
| **Auto-Yes** | The `--yes` / `-y` global flag that silently accepts a **Trust Prompt** or a missing-`build-frontend.sh` prompt for non-interactive use. | Auto-accept, non-interactive |
| **JSON Mode** | The `--json` global flag that suppresses TTY output and emits the **Envelope** as JSON on stdout. | Machine mode, agent mode |

## External integrations

| Term | Definition | Aliases to avoid |
| ---- | ---------- | ---------------- |
| **Builder** | The executable used to build a **Solution** or **Project**: either a local `build.cmd`/`build.sh` script when present, or `dotnet build -c Release`. | Compiler |
| **Build Script** | A `build.cmd` (Windows) or `build.sh` (non-Windows) file colocated with the **Solution**/**Project** that, when present, overrides the default `dotnet` **Builder**. | — |
| **Frontend Builder** | The Docker image `ghcr.io/stevehansen/vidyano-frontend-builder`, run by the `frontend` **Command** against the **Working Directory** mounted at `/src`. Pinned by digest (the `Program.FrontendImage` constant), not floated on `:latest`, so the exact image is verified on every run. | Vidyano builder |
| **Frontend Build Script** | The `build-frontend.sh` file invoked inside the **Frontend Builder** image; `dev frontend` creates it if missing. | — |
| **Scaffold** | The preparation phase of `dev frontend` that ensures the **Frontend Build Script** exists, normalizes it to LF line endings, and patches `.gitattributes` before the **Frontend Builder** runs. | Setup, bootstrap |
| **Mutation** | A single file change a **Scaffold** records (`created`, `crlf_to_lf`, `appended`); surfaced in the **Envelope** as the `mutations` list. | Edit, file change |
| **Launcher** | The mechanism used by the `launch` **Command**: `ShellExecute` for a **Solution**, or `code` (VS Code) for a bare **Project**. | Opener |

## Relationships

- A **Run** produces exactly one **Envelope** and one or more **Steps**.
- A **Command Chain** expands into N ordered **Steps**; a failed **Step** marks all subsequent **Steps** as `skipped`.
- A **Solution** contains zero or more **Project Candidates**; each **Project Candidate** is either included (yielding a **Bump** attempt) or skipped with a **Skip Reason**.
- A **Commands Config** must be a **Trusted Config** (or bypassed via **Auto-Yes**) before any **Custom Command** runs.
- An **Unknown Command** draws its **Suggestion** from a context-dependent candidate set: the **Commands Config**'s **Command** names when a config is present (a **Built-in** it does not declare would also be unknown), otherwise the **Built-in Command** names plus **Aliases**.
- A **Custom Command** resolves **Placeholders** using the **Solution**, **Project**, and **Working Directory** detected for the **Run**.
- A **Bump Commit** is a **Bump** plus a git commit and tag; it aborts committing when zero or more-than-one distinct new **SemVer** values were produced across **Projects**.
- A **Scaffold** produces zero or more **Mutations** and runs before the **Frontend Builder**; if a required file is missing and neither **Auto-Yes** nor an interactive **Trust Prompt**-style confirmation approves creation, the `frontend` **Step** fails as `interaction_required`.
- A **Run** discovers at most one **Workspace**, wrapping the detected **Solution** and/or **Project** and exposing the **Build Target** and the **Bump Targets**; a **Built-in Command** that **Needs Workspace** fails its **Step** with `no_project_found` (exit 2) when none is found.
- A **Help Model** holds one **Help Row** per visible **Command**, ordered with the `help` row last; both the human help table and the `--json` help payload render from it and nothing else, and each **Help Row** carries its **Help Source** (`builtin` or `config`).

## Example dialogue

> **Dev:** "If I run `dev vc patch` in a repo with three **Projects**, what happens?"
>
> **Domain expert:** "`vc` is the **Alias** for the `bump-commit` **Built-in Command**. It enumerates **Project Candidates** from the detected **Solution**, skipping any inside a git submodule or listed in **Dev Config**'s `IgnoreProjects`. Each included **Project** has its **Build** component — sorry, its **Patch** in the CLI vocabulary — incremented."
>
> **Dev:** "And then a single commit?"
>
> **Domain expert:** "Only if all **Bumps** produced the same new **SemVer**. If two **Projects** started at different versions and diverged, the **Bump Commit** refuses to commit and records `reason: multiple_versions` in the **Step**."
>
> **Dev:** "What if there's a `commands.json` that re-defines `bump-commit` as a shell script?"
>
> **Domain expert:** "Then it's a **Custom Command**, not the **Built-in**. The **Commands Config** must first be a **Trusted Config** — either its SHA-256 matches the **Trust Store** entry or the user answered the **Trust Prompt**. **Auto-Yes** approves it silently, which is why that flag is a sharper edge than `--json`."
>
> **Dev:** "Got it. And `{project}` inside that shell line?"
>
> **Domain expert:** "That's a **Placeholder**. It's replaced with the path to the detected `.csproj` before the line is handed to `cmd.exe /c` or `bash -c`. The substitution isn't shell-escaped, so instead the value is *guarded*: if a used **Placeholder** resolves to a path containing a shell metacharacter, the command is refused rather than run (threat E2, mitigated in the STRIDE model)."
>
> **Dev:** "Last thing — what does `dev frontend --json` report when it has to create `build-frontend.sh`?"
>
> **Domain expert:** "The **Scaffold** runs first and records a **Mutation** (`created`) for that file, plus a `crlf_to_lf` or `appended` **Mutation** if it touches `.gitattributes`. You'll see them in the **Envelope**'s `mutations` list. But under **JSON Mode** without **Auto-Yes**, a *missing* **Frontend Build Script** can't be created non-interactively, so the **Step** fails with `interaction_required` instead."
>
> **Dev:** "Why does `dev clean` work in an empty folder but `dev build` says `no_project_found`?"
>
> **Domain expert:** "Because `build` **Needs Workspace** and `clean` doesn't. Each **Built-in Command** declares that as a fact in the **Command Catalog**: `launch`, `bump`, `bump-commit`, and `build` require a detected **Workspace**; `frontend`, `clean`, and `help` run without one. No **Workspace** plus a command that needs one is a failed **Step** with `no_project_found`, exit 2."
>
> **Dev:** "And `dev help --json` — where does that command list come from?"
>
> **Domain expert:** "The **Command Catalog** builds one **Help Model**: an ordered list of **Help Rows**, the `help` row last. Both the human table and the `--json` payload render that same list, so they can't drift. Each **Help Row** carries a **Help Source** — `builtin`, or `config` for a **Custom Command** — and a renamed config entry that points at a **Built-in** still reports `source: builtin` with its backing name."

## Flagged ambiguities

- **"Patch" vs. "Build" and "Revision" vs. "Fix".** The CLI's `Bump Part` vocabulary (`patch`, `revision`) does not match the `SemVer` class field names (`Build`, `Fix`). Recommendation: keep the CLI surface as the canonical external vocabulary (**Patch**, **Revision**) and treat `Build`/`Fix` as an internal implementation detail of the `SemVer` type, or rename the fields.
- **"Build".** Used for (a) the third **SemVer** component, (b) the `build` **Command**, (c) the act of compiling via the **Builder**, and (d) git commit message prefix `build: {version}`. Recommendation: always qualify — **Build component**, `build` **Command**, compile via **Builder**.
- **"Version".** Refers to the **Tool Version** (`ThisAssembly.Info.InformationalVersion`), the **Project SemVer**, and the NuGet **Package Version**. Recommendation: prefix the subject (**Tool Version**, **Project SemVer**, **Package Version**) whenever context is not obvious.
- **"Command".** Overloaded between the CLI verb (`Built-in`/`Custom Command`), the JSON entry (now `CommandsConfigEntry` in code), the shell command *line* that a **Custom Command** executes, and the `Step.Command` value. The JSON-entry collision was resolved by renaming the class; still reserve **Command** for the CLI verb and call the shell string a **Command Line**.
- **"Target".** Three meanings collide. (1) `BuildTarget` (in `Workspace.cs`) is the "**Solution** file if present, else **Project** file" the **Builder** compiles or the **Launcher** opens. (2) `BumpTarget` (also `Workspace.cs`) is a **Project** enumerated for a **Bump**, carrying its `Include` flag and **Skip Reason** — this is the "**Project Candidate**" domain term above. (3) The JSON field in `Step.Data` is `"target"` (the **Build Target** path) and stays so for wire stability. Recommendation: never say bare "target" in code review — say **Build Target** (what to compile/open) or **Bump Target** (a **Project** to version); both are deliberately distinct from MSBuild's *target*.
- **"Config".** `commands.json` (**Commands Config**) and `dev.json` (**Dev Config**) are distinct files with different schemas and lookup rules (parent-directory fallback applies only to **Dev Config**). Always qualify which one.
- **"Default".** A **Commands Config Entry** may be marked `default: true` (**Default Command**), which is different from the implicit fallback to `launch` when no **Commands Config** exists. Recommendation: use **Default Command** only for the explicit config flag; call the no-config behavior the **Implicit Launch Fallback**.
- **"Built-in" (three referents).** (1) a **Built-in Command** — the CLI verb. (2) the `BuiltinCommand` record in the **Command Catalog** — the *canonical facts* of a builtin. (3) the `BuiltIn` field on a **Commands Config Entry** / **Help Row** — the *name of* the **Built-in Command** that a renamed config entry re-exposes. Recommendation: say **Built-in Command** for the verb, **BuiltinCommand record** for the type, and **backing built-in** for the field's referent.
- **"Workspace".** As a loose synonym for **Solution** it is still an alias to avoid (see **Solution**). But **Workspace** (the value object in `Workspace.cs`) is now a distinct domain term: the detected **Solution**-or-**Project** *context* for a **Run**, which a **Built-in Command** may require via **Needs Workspace**. Recommendation: say **Workspace** only for the context, never for the `.sln` file alone.
