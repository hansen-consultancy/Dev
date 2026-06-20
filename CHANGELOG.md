# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.14.0] - 2026-06-20

### Added
- "Did you mean …?" suggestion when an unknown command is typed: a display-only nearest-match hint (never auto-run; suppressed under `--json` but surfaced in the envelope error's `suggestion` field)

### Changed
- Internal: decomposed process execution, version-bump, workspace discovery, the `frontend` command, and the `commands.json` trust gate into small, individually tested modules and ports (behavior-preserving)

### Security
- Route the version-bump `git add`/`commit`/`tag` operations through argv-form `ProcSpec.ExecArgs` (each value delivered as one `ArgumentList` token, with a `--` separator on `git add`) instead of interpolated argument strings, so a crafted `<Version>` in a cloned repo can no longer split or inject extra git arguments (SECURITY_REVIEW F1 / STRIDE T3).
- Refuse to run a custom command when a used `{sln}`/`{project}`/`{dir}` placeholder resolves to a path containing a shell metacharacter (`& | ; < > \` $ " ' %`, CR/LF/NUL), closing the placeholder-injection vector (SECURITY_REVIEW F2 / STRIDE E2/T1). The trusted command body keeps its shell features; only the substituted paths are guarded.
- Pin the frontend builder Docker image by digest (`…:latest@sha256:b89acec0…`) instead of floating on `:latest`, so a repointed or compromised tag can no longer be substituted for the image mounted at `/src` (SECURITY_REVIEW F5 / STRIDE S3). Update the `Program.FrontendImage` constant to ship a new builder.

## [1.13.0] - 2026-04-17

### Added
- `--json` output mode with structured envelope, per-step results, and exit codes (0-6)
- `--yes` flag to auto-accept `commands.json` trust prompts and frontend file-creation prompts
- Capture of stderr and last 50 lines of stdout on child process failure, included in JSON data payload
- Help output in JSON mode includes aliases, chaining rule, placeholders, and global flags

### Security
- STRIDE: added E4 threat for `--yes` trust bypass (Medium severity)

## [1.12.0] - 2026-02-21

### Added
- Hash-based trust system for `commands.json`: requires explicit user approval before executing custom commands, using SHA-256 hashing with a persistent trust store at `%APPDATA%/hc-dev/trust.json`

### Security
- Mitigates STRIDE threats S1, T1, E1 via the new trust system
- Updated STRIDE S2 to reflect NuGet trusted publishing mitigation

### Documentation
- Added STRIDE threat model (`STRIDE.md`)

## [1.11.0] - 2026-02-02

### Added
- `dev.json` configuration file support to ignore specific projects during version bump
- GitHub Actions workflow for NuGet trusted publishing via OIDC

## [1.10.0] - 2025-11-17

### Fixed
- Skip git submodules when bumping versions to avoid modifying submodule contents

## [1.9.0] - 2025-08-07

### Added
- Command combination support using '+' operator (e.g., `dev b+f` to build then run frontend)
- 'c' alias for clean command
- src/ folder checking when no solution is found in current directory (#1) (#9)

### Documentation
- CLAUDE.md project instructions
- CHANGELOG.md and update-changelog command

## [1.8.0] - 2025-05-24

### Added
- Configurable commands support (#7)

### Documentation
- Added clean command to readme (#6)

## [1.7.1] - 2025-05-02

### Added
- Command to cleanout the CWD (#4) (#5)

### Fixed
- Incorrect MarkupLine

## [1.7.0] - 2025-04-23

### Fixed
- WaitForExit if needed

## [1.6.0] - 2025-04-20

### Added
- SpectreConsole for improved terminal UI
- Frontend helper for creating missing files
- Logo for the package

### Changed
- Include logo and readme in package

## [1.5.0] - 2025-04-20

### Added
- Support for .slnx files using Microsoft.VisualStudio.SolutionPersistence
- Command aliases for improved usability
- bump-commit command for version bumping with auto-commit

### Changed
- PackageId updated to HC.Dev

### Fixed
- Prefer shortest path when looking for .sln file

### Documentation
- Updated README for new package id

## [1.4.1] - 2025-04-19

### Fixed
- Don't run frontend in separate shell

### Changed
- Exclude nupkg folder from solution

## [1.4.0] - 2025-04-19

### Added
- Frontend command for frontend development tasks
- README documentation
- Package as .NET tool

## [1.3.0] - 2024-05-22

### Added
- Support for build.cmd/.sh files for custom build scripts
- .NET 8 support
- Version info in assembly

### Changed
- Updated package dependencies

[1.11.0]: https://github.com/hansen-consultancy/Dev/compare/1.10.0...1.11.0
[1.10.0]: https://github.com/hansen-consultancy/Dev/compare/1.9.0...1.10.0
[1.9.0]: https://github.com/hansen-consultancy/Dev/compare/1.8.0...1.9.0
[1.8.0]: https://github.com/hansen-consultancy/Dev/compare/1.7.1...1.8.0
[1.7.1]: https://github.com/hansen-consultancy/Dev/compare/1.7.0...1.7.1
[1.7.0]: https://github.com/hansen-consultancy/Dev/compare/1.6.0...1.7.0
[1.6.0]: https://github.com/hansen-consultancy/Dev/compare/1.5.0...1.6.0
[1.5.0]: https://github.com/hansen-consultancy/Dev/compare/1.4.1...1.5.0
[1.4.1]: https://github.com/hansen-consultancy/Dev/compare/1.4.0...1.4.1
[1.4.0]: https://github.com/hansen-consultancy/Dev/compare/1.3.0...1.4.0
[1.3.0]: https://github.com/hansen-consultancy/Dev/releases/tag/1.3.0