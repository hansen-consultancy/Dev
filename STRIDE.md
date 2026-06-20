# STRIDE Threat Model — HC.Dev (`dev` CLI tool)

> **Version:** 1.13.3
> **Date:** 2026-06-20
> **Scope:** The `dev` .NET global tool, its configuration files, and its interactions with the local system.

> **v1.13.3 note:** Closed the placeholder-injection gap that kept **T1** at *Partially Mitigated* and **E2** at *Medium* (see `SECURITY_REVIEW.md` F2). `RunCustomCommand` now gates substitution through `PlaceholderGuard.FindUnsafe` (`PlaceholderGuard.cs`): each *used* `{sln}`/`{project}`/`{dir}` placeholder's resolved path is scanned for shell metacharacters (`& | ; < > \` $ " ' %` and CR/LF/NUL) before it is spliced into the trusted command body; a hit refuses the command with a `placeholder_unsafe` error (exit 5) and runs nothing. The trusted body keeps its shell features (pipes/`&&`/redirects). Refusal was chosen over escaping because cmd.exe quoting is not reliably composable and escaping collides with author-supplied quotes around a placeholder; `(`/`)` are intentionally allowed so paths like `Program Files (x86)` keep working. Pure and exhaustively tested (`PlaceholderGuardTests.cs`). **Not yet released** — the tool version is still `1.13.0`; the `v1.13.x` notes here track threat-model changes ahead of the next release tag.

> **v1.13.2 note:** The CLI now offers a display-only "did you mean" **Suggestion** when an unknown command is typed (`CommandCatalog.Suggest`). It is *informational only* — printed via `ctx.Log` (suppressed under JSON mode) and surfaced in the envelope error's `suggestion` field, but **never auto-executed and never prompted to run** — so it introduces no new path to run a command the user did not type (captured as **E5** below). Two incidental hardening points: the suggestion text is `EscapeMarkup`-ed before rendering, and `Suggest` skips null/empty candidate names, so a trusted-but-malformed `commands.json` (an entry with `"name": null`) cannot turn a typo into an unhandled `NullReferenceException` instead of the clean `unknown_command` envelope.

> **v1.13.1 note:** Security review (see `SECURITY_REVIEW.md`) found that `ProcessGitPort` built git argument *strings* by interpolation (`git tag {version}`, `git commit -m "{message}"`), unlike the argv-form (`ArgumentList`) used elsewhere. Because `SemVer` carries `Suffix`/`BuildVariables` through a permissive `<Version>` regex, a crafted csproj in a cloned repo could split extra arguments into the git invocation or break out of `-m` quoting (argument/option injection — no shell, so not RCE). Fixed by routing all three git operations through `ProcSpec.ExecArgs`, delivering each value as one opaque token, plus a `--` guard on `git add`. **T3** is re-scoped and downgraded to **Mitigated** below.

> **v1.13.0 note:** Five architectural refactors land in this version. Four are behavior-preserving; the fifth (trust gate) modestly hardens existing mitigations:
> 1. Child-process execution extracted into `ProcessRunner.cs` (`IProcessRunner`, `ProcSpec`, `ProcRunResult`, `RealProcessRunner`). `ProcSpec.Shell` now delivers the user-supplied command line as a separate `ArgumentList` element to `bash -c` and `cmd.exe /S /C`, so the wrapper itself no longer concatenates untrusted strings into a quoted blob (T1 wrapper layer).
> 2. Version-bump split into pure `SemVer` math, an `IProjectVersionFile` file-mutation port (`CsprojVersionFile` owns the `<Version>` regex), an `IGitPort` (`ProcessGitPort` rides on `IProcessRunner`), and a `BumpPipeline` orchestrator that owns the cross-project version-agreement invariant and the commit-then-tag sequencing.
> 3. `sln`/`csproj`/`src/`/`dev.json` discovery collapsed into a `Workspace` value object with internal `IFileSystem` and `ISolutionReader` seams. `Workspace.Discover` owns submodule detection, ignore-list filtering, `.sln` vs `.slnx` precedence, the `src/` fallback, and parent-directory `dev.json` inheritance.
> 4. `frontend` command split into `FrontendEnvironment` (scaffolding `build-frontend.sh` from a private template, CRLF→LF normalization, `.gitattributes` patching, and the `(AutoYes × JsonMode × required)` prompt matrix) and `FrontendBuild` (docker invocation via `IProcessRunner`). An `IConfirmationPrompt` port replaces direct Spectre prompts inside the module. `FrontendBuild.Run` invokes `docker` via `ProcSpec.ExecArgs` (argv-form) instead of building a `bash -c "docker run -v \"$workspacePath:/src\""` shell command, so a workspace path containing `"`, `$()`, backticks, or other shell metacharacters cannot escape into a wrapping shell.
> 5. `commands.json` trust gate decomposed into `TrustGate.Decide` (pure decision function, exhaustively tested as a `(state × flags)` decision table) and three ports (`ITrustStore`, `ITrustPrompt`, `ITrustOutcomeSink`) dispatched by `TrustGateOrchestrator`. `FileSystemTrustStore` writes atomically (temp + rename) under a cross-process file lock and uses a schema-versioned format that is backward-compatible with the legacy `trust.json`.
>
> Affected-component references for T2, T3, T4, I1, R1, D2, etc. now point at the new files where the relevant code lives. The trust-gate refactor (#5 above) materially strengthens the mitigations on **S1**, **E1**, **E4**, and **T1** (see updated entries below).

## Overview

HC.Dev is a .NET 8.0 CLI tool distributed as a NuGet global tool. It operates in the context of the current working directory, detects solution/project files, and executes development tasks such as building, version bumping, launching IDEs, running Docker containers, and executing user-defined custom commands.

### Trust Boundaries

1. **User ↔ Tool**: The developer invoking `dev` from the terminal.
2. **Tool ↔ File System**: Reading/writing project files, config files, build scripts.
3. **Tool ↔ External Processes**: Spawning `dotnet`, `git`, `cmd.exe`/`bash`, `docker`, `code`, and custom build scripts. All process spawning is funneled through `ProcessRunner.cs` (`IProcessRunner` / `RealProcessRunner`) since v1.13.0.
4. **Tool ↔ NuGet**: Distribution and installation of the tool package.
5. **Tool ↔ Docker Registry**: Pulling the `ghcr.io/stevehansen/vidyano-frontend-builder` image.

### Assets

- Source code and project files (`.csproj`, `.sln`)
- Version metadata in project files
- Git repository state (commits, tags)
- Configuration files (`commands.json`, `dev.json`)
- Trust store (`%APPDATA%/hc-dev/trust.json`) — SHA-256 hashes of approved `commands.json` files keyed by full path
- Build scripts (`build.cmd`, `build.sh`, `build-frontend.sh`)
- Docker volume-mounted source directory

---

## S — Spoofing

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| S1 | **Malicious `commands.json` in cloned repo** — An attacker commits a crafted `commands.json` to a repository. When a developer runs `dev`, arbitrary commands execute under their identity. | `Trust.cs:TrustGate.Decide` (pure decision); `Trust.cs:TrustGateOrchestrator.Authorize`; `Program.cs:RunCustomCommand` → `ProcessRunner.cs:RealProcessRunner` | **High** → **Mitigated** | **Mitigated in v1.12.0; hardened in v1.13.0:** A hash-based trust system blocks execution of any `commands.json` that has not been explicitly approved by the user. On first encounter or when the file changes, the tool displays a warning, shows a summary of all commands (including shell commands that would run), and requires explicit confirmation (defaulting to "no"). Approved configs are recorded by full path and SHA-256 hash in `%APPDATA%/hc-dev/trust.json`. **v1.13.0:** the decision logic is now a pure `Decide` function exhaustively unit-tested across all twelve `(state × flags)` cells, eliminating "silent allow" bugs from flag-combination edge cases. Residual risk: a user may approve a malicious config without carefully reading the summary. |
| S2 | **Malicious NuGet package substitution** — An attacker publishes a package with a similar name (typosquatting `HC.Dev`) to execute malicious code when installed. | NuGet distribution | **Medium** → **Mitigated** | **Mitigated in v1.11.0:** NuGet trusted publishing workflow added to CI, ensuring only verified builds from the official repository can publish the package. The package uses a scoped ID (`HC.Dev`) with a specific `ToolCommandName`. Residual risk: typosquatting with a different package ID remains possible but is outside the project's control. |
| S3 | **Docker image spoofing** — If the Docker registry or user's Docker config is compromised, a malicious image could replace `ghcr.io/stevehansen/vidyano-frontend-builder:latest`. | `Frontend.cs:FrontendBuild.Run` — Docker run command; image identity is hardcoded at the `Program.cs:RunFrontend` composition site | **Medium** | Consider pinning the Docker image to a specific digest rather than `:latest`. Ensure the GitHub Container Registry package has appropriate access controls. |

---

## T — Tampering

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| T1 | **Command injection via `commands.json`** — Custom commands from `commands.json` are passed directly to `cmd.exe /c` or `bash -c` without sanitization. A malicious config can execute arbitrary shell commands. | `Program.cs:RunCustomCommand` → `ProcSpec.Shell` (`ProcessRunner.cs`) | **High** → **Mitigated** | **Partially mitigated in v1.12.0:** The trust system ensures the user must explicitly approve the `commands.json` content before any commands execute. The command summary shows the exact shell commands that will run, giving the user visibility. **Hardened in v1.13.0:** `ProcSpec.Shell` no longer wraps the command line in `"…"` before handing it to the interpreter — the command line is delivered as a distinct `ArgumentList` element (`bash -c <cmd>` / `cmd.exe /S /C <cmd>`), so quote characters in the command body cannot break out of the wrapper. **Closed in v1.13.3:** the last gap — variable placeholders (`{sln}`, `{project}`, `{dir}`) injected unsanitized — is now gated by `PlaceholderGuard.FindUnsafe`, which refuses any command whose used placeholder resolves to a path carrying a shell metacharacter (see **E2**). The trust check still covers the config file rather than the runtime-resolved command, but a metacharacter-bearing path can no longer reach the interpreter. Residual risk: once approved, the trusted command body itself runs with full user privileges (by design). |
| T2 | **Build script tampering** — The tool executes `build.cmd`/`build.sh` if found in the project directory, with no integrity check. | `Program.cs:BuildSolutionOrProject` (~line 617) → `ProcessRunner.cs:RealProcessRunner` | **Medium** | An attacker who can write to the project directory can replace build scripts. This is a standard risk for local development tools. |
| T3 | **Argument injection into `git` via `<Version>` content** — The permissive `<Version>(?<version>.*)</Version>` regex lets `SemVer.Suffix`/`BuildVariables` carry arbitrary text. That version string flowed (interpolated, unquoted for `tag`) into `git tag {version}` and `git commit -m "{message}"`, so a crafted csproj in a cloned repo could split additional git arguments or break out of `-m` quoting. (No shell is involved, so this is argument/option injection, not RCE.) | `VersionBump.cs:ProcessGitPort` (git argv), fed by `SemVer.ToString()`; `SemVer.cs:VersionRegex` (parse) | **Medium** → **Mitigated** | **Mitigated in v1.13.1:** all three git operations now go through `ProcSpec.ExecArgs`, delivering the path, message, and tag name as distinct `ArgumentList` tokens that the runtime cannot split or re-quote. `git add` uses a `--` separator so a path beginning with `-` is not read as an option; the tag name always begins with a numeric major component, and `-m` always consumes its next token verbatim, so neither needs a further dash guard. Residual risk: a malicious version still becomes a (rejected or odd-looking) tag/commit-message value, but cannot alter the git command shape. |
| T4 | **Parent directory `dev.json` traversal** — The tool checks the parent directory for `dev.json` if not found locally. A malicious `dev.json` placed in a shared parent directory could influence the tool's behavior (e.g., suppressing version bumps via `IgnoreProjects`). | `Workspace.cs:LoadDevConfig` | **Low** | Only traverses one level up. The impact is limited to ignoring projects during version bumping. |
| T5 | **Trust-store corruption under concurrent writers or crash** — Two `dev` processes racing to commit trust entries could lose writes; a crash mid-write could corrupt `trust.json`, dropping all approvals (forcing re-approval, but worse: a subsequent crash window between deletion and rewrite was a small DoS surface for trust loss). | `Trust.cs:FileSystemTrustStore.Commit` | **Low** → **Mitigated** | **Mitigated in v1.13.0:** writes go through a temp-file-plus-rename pattern (atomic on the same filesystem); a cross-process file lock (`FileShare.None` on a sentinel `.lock` file with a 10-second timeout) serializes commits across concurrent `dev` instances; a `SchemaVersion` field is written on every commit. Reads are tolerant: missing or corrupt JSON, and stranded `.tmp` files from interrupted writes, all yield a clean empty snapshot rather than throwing. Legacy `trust.json` files (v1.12.0 format) load with `SchemaVersion` defaulting to 1, so the upgrade is invisible to existing users. |

---

## R — Repudiation

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| R1 | **Automated git commits without audit trail** — The `bump-commit` command creates git commits and tags automatically. If the tool is run unintentionally or by an unauthorized script, changes are committed without explicit user confirmation. | `VersionBump.cs:BumpPipeline.ResolveGit` (commit/tag policy) → `ProcessGitPort` → `ProcessRunner.cs`; `Program.cs:RunBumpCommit` (CLI envelope) | **Low** | Git commits include author information from the local git config. The commit message follows a fixed format (`build: {version}`). The risk is low since this requires local access. Consider adding a confirmation prompt for commit operations. |
| R2 | **No logging of custom command execution** — When custom commands from `commands.json` run, there is no persistent log of what was executed. | `Program.cs:RunCustomCommand` (~line 255) | **Low** | The command output goes to stdout, but there is no file-based audit log. For a local development tool, this is acceptable. |

---

## I — Information Disclosure

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| I1 | **Source code exposed via Docker volume mount** — The `frontend` command mounts the entire working directory into a Docker container (`-v "{path}:/src"`). If the Docker image is compromised, all source code is accessible. | `Frontend.cs:FrontendBuild.Run` (docker argv composition); `Program.cs:RunFrontend` (composition) | **Medium** | The volume mount exposes the full working directory. Consider mounting only the necessary subdirectory if possible. Users should verify the Docker image integrity. |
| I2 | **Exception details displayed to console** — Exceptions from config file parsing (and a child-process start failure) are written to the console via `AnsiConsole.WriteException`, which may reveal file paths and internal details. | `Program.cs:52` (commands.json parse), `Program.cs:73` (workspace discovery error — surfaces dev.json parse failure from `Workspace.cs`), `Program.cs:375` (clean failure), `ProcessRunner.cs:RealProcessRunner` (start-failure path) | **Low** | This is a developer tool and the output is only visible to the local user. No sensitive data beyond file paths is exposed. |
| I3 | **Version and git info in banner** — The tool displays its version and git commit hash on every run. | `Program.cs:28-29` | **Informational** | This is intentional and useful. No action needed. |

---

## D — Denial of Service

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| D1 | **Recursive directory enumeration** — The `clean` command enumerates all directories recursively (`SearchOption.AllDirectories`). In a directory with an extremely deep or wide structure (e.g., symlink loops), this could hang or consume excessive memory. | `Program.cs:431` | **Low** | The `node_modules` exclusion helps, but symlink loops or very large trees could still be problematic. This is a minor risk for a local CLI tool. |
| D2 | **Blocking `WaitForExit()` on spawned processes** — All `Process.Start(...).WaitForExit()` calls block indefinitely. A hung build, Docker pull, or custom command will block the tool forever. | `ProcessRunner.cs:RealProcessRunner.Run` (centralized in v1.13.0; previously duplicated across `Program.cs`) | **Low** | Consider adding optional timeouts. For a local interactive tool, the user can always Ctrl+C. |
| D3 | **Malicious `commands.json` causing infinite loop** — A crafted config could define a default command that invokes `dev` recursively. | `Program.cs` — default-command resolution (~line 42, 206) | **Low** | No recursion guard exists. In practice, the process stack would eventually exhaust resources. |

---

## E — Elevation of Privilege

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| E1 | **Arbitrary command execution via `commands.json`** — Custom commands run with the full privileges of the invoking user. A malicious `commands.json` in a cloned repository could execute commands the user did not intend (e.g., `curl ... \| bash`, modifying system files, exfiltrating credentials). | `Trust.cs:TrustGate.Decide` + `TrustGateOrchestrator`; `Program.cs:RunCustomCommand` → `ProcessRunner.cs:RealProcessRunner` | **High** → **Mitigated** | **Mitigated in v1.12.0; hardened in v1.13.0:** The tool no longer blindly trusts `commands.json`. The three controls (warning, command summary, explicit default-no confirmation) are unchanged. **v1.13.0:** the decision logic is a pure `Decide` function with exhaustive unit-test coverage of all `(AlreadyTrusted | NewFile | ModifiedSinceTrust) × AutoYes × JsonMode` cells — there is no path where a flag-combination bug silently allows execution. Residual risk: once approved, commands run with full user privileges — no sandboxing is applied. |
| E2 | **Shell metacharacter injection via path placeholders** — The `{sln}`, `{project}`, and `{dir}` placeholders in custom commands are replaced with file/directory paths and passed to `cmd.exe /c` or `bash -c`. If paths contain shell metacharacters (`;`, `&&`, `\|`, backticks, `$(...)`), this enables command injection. | `PlaceholderGuard.FindUnsafe` (`PlaceholderGuard.cs`); `Program.cs:RunCustomCommand`; `Program.cs:ReplaceVariables` | **Medium** → **Mitigated** | **Mitigated in v1.13.3:** before substitution, `PlaceholderGuard.FindUnsafe` scans each *used* placeholder's resolved path for shell metacharacters (`& | ; < > \` $ " ' %` and CR/LF/NUL); a hit refuses the command with a `placeholder_unsafe` error (exit 5) and nothing runs. Refusal rather than escaping: cmd.exe quoting is not reliably composable and escaping collides with author-supplied quotes around a placeholder. `(`/`)` are intentionally allowed (so `Program Files (x86)` works) — without a blocked separator a parenthesis cannot start a command. Pure, exhaustively tested (`PlaceholderGuardTests.cs`). Residual risk: a path with a metacharacter is refused rather than run, even when benign — an accepted usability cost. |
| E3 | **Build script execution without validation** — `build.cmd`/`build.sh` are executed if they exist in the project directory, with no path validation or user confirmation. | `Program.cs` — `BuildSolutionOrProject` | **Low** | Standard behavior for build tools. The user is expected to trust the contents of their working directory. |
| E5 | **Confused-deputy via command suggestion** — A "did you mean" feature that auto-ran or default-yes-prompted its guess would let a typo execute a command the user never typed; paired with a trusted-but-hostile `commands.json` whose **Custom Command** name is a near-miss of a common builtin, a fat-finger could run attacker code (an E4-class confused deputy). | `CommandCatalog.Suggest`; `Program.cs` unknown-command exits (~line 176, ~line 213) | **Informational** | **Avoided by design (v1.13.2):** the suggestion is display-only — printed via `ctx.Log` (suppressed under JSON mode) and written to the envelope error's `suggestion` field, with no code path from a suggestion to command dispatch. It is never executed and never triggers a prompt. The config-path exit further suggests only declared **Custom Command** names (not builtins), and `Suggest` skips null/empty candidates. Should auto-run ever be added, it must be gated like other dangerous confirmations (built-ins only, interactive TTY only, default-no) to avoid reintroducing this and the **E4** `--yes` risk. |
| E4 | **`--yes` auto-accepts `commands.json` trust prompt** — The `--yes` global flag (added alongside `--json` for automation) silently approves an untrusted or modified `commands.json` and writes it to the trust store. A script or agent that blindly passes `--yes` into a directory with a hostile `commands.json` executes arbitrary shell commands without any prompt. | `Trust.cs:TrustGate.Decide` (the `AutoApproved` case); `TrustGateOrchestrator` (commits to `ITrustStore`) | **Medium** | The flag is opt-in and intended for non-interactive contexts (CI, agent-driven runs) where a prompt cannot be answered. Users and agents invoking `dev --yes` must have already vetted the working directory. **v1.13.0:** `AutoApproved` is now a distinct decision case in the pure `Decide` function — future policies (e.g., "refuse `--yes` on `NewFile`", or "require `--expected-hash`") become a one-line `Decide` change with unit tests rather than surgery on a 70-line method. Open follow-ups: (a) `--expected-hash=<hex>` flag; (b) refusing to auto-approve `NewFile` while permitting `ModifiedSinceTrust`; (c) prominent stderr warning on every auto-approval. |

---

## Risk Summary

| Severity | Count | Key Threats |
|----------|-------|-------------|
| **High → Mitigated** | 3 | S1, E1 — Untrusted `commands.json` execution (mitigated by trust system in v1.12.0; decision logic hardened by exhaustive unit tests in v1.13.0); T1 — command injection via config (trust + argv-delimited body + placeholder guard in v1.13.3) |
| **Medium → Mitigated** | 1 | S2 — NuGet package spoofing (mitigated by trusted publishing in v1.11.0) |
| **Medium** | 3 | S3, I1, E4 — Docker image trust, source exposure, `--yes` trust bypass |
| **Low → Mitigated** | 1 | T5 — Trust-store integrity under concurrent writers / crash (atomic write + cross-process lock + schema versioning in v1.13.0) |
| **Medium → Mitigated** | 2 | T3 — git argument injection via `<Version>` (argv-form `ExecArgs` + `--` guard in v1.13.1); E2 — path placeholder injection (`PlaceholderGuard` metacharacter refusal in v1.13.3) |
| **Low** | 7 | T2, T4, R1, R2, D1, D2, D3, E3 |
| **Informational** | 2 | I3; E5 — command suggestion is display-only by design (no auto-run, no prompt) |

## Recommended Mitigations (Priority Order)

1. ~~**Shell-escape placeholder values** in `ReplaceVariables` or switch to `ProcessStartInfo.ArgumentList` to avoid shell interpretation of paths containing metacharacters.~~ — **Done in v1.13.3.** `PlaceholderGuard.FindUnsafe` refuses any command whose used placeholder resolves to a metacharacter-bearing path (chosen over escaping; see **E2**/**T1**).
2. ~~**Warn on first use of `commands.json`**~~ — **Done in v1.12.0.** Hash-based trust system with confirmation prompt implemented in `VerifyCommandsTrust`.
3. **Pin the Docker image** to a specific digest instead of `:latest` for the frontend builder.
4. **Add optional timeouts** to `Process.WaitForExit()` calls, or document that Ctrl+C is the escape mechanism.
5. **Consider a confirmation prompt** for `bump-commit` before creating git commits and tags.
