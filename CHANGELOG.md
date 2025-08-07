# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[1.9.0]: https://github.com/stevehansen/HC.Dev/compare/1.8.0...1.9.0
[1.8.0]: https://github.com/stevehansen/HC.Dev/compare/1.7.1...1.8.0
[1.7.1]: https://github.com/stevehansen/HC.Dev/compare/1.7.0...1.7.1
[1.7.0]: https://github.com/stevehansen/HC.Dev/compare/1.6.0...1.7.0
[1.6.0]: https://github.com/stevehansen/HC.Dev/compare/1.5.0...1.6.0
[1.5.0]: https://github.com/stevehansen/HC.Dev/compare/1.4.1...1.5.0
[1.4.1]: https://github.com/stevehansen/HC.Dev/compare/1.4.0...1.4.1
[1.4.0]: https://github.com/stevehansen/HC.Dev/compare/1.3.0...1.4.0
[1.3.0]: https://github.com/stevehansen/HC.Dev/releases/tag/1.3.0