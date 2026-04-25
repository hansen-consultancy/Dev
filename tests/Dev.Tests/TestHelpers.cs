// Shared fakes used across multiple test files.

namespace Dev.Tests;

internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new();
    private readonly HashSet<string> _dirs = new();
    private readonly Dictionary<string, string> _parents = new();

    // Always normalize to forward-slash internally so tests stay portable across
    // Windows (where Path.Combine yields backslashes) and Linux/macOS.
    private static string Norm(string path) => path.Replace('\\', '/');

    public FakeFileSystem WithFile(string path, string content)
    {
        var n = Norm(path);
        _files[n] = content;
        var dir = System.IO.Path.GetDirectoryName(n);
        if (!string.IsNullOrEmpty(dir)) _dirs.Add(dir);
        return this;
    }

    public FakeFileSystem WithDir(string path)
    {
        _dirs.Add(Norm(path));
        return this;
    }

    public FakeFileSystem WithParent(string child, string parent)
    {
        _parents[Norm(child)] = Norm(parent);
        return this;
    }

    public string? GetWritten(string path)
        => _files.TryGetValue(Norm(path), out var c) ? c : null;

    public bool FileExists(string path) => _files.ContainsKey(Norm(path));
    public bool DirExists(string path) => _dirs.Contains(Norm(path));

    public string[] GetFiles(string dir, string pattern)
    {
        var prefix = Norm(dir);
        if (!prefix.EndsWith('/')) prefix += '/';
        return _files.Keys
            .Where(p => p.StartsWith(prefix) && !p[prefix.Length..].Contains('/'))
            .Where(p => MatchesGlob(System.IO.Path.GetFileName(p), pattern))
            .ToArray();
    }

    public string ReadAllText(string path)
        => _files.TryGetValue(Norm(path), out var c) ? c : throw new FileNotFoundException(path);

    public void WriteAllText(string path, string contents)
    {
        var n = Norm(path);
        _files[n] = contents;
        var dir = System.IO.Path.GetDirectoryName(n);
        if (!string.IsNullOrEmpty(dir)) _dirs.Add(dir);
    }

    public string? GetParentDir(string path)
    {
        var n = Norm(path);
        if (_parents.TryGetValue(n, out var p)) return p;
        var idx = n.LastIndexOf('/');
        if (idx <= 0) return null;
        return n[..idx];
    }

    private static bool MatchesGlob(string name, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith("*."))
        {
            var rest = pattern[1..];
            if (rest.EndsWith('*'))
            {
                var stem = rest[..^1];
                return name.Contains(stem, StringComparison.OrdinalIgnoreCase);
            }
            return name.EndsWith(rest, StringComparison.OrdinalIgnoreCase);
        }
        return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class FakeSolutionReader : ISolutionReader
{
    private readonly Dictionary<string, IEnumerable<string>> _responses = new();
    private readonly Dictionary<string, Exception> _throws = new();

    public void ReturnsForSolution(string slnPath, IEnumerable<string> projects)
        => _responses[slnPath] = projects;

    public void ThrowsForSolution(string slnPath, Exception ex)
        => _throws[slnPath] = ex;

    public IEnumerable<string> ReadProjectPaths(string slnPath)
    {
        if (_throws.TryGetValue(slnPath, out var ex)) throw ex;
        return _responses.TryGetValue(slnPath, out var p) ? p : Array.Empty<string>();
    }
}

internal sealed class ScriptedPrompter : IConfirmationPrompt
{
    private readonly Queue<bool> _answers;
    public List<string> Questions { get; } = new();

    public ScriptedPrompter(params bool[] answers) => _answers = new Queue<bool>(answers);

    public bool Confirm(string question)
    {
        Questions.Add(question);
        if (_answers.Count == 0)
            throw new InvalidOperationException($"ScriptedPrompter ran out of answers for: {question}");
        return _answers.Dequeue();
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcRunResult> _responses = new();
    public List<ProcSpec> Invocations { get; } = new();

    public void Enqueue(ProcRunResult result) => _responses.Enqueue(result);

    public ProcRunResult Run(ProcSpec spec, RunContext ctx)
    {
        Invocations.Add(spec);
        if (_responses.Count > 0) return _responses.Dequeue();
        return new ProcRunResult(0, Array.Empty<string>(), Array.Empty<string>(),
                                 spec.DisplayShell, spec.DisplayCommandLine);
    }
}
