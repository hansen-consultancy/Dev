// Target discovery for the `synchronize` command: which project in the Workspace
// is the Vidyano app whose model/schema files the Vidyano service CLI rewrites.
// An app is recognized by the model file the service persists next to it
// (App_Data/model.json, or wwwroot/App_Data/model.json as the Vidyano.Service
// samples lay it out) — never by parsing the csproj or its package references, so
// discovery survives Vidyano repackaging. Pure apart from the IFileSystem probe.

namespace Dev;

internal readonly record struct VidyanoApp(string ProjectPath, string Name);

// Either the single resolved app, or the reason no single one could be picked —
// already shaped as the envelope's error code/message, with the candidate names
// the user can pass as `dev sync <project>`.
internal sealed record VidyanoAppResolution(
    VidyanoApp? App, string? Code, string? Message, IReadOnlyList<string> Candidates)
{
    public static VidyanoAppResolution Found(VidyanoApp app) => new(app, null, null, [app.Name]);
}

internal static class VidyanoAppLocator
{
    private static readonly string[] ModelFileProbes =
    [
        Path.Combine("App_Data", "model.json"),
        Path.Combine("wwwroot", "App_Data", "model.json"),
    ];

    public static VidyanoAppResolution Resolve(
        IReadOnlyList<string> projectPaths, string? requestedName, IFileSystem fs)
    {
        var candidates = projectPaths
            .Where(p => IsVidyanoApp(p, fs))
            .Select(p => new VidyanoApp(p, Path.GetFileNameWithoutExtension(p)))
            .ToList();
        var names = candidates.Select(c => c.Name).ToList();

        if (candidates.Count == 0)
            return new VidyanoAppResolution(null, "no_vidyano_project",
                "No Vidyano app found in the current solution or project (no App_Data/model.json next to any project).",
                names);

        if (requestedName is not null)
        {
            var match = candidates.FirstOrDefault(c => string.Equals(c.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            return match.ProjectPath is not null
                ? VidyanoAppResolution.Found(match)
                : new VidyanoAppResolution(null, "no_vidyano_project",
                    $"No Vidyano app named '{requestedName}' in the current solution. Candidates: {string.Join(", ", names)}.",
                    names);
        }

        if (candidates.Count > 1)
            return new VidyanoAppResolution(null, "ambiguous_vidyano_project",
                $"The solution contains more than one Vidyano app; name the one to synchronize with 'dev sync <project>'. Candidates: {string.Join(", ", names)}.",
                names);

        return VidyanoAppResolution.Found(candidates[0]);
    }

    private static bool IsVidyanoApp(string projectPath, IFileSystem fs)
    {
        var dir = Path.GetDirectoryName(projectPath);
        return !string.IsNullOrEmpty(dir)
            && ModelFileProbes.Any(probe => fs.FileExists(Path.Combine(dir, probe)));
    }
}
