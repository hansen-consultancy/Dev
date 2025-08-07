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
- `h` or `?` → `help`
- `f` → `frontend`
- `v` → `bump`
- `vc` → `bump-commit`

### External Dependencies
- **Microsoft.VisualStudio.SolutionPersistence**: For parsing solution files
- **Spectre.Console**: For rich terminal UI
- **ThisAssembly.AssemblyInfo/Git**: For build-time assembly information

## Important Implementation Notes

- When bumping versions, the tool preserves the original version structure (major.minor.build.fix-suffix+buildvars).
- The `frontend` command uses Docker image `ghcr.io/stevehansen/vidyano-frontend-builder:latest`.
- Build files (`build.cmd` on Windows, `build.sh` on Linux/macOS) take precedence over `dotnet build` if they exist.
- The `clean` command removes `bin`, `obj`, `tmp-build`, and platform-specific build directories, excluding `node_modules`.
- Custom commands in `commands.json` support placeholders: `{sln}`, `{project}`, `{dir}`.