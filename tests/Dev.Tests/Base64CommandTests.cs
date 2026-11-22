using System.Text;
using Xunit;

namespace Dev.Tests;

public sealed class Base64CommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dev-b64-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeClipboard : ITransientClipboard
    {
        public string? Text { get; private set; }
        public TimeSpan Lifetime { get; private set; }
        public void Copy(string text, TimeSpan lifetime) => (Text, Lifetime) = (text, lifetime);
    }

    private sealed record Outcome(StepResult Step, byte[] Stdout, string Stderr)
    {
        public string StdoutText => Encoding.UTF8.GetString(Stdout);
    }

    private static Outcome Run(string[] args, byte[]? stdin = null, bool stdoutRedirected = true,
        ITransientClipboard? clipboard = null, bool jsonMode = false)
    {
        var stdout = new MemoryStream();
        var stderr = new StringWriter();
        var io = new Base64Io
        {
            Stdin = new MemoryStream(stdin ?? []),
            StdinRedirected = stdin is not null,
            Stdout = stdout,
            StdoutRedirected = stdoutRedirected,
            Stderr = stderr,
            Clipboard = clipboard,
        };
        var step = Base64Command.Run(args, new StepResult { Command = "b64", Args = args }, io, jsonMode);
        return new Outcome(step, stdout.ToArray(), stderr.ToString());
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // A minimal PKCS#12 header: SEQUENCE (long-form length) { INTEGER 3, ... }.
    private static readonly byte[] PfxBytes = [0x30, 0x82, 0x01, 0x00, 0x02, 0x01, 0x03, 0x30, 0x80, 0xFF, 0xFE];

    [Fact]
    public void Encodes_text_argument()
    {
        var r = Run(["hello"]);
        Assert.Equal("ok", r.Step.Status);
        Assert.Equal("aGVsbG8=\n", r.StdoutText);
        Assert.Equal("", r.Stderr);
    }

    [Fact]
    public void Text_round_trips_including_non_ascii()
    {
        const string text = "héllo wörld €";
        var encoded = Run([text]).StdoutText.TrimEnd('\n');
        var decoded = Run(["-d", encoded]);
        Assert.Equal(text, decoded.StdoutText);
    }

    [Fact]
    public void Binary_file_round_trips_through_output_file()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var input = WriteFile("data.bin", bytes);

        var encoded = Run([input]).StdoutText.TrimEnd('\n');
        Assert.Equal(Convert.ToBase64String(bytes), encoded);

        var output = Path.Combine(_dir, "out.bin");
        var decoded = Run(["-d", encoded, "-o", output]);
        Assert.Equal("ok", decoded.Step.Status);
        Assert.Equal(bytes, File.ReadAllBytes(output));
        Assert.Empty(decoded.Stdout);
    }

    [Fact]
    public void Decode_reads_base64_from_a_file_ignoring_line_breaks()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("line-wrapped content"));
        var input = WriteFile("in.b64", Encoding.ASCII.GetBytes(b64[..10] + "\r\n" + b64[10..] + "\r\n"));
        Assert.Equal("line-wrapped content", Run(["-d", input]).StdoutText);
    }

    [Fact]
    public void Encodes_stdin_dropping_one_trailing_newline_from_text()
    {
        Assert.Equal("aGVsbG8=\n", Run([], stdin: "hello\r\n"u8.ToArray()).StdoutText);
        Assert.Equal("aGVsbG8=\n", Run([], stdin: "hello\n"u8.ToArray()).StdoutText);
        Assert.Equal("stdin", ((Dictionary<string, object?>)Run([], stdin: "x"u8.ToArray()).Step.Data!)["source"]);
    }

    [Fact]
    public void Binary_stdin_is_encoded_byte_exact()
    {
        byte[] bytes = [0xFF, 0x00, 0x0A];
        Assert.Equal(Convert.ToBase64String(bytes) + "\n", Run([], stdin: bytes).StdoutText);
    }

    [Fact]
    public void Decodes_stdin()
    {
        Assert.Equal("hello", Run(["-d"], stdin: "aGVsbG8=\r\n"u8.ToArray()).StdoutText);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("aGVsbG8")]
    public void Invalid_base64_fails_with_exit_1(string input)
    {
        var r = Run(["-d", input]);
        Assert.Equal("failed", r.Step.Status);
        Assert.Equal(1, r.Step.ExitCode);
        Assert.Equal("invalid_base64", r.Step.Error!.Code);
        Assert.Empty(r.Stdout);
        Assert.Contains("not valid base64", r.Stderr);
        Assert.DoesNotContain(input, r.Stderr);
    }

    [Fact]
    public void Binary_decode_to_terminal_is_refused()
    {
        var r = Run(["-d", Convert.ToBase64String(PfxBytes)], stdoutRedirected: false);
        Assert.Equal("binary_output", r.Step.Error!.Code);
        Assert.Empty(r.Stdout);
    }

    [Fact]
    public void Binary_decode_to_redirected_stdout_is_byte_exact()
    {
        var r = Run(["-d", Convert.ToBase64String(PfxBytes)], stdoutRedirected: true);
        Assert.Equal(PfxBytes, r.Stdout);
    }

    [Fact]
    public void No_input_on_a_terminal_fails_with_exit_2()
    {
        var r = Run([]);
        Assert.Equal(2, r.Step.ExitCode);
        Assert.Equal("no_input", r.Step.Error!.Code);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("a", "b")]
    [InlineData("-o")]
    [InlineData("-c", "-o", "f", "x")]
    public void Usage_errors_fail_with_exit_2(params string[] args)
    {
        var r = Run(args);
        Assert.Equal(2, r.Step.ExitCode);
        Assert.Equal("invalid_arguments", r.Step.Error!.Code);
    }

    [Fact]
    public void Double_dash_lets_text_start_with_a_dash()
    {
        Assert.Equal(Convert.ToBase64String("-d"u8) + "\n", Run(["--", "-d"]).StdoutText);
    }

    [Fact]
    public void Envelope_args_never_carry_the_value()
    {
        Assert.Empty(Run(["secret-value"]).Step.Args);
    }

    [Fact]
    public void Json_mode_puts_output_in_data_and_writes_nothing_else()
    {
        var r = Run(["hello"], jsonMode: true);
        var data = (Dictionary<string, object?>)r.Step.Data!;
        Assert.Equal("aGVsbG8=", data["output"]);
        Assert.Empty(r.Stdout);
        Assert.Equal("", r.Stderr);
    }

    [Fact]
    public void Clip_copies_instead_of_printing()
    {
        var clipboard = new FakeClipboard();
        var r = Run(["-c", "hello"], clipboard: clipboard);
        Assert.Equal("aGVsbG8=", clipboard.Text);
        Assert.Equal(TimeSpan.FromSeconds(30), clipboard.Lifetime);
        Assert.Empty(r.Stdout);
        Assert.DoesNotContain("aGVsbG8=", r.Stderr);
        Assert.Empty(r.Step.Warnings);
    }

    [Fact]
    public void Clip_of_a_pfx_warns_on_stderr_without_echoing_the_value()
    {
        var clipboard = new FakeClipboard();
        var pfx = WriteFile("cert.pfx", PfxBytes);
        var r = Run([pfx, "--clip"], clipboard: clipboard);

        Assert.Equal(Convert.ToBase64String(PfxBytes), clipboard.Text);
        Assert.Contains("private key", r.Stderr);
        Assert.DoesNotContain(clipboard.Text!, r.Stderr);
        Assert.Single(r.Step.Warnings);
    }

    [Fact]
    public void Clip_without_clipboard_support_fails_with_exit_6()
    {
        var r = Run(["-c", "hello"], clipboard: null);
        Assert.Equal(6, r.Step.ExitCode);
        Assert.Equal("clipboard_unsupported", r.Step.Error!.Code);
        Assert.Empty(r.Stdout);
    }

    [Theory]
    [InlineData("cert.p12", new byte[] { 1, 2, 3 }, true)]
    [InlineData("server.KEY", new byte[] { 1, 2, 3 }, true)]
    [InlineData(null, new byte[] { 0x30, 0x82, 0x01, 0x00, 0x02, 0x01, 0x03 }, true)]
    [InlineData(null, new byte[] { 0x30, 0x05, 0x02, 0x01, 0x03 }, true)]
    [InlineData(null, new byte[] { 0x30, 0x82, 0x01, 0x00, 0x02, 0x01, 0x01 }, false)]
    [InlineData("notes.txt", new byte[] { 0x68, 0x69 }, false)]
    public void Detects_key_material(string? path, byte[] bytes, bool expected)
    {
        Assert.Equal(expected, Base64Command.LooksLikeKeyMaterial(path, bytes));
    }

    [Fact]
    public void Detects_pem_private_key()
    {
        Assert.True(Base64Command.LooksLikeKeyMaterial(null, "-----BEGIN RSA PRIVATE KEY-----\nMII..."u8));
        Assert.False(Base64Command.LooksLikeKeyMaterial(null, "-----BEGIN CERTIFICATE-----\nMII..."u8));
    }
}
