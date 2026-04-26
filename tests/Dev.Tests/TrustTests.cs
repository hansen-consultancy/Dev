using System.Text.Json;
using Xunit;

namespace Dev.Tests;

public sealed class TrustGateDecideTests
{
    private static readonly IReadOnlyList<CommandsConfigEntry> EmptyCommands = Array.Empty<CommandsConfigEntry>();

    private static TrustRequest Req(string path = "/repo/commands.json", string hash = "h1")
        => new(path, hash, EmptyCommands);

    private static TrustStoreSnapshot Empty() =>
        new(1, new Dictionary<string, string>());

    private static TrustStoreSnapshot With(string path, string hash) =>
        new(1, new Dictionary<string, string> { [path] = hash });

    // --- AlreadyTrusted: path + hash both match. Flags should not matter. ---

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Path_and_hash_match_yields_AlreadyTrusted_regardless_of_flags(bool autoYes, bool jsonMode)
    {
        var d = TrustGate.Decide(Req(), With("/repo/commands.json", "h1"), new TrustFlags(autoYes, jsonMode));
        Assert.IsType<TrustDecision.AlreadyTrusted>(d);
    }

    // --- NewFile (path not in store) × flags ---

    [Fact]
    public void NewFile_no_yes_no_json_yields_PromptRequired_NewFile()
    {
        var d = TrustGate.Decide(Req(), Empty(), new TrustFlags(AutoYes: false, JsonMode: false));
        var p = Assert.IsType<TrustDecision.PromptRequired>(d);
        Assert.Equal(PromptReason.NewFile, p.Reason);
    }

    [Fact]
    public void NewFile_with_yes_yields_AutoApproved_NewFile()
    {
        var d = TrustGate.Decide(Req(), Empty(), new TrustFlags(AutoYes: true, JsonMode: false));
        var a = Assert.IsType<TrustDecision.AutoApproved>(d);
        Assert.Equal(PromptReason.NewFile, a.Reason);
    }

    [Fact]
    public void NewFile_json_without_yes_yields_InteractionRequired_NewFile()
    {
        var d = TrustGate.Decide(Req(), Empty(), new TrustFlags(AutoYes: false, JsonMode: true));
        var i = Assert.IsType<TrustDecision.InteractionRequired>(d);
        Assert.Equal(PromptReason.NewFile, i.Reason);
    }

    [Fact]
    public void NewFile_json_with_yes_yields_AutoApproved_NewFile()
    {
        var d = TrustGate.Decide(Req(), Empty(), new TrustFlags(AutoYes: true, JsonMode: true));
        var a = Assert.IsType<TrustDecision.AutoApproved>(d);
        Assert.Equal(PromptReason.NewFile, a.Reason);
    }

    // --- Modified (path in store but hash differs) × flags ---

    [Fact]
    public void Modified_no_yes_no_json_yields_PromptRequired_ModifiedSinceTrust()
    {
        var d = TrustGate.Decide(Req(hash: "newhash"),
                                 With("/repo/commands.json", "oldhash"),
                                 new TrustFlags(false, false));
        var p = Assert.IsType<TrustDecision.PromptRequired>(d);
        Assert.Equal(PromptReason.ModifiedSinceTrust, p.Reason);
    }

    [Fact]
    public void Modified_with_yes_yields_AutoApproved_ModifiedSinceTrust()
    {
        var d = TrustGate.Decide(Req(hash: "newhash"),
                                 With("/repo/commands.json", "oldhash"),
                                 new TrustFlags(true, false));
        var a = Assert.IsType<TrustDecision.AutoApproved>(d);
        Assert.Equal(PromptReason.ModifiedSinceTrust, a.Reason);
    }

    [Fact]
    public void Modified_json_without_yes_yields_InteractionRequired_ModifiedSinceTrust()
    {
        var d = TrustGate.Decide(Req(hash: "newhash"),
                                 With("/repo/commands.json", "oldhash"),
                                 new TrustFlags(false, true));
        var i = Assert.IsType<TrustDecision.InteractionRequired>(d);
        Assert.Equal(PromptReason.ModifiedSinceTrust, i.Reason);
    }

    [Fact]
    public void Modified_json_with_yes_yields_AutoApproved_ModifiedSinceTrust()
    {
        var d = TrustGate.Decide(Req(hash: "newhash"),
                                 With("/repo/commands.json", "oldhash"),
                                 new TrustFlags(true, true));
        var a = Assert.IsType<TrustDecision.AutoApproved>(d);
        Assert.Equal(PromptReason.ModifiedSinceTrust, a.Reason);
    }
}

public sealed class TrustGateOrchestratorTests
{
    private static readonly IReadOnlyList<CommandsConfigEntry> EmptyCommands = Array.Empty<CommandsConfigEntry>();

    [Fact]
    public void AlreadyTrusted_does_not_touch_store_or_prompt()
    {
        var store = new InMemoryTrustStore();
        store.Seed("/repo/commands.json", "h1");
        var prompt = new RecordingPrompt();
        var sink = new RecordingSink();
        var orch = new TrustGateOrchestrator(store, prompt, sink);

        var ok = orch.Authorize(
            new TrustRequest("/repo/commands.json", "h1", EmptyCommands),
            new TrustFlags(false, false));

        Assert.True(ok);
        Assert.Equal(0, store.CommitCount);
        Assert.Empty(prompt.Calls);
        Assert.False(sink.RejectedCalled);
        Assert.Null(sink.InteractionRequiredReason);
    }

    [Fact]
    public void AutoApproved_writes_store_and_does_not_prompt()
    {
        var store = new InMemoryTrustStore();
        var prompt = new RecordingPrompt();
        var sink = new RecordingSink();
        var orch = new TrustGateOrchestrator(store, prompt, sink);

        var ok = orch.Authorize(
            new TrustRequest("/repo/commands.json", "h1", EmptyCommands),
            new TrustFlags(AutoYes: true, JsonMode: false));

        Assert.True(ok);
        Assert.Equal(1, store.CommitCount);
        Assert.Equal("h1", store.Snapshot.TrustedConfigs["/repo/commands.json"]);
        Assert.Empty(prompt.Calls);
    }

    [Fact]
    public void PromptRequired_accepted_writes_store()
    {
        var store = new InMemoryTrustStore();
        var prompt = new RecordingPrompt(answers: true);
        var sink = new RecordingSink();
        var orch = new TrustGateOrchestrator(store, prompt, sink);

        var ok = orch.Authorize(
            new TrustRequest("/repo/commands.json", "h1", EmptyCommands),
            new TrustFlags(false, false));

        Assert.True(ok);
        Assert.Single(prompt.Calls);
        Assert.Equal(PromptReason.NewFile, prompt.Calls[0]);
        Assert.Equal(1, store.CommitCount);
        Assert.False(sink.RejectedCalled);
    }

    [Fact]
    public void PromptRequired_declined_calls_sink_and_does_not_write_store()
    {
        var store = new InMemoryTrustStore();
        var prompt = new RecordingPrompt(answers: false);
        var sink = new RecordingSink();
        var orch = new TrustGateOrchestrator(store, prompt, sink);

        var ok = orch.Authorize(
            new TrustRequest("/repo/commands.json", "h1", EmptyCommands),
            new TrustFlags(false, false));

        Assert.False(ok);
        Assert.True(sink.RejectedCalled);
        Assert.Equal(0, store.CommitCount);
    }

    [Fact]
    public void InteractionRequired_calls_sink_with_the_right_reason_and_no_prompt_no_commit()
    {
        var store = new InMemoryTrustStore();
        store.Seed("/repo/commands.json", "old");
        var prompt = new RecordingPrompt();
        var sink = new RecordingSink();
        var orch = new TrustGateOrchestrator(store, prompt, sink);

        var ok = orch.Authorize(
            new TrustRequest("/repo/commands.json", "new", EmptyCommands),
            new TrustFlags(AutoYes: false, JsonMode: true));

        Assert.False(ok);
        Assert.Equal(PromptReason.ModifiedSinceTrust, sink.InteractionRequiredReason);
        Assert.Empty(prompt.Calls);
        Assert.Equal(0, store.CommitCount);
    }
}

public sealed class FileSystemTrustStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _trustFile;

    public FileSystemTrustStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hc-dev-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _trustFile = Path.Combine(_dir, "trust.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Read_returns_empty_snapshot_when_file_missing()
    {
        var store = new FileSystemTrustStore(_trustFile);
        var snapshot = store.Read();
        Assert.Equal(FileSystemTrustStore.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Empty(snapshot.TrustedConfigs);
    }

    [Fact]
    public void Commit_then_Read_round_trips()
    {
        var store = new FileSystemTrustStore(_trustFile);
        store.Commit("/repo/commands.json", "abc123");

        var fresh = new FileSystemTrustStore(_trustFile);
        var snapshot = fresh.Read();
        Assert.Equal("abc123", snapshot.TrustedConfigs["/repo/commands.json"]);
    }

    [Fact]
    public void Read_tolerates_corrupt_json_by_returning_empty()
    {
        File.WriteAllText(_trustFile, "{not valid json");
        var store = new FileSystemTrustStore(_trustFile);
        var snapshot = store.Read();
        Assert.Empty(snapshot.TrustedConfigs);
    }

    [Fact]
    public void Read_accepts_legacy_file_without_schema_version_field()
    {
        // The pre-refactor format had no SchemaVersion field — only TrustedConfigs.
        File.WriteAllText(_trustFile, """{"TrustedConfigs":{"/repo/commands.json":"legacyhash"}}""");
        var store = new FileSystemTrustStore(_trustFile);
        var snapshot = store.Read();
        Assert.Equal("legacyhash", snapshot.TrustedConfigs["/repo/commands.json"]);
        Assert.Equal(1, snapshot.SchemaVersion); // defaults to 1 for legacy files
    }

    [Fact]
    public void Stranded_temp_file_does_not_corrupt_Read()
    {
        // Simulate a crash mid-write: temp file exists, real file untouched.
        File.WriteAllText(_trustFile, """{"SchemaVersion":1,"TrustedConfigs":{"/a":"x"}}""");
        File.WriteAllText(_trustFile + ".tmp", "garbage from interrupted write");

        var store = new FileSystemTrustStore(_trustFile);
        var snapshot = store.Read();
        Assert.Equal("x", snapshot.TrustedConfigs["/a"]);
    }

    [Fact]
    public void Commit_preserves_existing_entries()
    {
        var store = new FileSystemTrustStore(_trustFile);
        store.Commit("/a", "ha");
        store.Commit("/b", "hb");

        var snapshot = new FileSystemTrustStore(_trustFile).Read();
        Assert.Equal("ha", snapshot.TrustedConfigs["/a"]);
        Assert.Equal("hb", snapshot.TrustedConfigs["/b"]);
    }

    [Fact]
    public void Commit_overwrites_existing_hash_for_same_path()
    {
        var store = new FileSystemTrustStore(_trustFile);
        store.Commit("/a", "ha-old");
        store.Commit("/a", "ha-new");

        var snapshot = new FileSystemTrustStore(_trustFile).Read();
        Assert.Equal("ha-new", snapshot.TrustedConfigs["/a"]);
        Assert.Single(snapshot.TrustedConfigs);
    }

    [Fact]
    public void Commit_writes_schema_version_on_disk()
    {
        var store = new FileSystemTrustStore(_trustFile);
        store.Commit("/a", "ha");
        var json = File.ReadAllText(_trustFile);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(FileSystemTrustStore.CurrentSchemaVersion,
                     doc.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public void Path_lookups_match_case_per_OS_filesystem_semantics()
    {
        // Windows paths are case-insensitive: a user who initially approves
        // `C:\Repo\commands.json` should not be re-prompted for the same file
        // surfaced as `c:\repo\commands.json` by a different code path.
        var store = new FileSystemTrustStore(_trustFile);
        store.Commit("/Repo/Commands.json", "hash-a");

        var snapshot = new FileSystemTrustStore(_trustFile).Read();
        if (OperatingSystem.IsWindows())
        {
            Assert.True(snapshot.TrustedConfigs.ContainsKey("/repo/commands.json"));
            Assert.True(snapshot.TrustedConfigs.ContainsKey("/REPO/COMMANDS.JSON"));
            Assert.Equal("hash-a", snapshot.TrustedConfigs["/repo/commands.json"]);
        }
        else
        {
            Assert.True(snapshot.TrustedConfigs.ContainsKey("/Repo/Commands.json"));
            Assert.False(snapshot.TrustedConfigs.ContainsKey("/repo/commands.json"));
        }
    }
}

public sealed class TrustGateOrchestratorHashingTests
{
    [Fact]
    public void ComputeHash_is_deterministic_and_lowercase_hex()
    {
        var bytes = "hello world\n"u8.ToArray();
        var h1 = TrustGateOrchestrator.ComputeHash(bytes);
        var h2 = TrustGateOrchestrator.ComputeHash(bytes);
        Assert.Equal(h1, h2);
        Assert.Equal(h1.ToLowerInvariant(), h1);
        Assert.Equal(64, h1.Length); // SHA-256 hex length
    }

    [Fact]
    public void ComputeHash_differs_for_different_content()
    {
        var a = TrustGateOrchestrator.ComputeHash("a"u8.ToArray());
        var b = TrustGateOrchestrator.ComputeHash("b"u8.ToArray());
        Assert.NotEqual(a, b);
    }
}

internal sealed class InMemoryTrustStore : ITrustStore
{
    private Dictionary<string, string> _entries = new();
    public int CommitCount { get; private set; }

    public TrustStoreSnapshot Snapshot =>
        new(1, new Dictionary<string, string>(_entries));

    public void Seed(string path, string hash) => _entries[path] = hash;

    public TrustStoreSnapshot Read() => Snapshot;

    public void Commit(string fullPath, string contentHash)
    {
        _entries[fullPath] = contentHash;
        CommitCount++;
    }
}

internal sealed class RecordingPrompt : ITrustPrompt
{
    private readonly Queue<bool> _answers;
    public List<PromptReason> Calls { get; } = new();

    public RecordingPrompt(params bool[] answers) => _answers = new Queue<bool>(answers);

    public bool AskToTrust(TrustRequest request, PromptReason reason)
    {
        Calls.Add(reason);
        return _answers.Count > 0 && _answers.Dequeue();
    }
}

internal sealed class RecordingSink : ITrustOutcomeSink
{
    public PromptReason? InteractionRequiredReason { get; private set; }
    public bool RejectedCalled { get; private set; }

    public void ReportInteractionRequired(PromptReason reason) => InteractionRequiredReason = reason;
    public void ReportRejected() => RejectedCalled = true;
}
