using Xunit;

namespace Dev.Tests;

public sealed class PlaceholderGuardTests
{
    [Fact]
    public void Clean_paths_are_safe()
    {
        Assert.Null(PlaceholderGuard.FindUnsafe(
            "dotnet build {project}", @"C:\repo\app.sln", @"C:\repo\app.csproj", @"C:\repo"));
    }

    [Fact]
    public void Spaces_and_backslashes_are_safe()
    {
        // Spaces are common in real paths and do not enable command chaining; the
        // path separator must not be treated as unsafe either.
        Assert.Null(PlaceholderGuard.FindUnsafe(
            "dotnet build {project}", null, @"C:\Users\John Doe\my app.csproj", @"C:\Users\John Doe"));
    }

    [Fact]
    public void Program_files_x86_parentheses_are_allowed()
    {
        // The deliberate carve-out: parentheses cannot start a command without one
        // of the blocked separators, so this very common path keeps working.
        Assert.Null(PlaceholderGuard.FindUnsafe(
            "build {dir}", null, null, @"C:\Program Files (x86)\app"));
    }

    [Theory]
    [InlineData('&')]
    [InlineData('|')]
    [InlineData(';')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('`')]
    [InlineData('$')]
    [InlineData('"')]
    [InlineData('\'')]
    [InlineData('%')]
    [InlineData('\n')]
    [InlineData('\r')]
    [InlineData('\0')]
    public void Metacharacter_in_used_placeholder_is_refused(char meta)
    {
        var dir = $"C:\\repo{meta}evil";
        var hit = PlaceholderGuard.FindUnsafe("build {dir}", null, null, dir);
        Assert.NotNull(hit);
        Assert.Equal("{dir}", hit.Value.Placeholder);
        Assert.Equal(meta, hit.Value.Character);
    }

    [Fact]
    public void Classic_amp_injection_is_refused()
    {
        var hit = PlaceholderGuard.FindUnsafe(
            "dotnet build {project}", null, @"C:\repo\app & calc.exe.csproj", @"C:\repo");
        Assert.NotNull(hit);
        Assert.Equal("{project}", hit.Value.Placeholder);
        Assert.Equal('&', hit.Value.Character);
    }

    [Fact]
    public void Unsafe_value_for_unreferenced_placeholder_is_ignored()
    {
        // {dir} carries '&', but the command never uses {dir} — nothing is spliced,
        // so there is nothing to refuse.
        Assert.Null(PlaceholderGuard.FindUnsafe(
            "dotnet build {project}", null, @"C:\repo\app.csproj", @"C:\repo & evil"));
    }

    [Fact]
    public void Null_placeholder_value_is_safe_even_when_referenced()
    {
        // No solution discovered: {sln} substitutes to empty, never to an injection.
        Assert.Null(PlaceholderGuard.FindUnsafe(
            "build {sln}", null, @"C:\repo\app.csproj", @"C:\repo"));
    }

    [Fact]
    public void First_unsafe_placeholder_in_token_order_is_reported()
    {
        // Both {sln} and {dir} are unsafe; {sln} is inspected first.
        var hit = PlaceholderGuard.FindUnsafe(
            "x {sln} {dir}", @"C:\a;rm.sln", null, @"C:\b & evil");
        Assert.NotNull(hit);
        Assert.Equal("{sln}", hit.Value.Placeholder);
        Assert.Equal(';', hit.Value.Character);
    }
}
