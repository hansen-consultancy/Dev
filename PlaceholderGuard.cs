// Guards the one place where untrusted input crosses into the trusted custom-command
// shell string: the {sln}/{project}/{dir} placeholder substitution.
//
// The command body in commands.json is hash-gated (TrustGate) and is legitimately a
// shell string — pipes, &&, and redirects in the *body* are intentional and stay.
// The placeholders, however, resolve to filesystem paths the tool discovered, not to
// trusted input: a checked-out directory or a project file named "x & calc.exe" would
// otherwise be spliced into cmd.exe / bash and execute (SECURITY_REVIEW F2, STRIDE E2/T1).
//
// We refuse rather than escape. cmd.exe quoting is not reliably composable, and escaping
// a value collides with author-supplied quotes around a placeholder ("{project}"). So a
// resolved value carrying a shell metacharacter is rejected before execution, with the
// offending placeholder and character reported. Kept pure (no I/O) and exhaustively
// testable, mirroring CommandCatalog / TrustGate.Decide.
//
// '(' and ')' are deliberately NOT treated as unsafe so common paths such as
// "Program Files (x86)" keep working — without one of the blocked separators a
// parenthesis cannot start a new command.

namespace Dev;

internal readonly record struct UnsafePlaceholder(string Placeholder, char Character);

internal static class PlaceholderGuard
{
    // Characters that let a substituted value break out of its argument to chain,
    // substitute, or redirect a command on cmd.exe or bash, or break out of quoting.
    private static readonly char[] UnsafeChars =
        { '&', '|', ';', '<', '>', '`', '$', '"', '\'', '%', '\n', '\r', '\0' };

    // Returns the first placeholder whose resolved value carries an unsafe character,
    // or null when every placeholder used by the command resolves to a safe value.
    // A value is only inspected when its token actually appears in the command, so a
    // command that never references {dir} is not refused for an odd working directory.
    public static UnsafePlaceholder? FindUnsafe(string command, string? sln, string? project, string dir)
    {
        ReadOnlySpan<(string Token, string? Value)> placeholders =
        [
            ("{sln}", sln),
            ("{project}", project),
            ("{dir}", dir),
        ];

        foreach (var (token, value) in placeholders)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!command.Contains(token, StringComparison.Ordinal)) continue;

            var idx = value.IndexOfAny(UnsafeChars);
            if (idx >= 0) return new UnsafePlaceholder(token, value[idx]);
        }

        return null;
    }
}
