# STRIDE Threat Model — HC.Dev (`dev` CLI tool)

> **Version:** 1.11.0
> **Date:** 2026-02-20
> **Scope:** The `dev` .NET global tool, its configuration files, and its interactions with the local system.

## Overview

HC.Dev is a .NET 8.0 CLI tool distributed as a NuGet global tool. It operates in the context of the current working directory, detects solution/project files, and executes development tasks such as building, version bumping, launching IDEs, running Docker containers, and executing user-defined custom commands.

### Trust Boundaries

1. **User ↔ Tool**: The developer invoking `dev` from the terminal.
2. **Tool ↔ File System**: Reading/writing project files, config files, build scripts.
3. **Tool ↔ External Processes**: Spawning `dotnet`, `git`, `cmd.exe`/`bash`, `docker`, `code`, and custom build scripts.
4. **Tool ↔ NuGet**: Distribution and installation of the tool package.
5. **Tool ↔ Docker Registry**: Pulling the `ghcr.io/stevehansen/vidyano-frontend-builder` image.

### Assets

- Source code and project files (`.csproj`, `.sln`)
- Version metadata in project files
- Git repository state (commits, tags)
- Configuration files (`commands.json`, `dev.json`)
- Build scripts (`build.cmd`, `build.sh`, `build-frontend.sh`)
- Docker volume-mounted source directory

---

## S — Spoofing

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| S1 | **Malicious `commands.json` in cloned repo** — An attacker commits a crafted `commands.json` to a repository. When a developer runs `dev`, arbitrary commands execute under their identity. | `Program.cs:186-197` — Custom command execution via `cmd.exe /c` or `bash -c` | **High** | The tool trusts configuration files in the working directory by design. Users should review `commands.json` in unfamiliar repositories before running `dev`. Consider displaying a warning on first use or when the file changes. |
| S2 | **Malicious NuGet package substitution** — An attacker publishes a package with a similar name (typosquatting `HC.Dev`) to execute malicious code when installed. | NuGet distribution | **Medium** | The package uses a scoped ID (`HC.Dev`) with a specific `ToolCommandName`. Users should verify the package source. NuGet trusted publishing (added in recent CI) helps mitigate this. |
| S3 | **Docker image spoofing** — If the Docker registry or user's Docker config is compromised, a malicious image could replace `ghcr.io/stevehansen/vidyano-frontend-builder:latest`. | `Program.cs:293` — Docker run command | **Medium** | Consider pinning the Docker image to a specific digest rather than `:latest`. Ensure the GitHub Container Registry package has appropriate access controls. |

---

## T — Tampering

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| T1 | **Command injection via `commands.json`** — Custom commands from `commands.json` are passed directly to `cmd.exe /c` or `bash -c` without sanitization. A malicious config can execute arbitrary shell commands. | `Program.cs:194-197` | **High** | This is by design (the tool is meant to run user-defined commands), but the implicit trust is dangerous in shared/cloned repositories. Variable placeholders (`{sln}`, `{project}`, `{dir}`) are also injected unsanitized into the shell command string. If a directory or file path contains shell metacharacters, this could lead to unintended command execution. |
| T2 | **Build script tampering** — The tool executes `build.cmd`/`build.sh` if found in the project directory, with no integrity check. | `Program.cs:497-503` — `BuildSolutionOrProject` | **Medium** | An attacker who can write to the project directory can replace build scripts. This is a standard risk for local development tools. |
| T3 | **Version file tampering via regex** — The version regex `<Version>(?<version>.*)</Version>` uses a greedy match that could be exploited with crafted `.csproj` content to write unexpected values. | `Program.cs:626-627`, `BumpProjectVersion` | **Low** | The regex is simple and applied to XML content the developer controls. Risk is minimal in practice. |
| T4 | **Parent directory `dev.json` traversal** — The tool checks the parent directory for `dev.json` if not found locally. A malicious `dev.json` placed in a shared parent directory could influence the tool's behavior (e.g., suppressing version bumps via `IgnoreProjects`). | `Program.cs:38-46` | **Low** | Only traverses one level up. The impact is limited to ignoring projects during version bumping. |

---

## R — Repudiation

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| R1 | **Automated git commits without audit trail** — The `bump-commit` command creates git commits and tags automatically. If the tool is run unintentionally or by an unauthorized script, changes are committed without explicit user confirmation. | `Program.cs:369-393` — `bump-commit` flow | **Low** | Git commits include author information from the local git config. The commit message follows a fixed format (`build: {version}`). The risk is low since this requires local access. Consider adding a confirmation prompt for commit operations. |
| R2 | **No logging of custom command execution** — When custom commands from `commands.json` run, there is no persistent log of what was executed. | `Program.cs:186-197` | **Low** | The command output goes to stdout, but there is no file-based audit log. For a local development tool, this is acceptable. |

---

## I — Information Disclosure

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| I1 | **Source code exposed via Docker volume mount** — The `frontend` command mounts the entire working directory into a Docker container (`-v "{path}:/src"`). If the Docker image is compromised, all source code is accessible. | `Program.cs:293` | **Medium** | The volume mount exposes the full working directory. Consider mounting only the necessary subdirectory if possible. Users should verify the Docker image integrity. |
| I2 | **Exception details displayed to console** — Exceptions from config file parsing are written to the console via `AnsiConsole.WriteException`, which may reveal file paths and internal details. | `Program.cs:29-30`, `Program.cs:57-58` | **Low** | This is a developer tool and the output is only visible to the local user. No sensitive data beyond file paths is exposed. |
| I3 | **Version and git info in banner** — The tool displays its version and git commit hash on every run. | `Program.cs:12-13` | **Informational** | This is intentional and useful. No action needed. |

---

## D — Denial of Service

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| D1 | **Recursive directory enumeration** — The `clean` command enumerates all directories recursively (`SearchOption.AllDirectories`). In a directory with an extremely deep or wide structure (e.g., symlink loops), this could hang or consume excessive memory. | `Program.cs:308-310` | **Low** | The `node_modules` exclusion helps, but symlink loops or very large trees could still be problematic. This is a minor risk for a local CLI tool. |
| D2 | **Blocking `WaitForExit()` on spawned processes** — All `Process.Start(...).WaitForExit()` calls block indefinitely. A hung build, Docker pull, or custom command will block the tool forever. | Multiple locations | **Low** | Consider adding optional timeouts. For a local interactive tool, the user can always Ctrl+C. |
| D3 | **Malicious `commands.json` causing infinite loop** — A crafted config could define a default command that invokes `dev` recursively. | `Program.cs:66-67` | **Low** | No recursion guard exists. In practice, the process stack would eventually exhaust resources. |

---

## E — Elevation of Privilege

| # | Threat | Affected Component | Severity | Mitigation |
|---|--------|--------------------|----------|------------|
| E1 | **Arbitrary command execution via `commands.json`** — Custom commands run with the full privileges of the invoking user. A malicious `commands.json` in a cloned repository could execute commands the user did not intend (e.g., `curl ... \| bash`, modifying system files, exfiltrating credentials). | `Program.cs:186-197` | **High** | This is the most significant risk. The tool blindly trusts `commands.json` in the working directory. Mitigations: (1) warn users when `commands.json` is present in a new/untrusted repo, (2) display the command before execution and prompt for confirmation, (3) consider a sandboxing mechanism or allowlist. |
| E2 | **Shell metacharacter injection via path placeholders** — The `{sln}`, `{project}`, and `{dir}` placeholders in custom commands are replaced with file/directory paths and passed to `cmd.exe /c` or `bash -c`. If paths contain shell metacharacters (`;`, `&&`, `\|`, backticks, `$(...)`), this enables command injection. | `Program.cs:603-609` (ReplaceVariables), `Program.cs:194-197` | **Medium** | Paths are not shell-escaped before substitution. Consider properly quoting or escaping path values when constructing shell command strings, or use argument arrays with `ProcessStartInfo` to avoid shell interpretation entirely. |
| E3 | **Build script execution without validation** — `build.cmd`/`build.sh` are executed if they exist in the project directory, with no path validation or user confirmation. | `Program.cs:497-503` | **Low** | Standard behavior for build tools. The user is expected to trust the contents of their working directory. |

---

## Risk Summary

| Severity | Count | Key Threats |
|----------|-------|-------------|
| **High** | 3 | S1, T1, E1 — All related to untrusted `commands.json` execution |
| **Medium** | 4 | S2, S3, I1, E2 — Package spoofing, Docker image trust, path injection |
| **Low** | 7 | T2, T3, T4, R1, R2, D1, D2, D3, E3 |
| **Informational** | 1 | I3 |

## Recommended Mitigations (Priority Order)

1. **Shell-escape placeholder values** in `ReplaceVariables` or switch to `ProcessStartInfo.ArgumentList` to avoid shell interpretation of paths containing metacharacters.
2. **Warn on first use of `commands.json`** in a repository, or display the resolved command and prompt before executing custom commands for the first time.
3. **Pin the Docker image** to a specific digest instead of `:latest` for the frontend builder.
4. **Add optional timeouts** to `Process.WaitForExit()` calls, or document that Ctrl+C is the escape mechanism.
5. **Consider a confirmation prompt** for `bump-commit` before creating git commits and tags.
