# Update Changelog

Please update the CHANGELOG.md file with the latest changes from git history.

## Instructions:

1. Check for any new git tags since the last entry in CHANGELOG.md
2. If there are new tags, add entries for them at the top of the changelog (after the header)
3. For unreleased changes (commits since the last tag), add an [Unreleased] section at the top
4. Analyze commit messages to categorize changes as:
   - **Added** for new features
   - **Changed** for changes in existing functionality  
   - **Deprecated** for soon-to-be removed features
   - **Removed** for now removed features
   - **Fixed** for any bug fixes
   - **Security** in case of vulnerabilities
   - **Documentation** for documentation updates

## Format:
- Follow the existing changelog format
- Use the date format YYYY-MM-DD
- Include PR numbers when available (e.g., #123)
- Group changes by category
- Keep entries concise but descriptive

## Example entry:
```markdown
## [1.9.0] - 2025-01-08

### Added
- New feature description (#PR)

### Fixed
- Bug fix description
```

Please check the current state of the changelog and update it with any missing versions or recent changes.