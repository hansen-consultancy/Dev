// `dev b64`: base64 encode/decode without a GUI tool. One input rule for both
// directions — an argument naming an existing file is read as bytes, any other
// argument is the value itself, no argument reads stdin — so a .pfx is just a file.
// The result goes to stdout, a file (-o), or the clipboard (-c). The value is never
// written anywhere else: stderr notices and the --json envelope's args/errors carry
// sizes and paths only.

using System.Text;

namespace Dev;

// The process's standard streams plus the clipboard, injectable for tests.
internal sealed class Base64Io
{
    public required Stream Stdin { get; init; }
    public required bool StdinRedirected { get; init; }
    public required Stream Stdout { get; init; }
    public required bool StdoutRedirected { get; init; }
    public required TextWriter Stderr { get; init; }

    // Null where no clipboard is supported (non-Windows).
    public ITransientClipboard? Clipboard { get; init; }

    public static Base64Io FromConsole() => new()
    {
        Stdin = Console.OpenStandardInput(),
        StdinRedirected = Console.IsInputRedirected,
        Stdout = Console.OpenStandardOutput(),
        StdoutRedirected = Console.IsOutputRedirected,
        Stderr = Console.Error,
        Clipboard = OperatingSystem.IsWindows() ? new WindowsClipboard() : null,
    };
}

// A clipboard for values that must not outlive their use (keys, certificates).
internal interface ITransientClipboard
{
    // Puts text on the clipboard, excluded from clipboard history and cloud sync, and
    // clears it after `lifetime` unless something else has been copied since.
    // Throws InvalidOperationException when the clipboard cannot be written.
    void Copy(string text, TimeSpan lifetime);
}

internal static class Base64Command
{
    internal static readonly TimeSpan ClipboardLifetime = TimeSpan.FromSeconds(30);

    internal const string Usage = "Usage: dev b64 [-d] [-o <file> | -c] [<file>|<text>]  (no input: reads stdin)";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] KeyFileExtensions = [".pfx", ".p12", ".key"];

    private sealed record Request(bool Decode, string? Input, string? OutputFile, bool Clip);

    public static StepResult Run(string[] args, StepResult step, Base64Io io, bool jsonMode)
    {
        // The input may be a secret typed on the command line; keep it out of the envelope.
        step.Args = [];

        var usageError = Parse(args, out var request);
        if (usageError is not null)
            return Fail(step, io, jsonMode, 2, "invalid_arguments", $"{usageError} {Usage}");

        byte[] input;
        string source;
        string? inputFile = null;
        if (request.Input is { } arg)
        {
            if (File.Exists(arg))
            {
                inputFile = Path.GetFullPath(arg);
                input = File.ReadAllBytes(arg);
                source = "file";
            }
            else
            {
                input = Encoding.UTF8.GetBytes(arg);
                source = "argument";
            }
        }
        else if (io.StdinRedirected)
        {
            input = DropTrailingNewlineFromText(ReadAll(io.Stdin));
            source = "stdin";
        }
        else
        {
            return Fail(step, io, jsonMode, 2, "no_input", $"No input: pass a file or text, or pipe data in. {Usage}");
        }

        byte[] plain, output;
        if (request.Decode)
        {
            var encoded = Encoding.UTF8.GetString(input).Trim().TrimStart('﻿');
            try
            {
                plain = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return Fail(step, io, jsonMode, 1, "invalid_base64", "Input is not valid base64.");
            }
            output = plain;
        }
        else
        {
            plain = input;
            output = Encoding.ASCII.GetBytes(Convert.ToBase64String(input));
        }

        var data = new Dictionary<string, object?>
        {
            ["mode"] = request.Decode ? "decode" : "encode",
            ["source"] = source,
            ["inputBytes"] = input.Length,
            ["outputBytes"] = output.Length,
        };
        if (inputFile is not null) data["inputFile"] = inputFile;
        step.Data = data;

        if (request.OutputFile is { } outputFile)
        {
            File.WriteAllBytes(outputFile, output);
            data["destination"] = "file";
            data["outputFile"] = Path.GetFullPath(outputFile);
            Notice(io, jsonMode, $"Wrote {output.Length} bytes to {outputFile}.");
            return step;
        }

        var text = TryGetText(output);
        if (text is null && (request.Clip || jsonMode || !io.StdoutRedirected))
            return Fail(step, io, jsonMode, 1, "binary_output", "Decoded data is binary; write it to a file with -o <file>.");

        if (request.Clip)
        {
            if (io.Clipboard is null)
                return Fail(step, io, jsonMode, 6, "clipboard_unsupported", "The clipboard (-c) is only supported on Windows.");

            if (LooksLikeKeyMaterial(inputFile, plain))
            {
                const string warning = "This looks like a certificate or private key. It is kept out of Windows clipboard history (Win+V) and cloud clipboard, but any app or clipboard manager can read it until it is cleared.";
                step.Warnings.Add(warning);
                if (!jsonMode) io.Stderr.WriteLine($"Warning: {warning}");
            }

            try
            {
                io.Clipboard.Copy(text!, ClipboardLifetime);
            }
            catch (InvalidOperationException ex)
            {
                return Fail(step, io, jsonMode, 1, "clipboard_failed", ex.Message);
            }
            data["destination"] = "clipboard";
            Notice(io, jsonMode, $"Copied to the clipboard; it is cleared in {ClipboardLifetime.TotalSeconds:0} s.");
            return step;
        }

        data["destination"] = "stdout";
        if (jsonMode)
        {
            data["output"] = text;
            return step;
        }

        io.Stdout.Write(output);
        // Encoded output is a line of text; decoded output is written byte-exact when
        // redirected, and only gets a newline on a terminal so the prompt stays clean.
        if (!request.Decode || !io.StdoutRedirected) io.Stdout.Write("\n"u8);
        io.Stdout.Flush();
        return step;
    }

    // Returns a usage error, or null with the parsed request. `--` ends options so
    // text starting with '-' can still be encoded.
    private static string? Parse(string[] args, out Request request)
    {
        request = new Request(false, null, null, false);
        bool decode = false, clip = false, optionsDone = false;
        string? input = null, outputFile = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!optionsDone && a.Length > 1 && a[0] == '-')
            {
                switch (a)
                {
                    case "--": optionsDone = true; continue;
                    case "-d" or "--decode": decode = true; continue;
                    case "-c" or "--clip": clip = true; continue;
                    case "-o" or "--output":
                        if (i + 1 >= args.Length) return "Option -o needs a file path.";
                        outputFile = args[++i];
                        continue;
                    default: return $"Unknown option '{a}'.";
                }
            }

            if (input is not null) return "Expected a single input; quote text that contains spaces.";
            input = a;
        }

        if (clip && outputFile is not null) return "Use either -o or -c, not both.";

        request = new Request(decode, input, outputFile, clip);
        return null;
    }

    // Shells add a trailing newline when piping text (PowerShell, echo); drop one so
    // `"abc" | dev b64` encodes "abc". Binary input (not valid UTF-8) is left exact.
    private static byte[] DropTrailingNewlineFromText(byte[] data)
    {
        if (TryGetText(data) is null) return data;
        if (data.AsSpan().EndsWith("\r\n"u8)) return data[..^2];
        if (data.AsSpan().EndsWith("\n"u8)) return data[..^1];
        return data;
    }

    private static string? TryGetText(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    // Heuristic, warning-only: a key/certificate file extension, a PKCS#12 (PFX)
    // DER header, or a PEM private key block.
    internal static bool LooksLikeKeyMaterial(string? path, ReadOnlySpan<byte> plain)
    {
        if (path is not null && KeyFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return true;
        return IsPkcs12(plain) || plain.IndexOf("PRIVATE KEY-----"u8) >= 0;
    }

    // PFX = DER SEQUENCE { version INTEGER 3, ... }: 30 <length> 02 01 03.
    private static bool IsPkcs12(ReadOnlySpan<byte> b)
    {
        if (b.Length < 5 || b[0] != 0x30) return false;
        var lengthBytes = b[1] < 0x80 ? 0 : b[1] & 0x7F;
        if (lengthBytes > 4) return false;
        var i = 2 + lengthBytes;
        return b.Length >= i + 3 && b[i] == 0x02 && b[i + 1] == 0x01 && b[i + 2] == 0x03;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Notice(Base64Io io, bool jsonMode, string message)
    {
        if (!jsonMode) io.Stderr.WriteLine(message);
    }

    private static StepResult Fail(StepResult step, Base64Io io, bool jsonMode, int exitCode, string code, string message)
    {
        if (!jsonMode) io.Stderr.WriteLine($"dev b64: {message}");
        step.Status = "failed";
        step.ExitCode = exitCode;
        step.Error = new StepError { Code = code, Message = message };
        return step;
    }
}
