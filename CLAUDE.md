# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

### Building the Project
```bash
dotnet build -c Release
```

### Packaging as NuGet Tool
```bash
dotnet pack
```
The NuGet packages are output to `./nupkg/` directory.

### Installing/Updating the Tool Locally
```bash
# Install globally
dotnet tool install --global HC.Dev

# Update to latest
dotnet tool update --global HC.Dev
```

## Architecture Overview

This is a .NET 8.0 command-line tool (`dev`) that simplifies common development tasks for .NET projects. The tool is distributed as a NuGet package (`HC.Dev`).

### Core Components

- **Program.cs**: Main entry point that handles command routing and execution. Uses Spectre.Console for terminal UI.
- **SemVer.cs**: Semantic versioning implementation for version bumping operations.
- **Dev.csproj**: Project configuration with NuGet package metadata and tool configuration.

### Key Features Implementation

1. **Command System**: The tool supports both built-in commands and custom commands via `commands.json` configuration files.

2. **Solution/Project Detection**: Automatically detects `.sln`, `.slnx`, or `.csproj` files in the current directory to determine context.

3. **Version Management**: Uses regex pattern `<Version>(?<version>.*)</Version>` to find and update versions in project files.

4. **Cross-Platform Support**: Uses `RuntimeInformation.IsOSPlatform()` to handle platform-specific behaviors (Windows vs Linux/macOS).

### Command Aliases
- `b` → `build`
- `c` → `clean`
- `h` or `?` → `help`
- `f` → `frontend`
- `v` → `bump`
- `vc` → `bump-commit`

### Command Combinations
Commands can be combined using the `+` operator to execute them sequentially (e.g., `dev b+f` to build then run frontend). Execution stops if any command fails.

### External Dependencies
- **Microsoft.VisualStudio.SolutionPersistence**: For parsing solution files
- **Spectre.Console**: For rich terminal UI
- **ThisAssembly.AssemblyInfo/Git**: For build-time assembly information

## Ubiquitous Language

`UBIQUITOUS_LANGUAGE.md` (repo root) is the canonical domain glossary — the agreed vocabulary for the CLI's run/command, workspace/versioning, trust, and frontend subdomains. Use these terms in code, comments, help text, and JSON envelope fields; consult its "Flagged ambiguities" before naming new concepts — notably **Build** (compile command vs. SemVer 3rd component vs. build metadata), **Patch/Revision** (CLI bump parts) vs. their `Build`/`Fix` SemVer fields, **Target** (`BuildTarget` what-to-compile vs. `BumpTarget` what-to-version), and **Built-in** (the command verb vs. the `BuiltinCommand` record vs. the `BuiltIn` config field). Update it when introducing or renaming a domain concept.

## Security & STRIDE Threat Model

This project maintains a STRIDE threat model in `STRIDE.md`. When making changes, **always review whether `STRIDE.md` needs updating**. This applies to:

- **New features** that introduce trust boundaries, external process execution, file I/O, or user input handling
- **Security-related changes** such as trust mechanisms, authentication, input validation, or sandboxing
- **Changes to existing threats** — if a mitigation is implemented, update the affected threat's severity and mitigation column
- **New configuration surfaces** — any new config files, environment variables, or CLI flags that could be abused
- **Dependency changes** — new external processes, Docker images, NuGet packages, or network calls
- **When a change mitigates or resolves an existing finding** — re-scope it to Mitigated (update the mitigation text, severity, and Risk Summary row)

**Updates are bidirectional and ride in the same PR.** Whether a change *introduces* a threat or *mitigates* one, the matching `STRIDE.md` edit ships in the **same PR** as the code/config change — never as a follow-up. A fix that closes a tracked finding is not done until `STRIDE.md` reflects it; treat a security-relevant diff with no `STRIDE.md` change as incomplete.

When updating `STRIDE.md`:
1. Update the version and date at the top (doc version is independent of the tool version)
2. Add/modify threat entries in the appropriate STRIDE category, including the **Control** column (OWASP ASVS 5.0 chapter, or the local/infra control where ASVS is thin — see the "Control citations" note in `STRIDE.md`)
3. Update the Risk Summary table
4. Update the Recommended Mitigations section (strike through completed items)
5. Link GitHub issues for unresolved High/Critical findings (label: `security`) and review the model after major releases

## Important Implementation Notes

- When bumping versions, the tool preserves the original version structure (major.minor.build.fix-suffix+buildvars).
- The `frontend` command uses Docker image `ghcr.io/stevehansen/vidyano-frontend-builder:latest`.
- Build files (`build.cmd` on Windows, `build.sh` on Linux/macOS) take precedence over `dotnet build` if they exist.
- The `clean` command removes `bin`, `obj`, `tmp-build`, and platform-specific build directories, excluding `node_modules`.
- Custom commands in `commands.json` support placeholders: `{sln}`, `{project}`, `{dir}`.