# STRIDE Threat Model — HC.Dev (`dev` CLI tool)

> **Version:** 1.13.0
> **Date:** 2026-04-25
> **Scope:** The `dev` .NET global tool, its configuration files, and its interactions with the local system.

> **v1.13.0 note:** Child-process execution was extracted from `Program.cs` into a `ProcessRunner.cs` port + adapter (`IProcessRunner`, `ProcSpec`, `ProcRunResult`, `RealProcessRunner`). Behavior is preserved verbatim — no threat severities change — but threats that pivot on process spawning, OS-shell wrapping, output tailing, or `WaitForExit` now point at `ProcessRunner.cs` in addition to (or instead of) the call sites in `Program.cs`.

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
| S1 | **Malicious `commands.json` in cloned repo** — An attacker commits a crafted `commands.json` to a repository. When a developer runs `dev`, arbitrary commands execute under their identity. | `Program.cs:RunCustomCommand` → `ProcessRunner.cs:RealProcessRunner` (via `ProcSpec.Shell` → `cmd.exe /c` or `bash -c`) | **High** → **Mitigated** | **Mitigated in v1.12.0:** A hash-based trust system (`VerifyCommandsTrust`) now blocks execution of any `commands.json` that has not been explicitly approved by the user. On first encounter or when the file changes, the tool displays a warning, shows a summary of all commands (including shell commands that would run), and requires explicit confirmation (defaulting to "no"). Approved configs are recorded by full path and SHA-256 hash in `%APPDATA%/hc-dev/trust.json`. Residual risk: a user may approve a malicious config without carefully reading the summary. |
| S2 | **Malicious NuGet package substitution** — An attacker publishes a package with a similar name (typosquatting `HC.Dev`) to execute malicious code when installed. | NuGet distribution | **Medium** → **Mitigated** | **Mitigated in v1.11.0:** NuGet trusted publishing workflow added to CI, ensuring only verified builds from the official repository can publish the package. The package uses a scoped ID (`HC.Dev`) with a specific `ToolCommandName`. Residual risk: typosquatting with a different package ID remains possible but is outside the project's control. |
| S3 | **Docker image spoofing** — If the Docker registry or user's Docker config is compromised, a malicious image could replace `ghcr.io/stevehansen/vidyano-frontend-builder:latest`. | `Program.cs:RunFrontend` (~line 560) — Docker run command | **Medium** | Consider pinning the Docker image to a specific digest rather than `:latest`. Ensure the GitHub Container Registry package has appropriate access controls. |

---

## T — Tampering

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| T1 | **Command injection via `commands.json`** — Custom commands from `commands.json` are passed directly to `cmd.exe /c` or `bash -c` without sanitization. A malicious config can execute arbitrary shell commands. | `Program.cs:RunCustomCommand` → `ProcSpec.Shell` (`ProcessRunner.cs`) | **High** → **Partially Mitigated** | **Partially mitigated in v1.12.0:** The trust system ensures the user must explicitly approve the `commands.json` content before any commands execute. The command summary shows the exact shell commands that will run, giving the user visibility. However, variable placeholders (`{sln}`, `{project}`, `{dir}`) are still injected unsanitized — if paths contain shell metacharacters, this could lead to unintended command execution. The trust check covers the config file itself, not the runtime-resolved commands. |
| T2 | **Build script tampering** — The tool executes `build.cmd`/`build.sh` if found in the project directory, with no integrity check. | `Program.cs:BuildSolutionOrProject` (~line 617) → `ProcessRunner.cs:RealProcessRunner` | **Medium** | An attacker who can write to the project directory can replace build scripts. This is a standard risk for local development tools. |
| T3 | **Version file tampering via regex** — The version regex `<Version>(?<version>.*)</Version>` uses a greedy match that could be exploited with crafted `.csproj` content to write unexpected values. | `Program.cs:BumpProjectVersion` (~line 584), `VersionRegex` (~line 919) | **Low** | The regex is simple and applied to XML content the developer controls. Risk is minimal in practice. |
| T4 | **Parent directory `dev.json` traversal** — The tool checks the parent directory for `dev.json` if not found locally. A malicious `dev.json` placed in a shared parent directory could influence the tool's behavior (e.g., suppressing version bumps via `IgnoreProjects`). | `Program.cs:66-76` | **Low** | Only traverses one level up. The impact is limited to ignoring projects during version bumping. |

---

## R — Repudiation

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| R1 | **Automated git commits without audit trail** — The `bump-commit` command creates git commits and tags automatically. If the tool is run unintentionally or by an unauthorized script, changes are committed without explicit user confirmation. | `Program.cs:RunBumpCommit` (~lines 311–390) — `bump-commit` flow | **Low** | Git commits include author information from the local git config. The commit message follows a fixed format (`build: {version}`). The risk is low since this requires local access. Consider adding a confirmation prompt for commit operations. |
| R2 | **No logging of custom command execution** — When custom commands from `commands.json` run, there is no persistent log of what was executed. | `Program.cs:RunCustomCommand` (~line 255) | **Low** | The command output goes to stdout, but there is no file-based audit log. For a local development tool, this is acceptable. |

---

## I — Information Disclosure

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| I1 | **Source code exposed via Docker volume mount** — The `frontend` command mounts the entire working directory into a Docker container (`-v "{path}:/src"`). If the Docker image is compromised, all source code is accessible. | `Program.cs:RunFrontend` (~line 561) | **Medium** | The volume mount exposes the full working directory. Consider mounting only the necessary subdirectory if possible. Users should verify the Docker image integrity. |
| I2 | **Exception details displayed to console** — Exceptions from config file parsing (and a child-process start failure) are written to the console via `AnsiConsole.WriteException`, which may reveal file paths and internal details. | `Program.cs:54-55`, `Program.cs:89-90`, `Program.cs:447-448`, `ProcessRunner.cs:RealProcessRunner` (start-failure path) | **Low** | This is a developer tool and the output is only visible to the local user. No sensitive data beyond file paths is exposed. |
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
| E1 | **Arbitrary command execution via `commands.json`** — Custom commands run with the full privileges of the invoking user. A malicious `commands.json` in a cloned repository could execute commands the user did not intend (e.g., `curl ... \| bash`, modifying system files, exfiltrating credentials). | `Program.cs:RunCustomCommand` → `ProcessRunner.cs:RealProcessRunner` | **High** → **Mitigated** | **Mitigated in v1.12.0:** The tool no longer blindly trusts `commands.json`. All three recommended controls are now implemented: (1) a warning is shown when `commands.json` is new or modified, (2) a full command summary is displayed before execution, and (3) the user must explicitly confirm with a prompt that defaults to "no". The SHA-256 hash ensures any file modification triggers re-approval. Residual risk: once approved, commands run with full user privileges — no sandboxing is applied. |
| E2 | **Shell metacharacter injection via path placeholders** — The `{sln}`, `{project}`, and `{dir}` placeholders in custom commands are replaced with file/directory paths and passed to `cmd.exe /c` or `bash -c`. If paths contain shell metacharacters (`;`, `&&`, `\|`, backticks, `$(...)`), this enables command injection. | `Program.cs:ReplaceVariables` (~line 697), `Program.cs:RunCustomCommand` (~line 267), `ProcessRunner.cs:ProcSpec.Shell` | **Medium** | Paths are not shell-escaped before substitution. Consider properly quoting or escaping path values when constructing shell command strings, or use argument arrays with `ProcessStartInfo` to avoid shell interpretation entirely. |
| E3 | **Build script execution without validation** — `build.cmd`/`build.sh` are executed if they exist in the project directory, with no path validation or user confirmation. | `Program.cs` — `BuildSolutionOrProject` | **Low** | Standard behavior for build tools. The user is expected to trust the contents of their working directory. |
| E4 | **`--yes` auto-accepts `commands.json` trust prompt** — The `--yes` global flag (added alongside `--json` for automation) silently approves an untrusted or modified `commands.json` and writes it to the trust store. A script or agent that blindly passes `--yes` into a directory with a hostile `commands.json` executes arbitrary shell commands without any prompt. | `Program.cs` — `VerifyCommandsTrust` | **Medium** | The flag is opt-in and intended for non-interactive contexts (CI, agent-driven runs) where a prompt cannot be answered. Users and agents invoking `dev --yes` must have already vetted the working directory. Consider: (a) requiring `--yes` to additionally specify an expected hash, (b) refusing to auto-approve when the config is newly-introduced vs. only-modified, or (c) emitting a prominent stderr warning on every auto-approval. |

---

## Risk Summary

| Severity | Count | Key Threats |
|----------|-------|-------------|
| **High → Mitigated** | 2 | S1, E1 — Untrusted `commands.json` execution (mitigated by trust system in v1.12.0) |
| **High → Partially Mitigated** | 1 | T1 — Command injection via config (trust system + command summary, but placeholder injection remains) |
| **Medium → Mitigated** | 1 | S2 — NuGet package spoofing (mitigated by trusted publishing in v1.11.0) |
| **Medium** | 4 | S3, I1, E2, E4 — Docker image trust, source exposure, path injection, `--yes` trust bypass |
| **Low** | 7 | T2, T3, T4, R1, R2, D1, D2, D3, E3 |
| **Informational** | 1 | I3 |

## Recommended Mitigations (Priority Order)

1. **Shell-escape placeholder values** in `ReplaceVariables` or switch to `ProcessStartInfo.ArgumentList` to avoid shell interpretation of paths containing metacharacters.
2. ~~**Warn on first use of `commands.json`**~~ — **Done in v1.12.0.** Hash-based trust system with confirmation prompt implemented in `VerifyCommandsTrust`.
3. **Pin the Docker image** to a specific digest instead of `:latest` for the frontend builder.
4. **Add optional timeouts** to `Process.WaitForExit()` calls, or document that Ctrl+C is the escape mechanism.
5. **Consider a confirmation prompt** for `bump-commit` before creating git commits and tags.
