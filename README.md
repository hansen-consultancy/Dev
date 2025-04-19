# Dev

A .NET tool designed to simplify common development tasks for .NET projects.

## Installation

You can install the tool globally using the .NET CLI:

```bash
dotnet tool install --global Dev
```

To update to the latest version:

```bash
dotnet tool update --global Dev
```

## Features

### Launch Solution or Project

The default command launches the current solution in your default IDE or project in Visual Studio Code.

```bash
dev
# or
dev launch
```

### Bump Version

Bumps the version of all projects in the current solution or the current project.

```bash
dev bump [major|minor|patch|revision]
```

The bump command supports the following options:
- `major`: Increments the major version (e.g., 1.0.0 → 2.0.0)
- `minor`: Increments the minor version (e.g., 1.0.0 → 1.1.0) - Default
- `patch`: Increments the patch version (e.g., 1.0.0 → 1.0.1)
- `revision`: Increments the revision version (e.g., 1.0.0.0 → 1.0.0.1)

### Build Solution or Project

Builds the current solution or project in Release mode.

```bash
dev build
```

If a `build.cmd` (Windows) or `build.sh` (Linux/macOS) file exists in the same directory, it will be used instead.

### Frontend Building

Runs the Vidyano frontend builder in the current directory using Docker.

```bash
dev frontend
```

This command uses the `ghcr.io/stevehansen/vidyano-frontend-builder` Docker image to build your frontend.

### Help

Displays help information.

```bash
dev help
```

## License

This project is licensed under the MIT License. See the LICENSE file for details.