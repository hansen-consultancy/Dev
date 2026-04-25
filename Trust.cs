// commands.json trust gate, decomposed:
//   TrustGate.Decide is a pure decision function — exhaustively testable as
//     a (state × flags) decision table.
//   Three ports — ITrustStore (atomic, schema-versioned, cross-process safe),
//     ITrustPrompt (Spectre warning + command summary + ConfirmationPrompt),
//     ITrustOutcomeSink (envelope.Error + ExitCode=5) — keep adapters small
//     and replaceable.
//   TrustGateOrchestrator hashes, dispatches Decide's verdict, and commits.
//
// STRIDE pivot: T1, E1, E4 — see STRIDE.md.

using System.Security.Cryptography;
using System.Text.Json;
using Spectre.Console;

namespace Dev;

internal readonly record struct TrustRequest(
    string FullPath,
    string ContentHash,
    IReadOnlyList<CommandsConfigEntry> Commands);

internal sealed record TrustStoreSnapshot(
    int SchemaVersion,
    IReadOnlyDictionary<string, string> TrustedConfigs);

internal readonly record struct TrustFlags(bool AutoYes, bool JsonMode);

internal enum PromptReason { NewFile, ModifiedSinceTrust }

internal abstract record TrustDecision
{
    public sealed record AlreadyTrusted(TrustRequest Request) : TrustDecision;
    public sealed record PromptRequired(TrustRequest Request, PromptReason Reason) : TrustDecision;
    public sealed record AutoApproved(TrustRequest Request, PromptReason Reason) : TrustDecision;
    public sealed record InteractionRequired(TrustRequest Request, PromptReason Reason) : TrustDecision;
}

internal static class TrustGate
{
    public static TrustDecision Decide(
        TrustRequest request, TrustStoreSnapshot store, TrustFlags flags)
    {
        var known = store.TrustedConfigs.TryGetValue(request.FullPath, out var trustedHash);
        if (known && trustedHash == request.ContentHash)
            return new TrustDecision.AlreadyTrusted(request);

        var reason = known ? PromptReason.ModifiedSinceTrust : PromptReason.NewFile;

        if (flags.AutoYes)
            return new TrustDecision.AutoApproved(request, reason);

        if (flags.JsonMode)
            return new TrustDecision.InteractionRequired(request, reason);

        return new TrustDecision.PromptRequired(request, reason);
    }
}

internal interface ITrustStore
{
    TrustStoreSnapshot Read();
    void Commit(string fullPath, string contentHash);
}

internal interface ITrustPrompt
{
    bool AskToTrust(TrustRequest request, PromptReason reason);
}

internal interface ITrustOutcomeSink
{
    void ReportInteractionRequired(PromptReason reason);
    void ReportRejected();
}

internal sealed class TrustGateOrchestrator
{
    private readonly ITrustStore _store;
    private readonly ITrustPrompt _prompt;
    private readonly ITrustOutcomeSink _sink;

    public TrustGateOrchestrator(ITrustStore store, ITrustPrompt prompt, ITrustOutcomeSink sink)
    {
        _store = store;
        _prompt = prompt;
        _sink = sink;
    }

    public bool Authorize(string configFilePath, IReadOnlyList<CommandsConfigEntry> commands, TrustFlags flags)
    {
        var bytes = File.ReadAllBytes(configFilePath);
        var request = new TrustRequest(
            Path.GetFullPath(configFilePath),
            ComputeHash(bytes),
            commands);
        return Authorize(request, flags);
    }

    public bool Authorize(TrustRequest request, TrustFlags flags)
    {
        var snapshot = _store.Read();
        var decision = TrustGate.Decide(request, snapshot, flags);

        switch (decision)
        {
            case TrustDecision.AlreadyTrusted:
                return true;

            case TrustDecision.AutoApproved:
                _store.Commit(request.FullPath, request.ContentHash);
                return true;

            case TrustDecision.PromptRequired pr:
                if (_prompt.AskToTrust(request, pr.Reason))
                {
                    _store.Commit(request.FullPath, request.ContentHash);
                    return true;
                }
                _sink.ReportRejected();
                return false;

            case TrustDecision.InteractionRequired ir:
                _sink.ReportInteractionRequired(ir.Reason);
                return false;

            default:
                throw new InvalidOperationException($"Unhandled trust decision: {decision.GetType().Name}");
        }
    }

    public static string ComputeHash(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

internal sealed class FileSystemTrustStore : ITrustStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly string _trustFile;
    private readonly string _lockFile;

    public FileSystemTrustStore(string? trustFile = null)
    {
        _trustFile = trustFile ?? DefaultTrustFile();
        _lockFile = _trustFile + ".lock";
    }

    public static string DefaultTrustFile() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "hc-dev", "trust.json");

    public TrustStoreSnapshot Read()
    {
        try
        {
            if (!File.Exists(_trustFile))
                return Empty();
            var doc = JsonSerializer.Deserialize<TrustFileDoc>(File.ReadAllText(_trustFile));
            if (doc is null)
                return Empty();
            return new TrustStoreSnapshot(
                doc.SchemaVersion,
                doc.TrustedConfigs ?? new Dictionary<string, string>());
        }
        catch
        {
            return Empty();
        }
    }

    public void Commit(string fullPath, string contentHash)
    {
        var dir = Path.GetDirectoryName(_trustFile)!;
        Directory.CreateDirectory(dir);

        using var _ = AcquireLock();

        // Re-read under the lock so we don't lose entries written by another
        // process between our snapshot and our commit.
        var current = Read();
        var updated = new Dictionary<string, string>(current.TrustedConfigs)
        {
            [fullPath] = contentHash,
        };

        var doc = new TrustFileDoc
        {
            SchemaVersion = CurrentSchemaVersion,
            TrustedConfigs = updated,
        };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

        // Temp + rename for atomicity: a crash mid-write leaves the temp file
        // incomplete but the real trust.json untouched.
        var temp = _trustFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _trustFile, overwrite: true);
    }

    private FileStream AcquireLock()
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static TrustStoreSnapshot Empty() =>
        new(CurrentSchemaVersion, new Dictionary<string, string>());

    private sealed class TrustFileDoc
    {
        // Default = 1 so legacy trust.json files (which lack the field) load as
        // SchemaVersion 1 rather than 0.
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, string>? TrustedConfigs { get; set; }
    }
}

internal sealed class SpectreTrustPrompt : ITrustPrompt
{
    public bool AskToTrust(TrustRequest request, PromptReason reason)
    {
        AnsiConsole.MarkupLine(reason == PromptReason.ModifiedSinceTrust
            ? "[yellow]Warning:[/] The commands.json in this directory has been modified since you last approved it."
            : "[yellow]Warning:[/] A custom commands.json was found in this directory.");
        DisplayCommandSummary(request.Commands);
        return AnsiConsole.Prompt(new ConfirmationPrompt("Do you want to trust this configuration?")
        {
            DefaultValue = false,
        });
    }

    private static void DisplayCommandSummary(IReadOnlyList<CommandsConfigEntry> commands)
    {
        AnsiConsole.MarkupLine("\nThis configuration replaces the default commands with:");
        foreach (var cmd in commands)
        {
            var label = $"[cyan]{cmd.Name}[/]";
            if (cmd.Default) label += " (default)";

            string detail;
            if (!string.IsNullOrEmpty(cmd.BuiltIn))
                detail = $"(built-in) {cmd.BuiltIn}";
            else
                detail = (System.Runtime.InteropServices.RuntimeInformation
                              .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                          ? cmd.Windows : cmd.NonWindows) ?? string.Empty;

            AnsiConsole.MarkupLine($"  - {label}: {detail.EscapeMarkup()}");
        }
        AnsiConsole.WriteLine();
    }
}

internal sealed class EnvelopeOutcomeSink : ITrustOutcomeSink
{
    private readonly Envelope _envelope;
    public EnvelopeOutcomeSink(Envelope envelope) => _envelope = envelope;

    public void ReportInteractionRequired(PromptReason reason)
    {
        _envelope.Error = new StepError
        {
            Code = "interaction_required",
            Message = reason == PromptReason.ModifiedSinceTrust
                ? "commands.json has been modified since it was trusted; re-run with --yes to re-approve."
                : "Untrusted commands.json; re-run with --yes to approve.",
        };
        _envelope.ExitCode = 5;
    }

    public void ReportRejected()
    {
        _envelope.Error = new StepError
        {
            Code = "trust_rejected",
            Message = "User rejected commands.json trust.",
        };
        _envelope.ExitCode = 5;
    }
}

internal static class TrustGateFactory
{
    public static TrustGateOrchestrator ForProduction(Envelope envelope) =>
        new(new FileSystemTrustStore(),
            new SpectreTrustPrompt(),
            new EnvelopeOutcomeSink(envelope));
}
