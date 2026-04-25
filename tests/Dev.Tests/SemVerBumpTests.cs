using Xunit;

namespace Dev.Tests;

public sealed class SemVerBumpTests
{
    [Theory]
    [InlineData("1.2.3", "2.0.0")]
    [InlineData("1.2.3.4", "2.0.0.0")]
    [InlineData("1.0", "2.0.0")]
    [InlineData("1", "2.0.0")]
    [InlineData("1.2.3-alpha", "2.0.0-alpha")]
    [InlineData("1.2.3+sha", "2.0.0+sha")]
    public void Bump_major_resets_minor_and_build_to_zero(string from, string expected)
    {
        var result = new SemVer(from).Bump(BumpPart.Major);
        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.2.3.4", "1.3.0.0")]
    [InlineData("1.2.3-alpha+sha", "1.3.0-alpha+sha")]
    public void Bump_minor_resets_build_to_zero_and_preserves_suffix_and_buildvars(string from, string expected)
    {
        var result = new SemVer(from).Bump(BumpPart.Minor);
        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3.4", "1.2.4.0")]
    public void Bump_patch_increments_build_and_resets_fix_to_zero_when_present(string from, string expected)
    {
        var result = new SemVer(from).Bump(BumpPart.Patch);
        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.5")]
    [InlineData("1.2.3.0", "1.2.3.1")]
    public void Bump_revision_increments_fix(string from, string expected)
    {
        var result = new SemVer(from).Bump(BumpPart.Revision);
        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public void Bump_revision_on_three_part_version_is_a_noop()
    {
        // Preserves the existing null-propagating add semantics: bumping revision
        // when no fix component exists leaves the version unchanged.
        var result = new SemVer("1.2.3").Bump(BumpPart.Revision);
        Assert.Equal("1.2.3", result.ToString());
    }

    [Fact]
    public void Bump_major_preserves_null_fix_but_resets_non_null_fix_to_zero()
    {
        // "fix-null-vs-zero asymmetry" — the rule the issue calls out by name.
        Assert.Equal("2.0.0", new SemVer("1.2.3").Bump(BumpPart.Major).ToString());
        Assert.Equal("2.0.0.0", new SemVer("1.2.3.4").Bump(BumpPart.Major).ToString());
    }

    [Fact]
    public void Bump_with_pre_release_suffix_overrides_existing_suffix()
    {
        var result = new SemVer("1.2.3-rc").Bump(new BumpSpec(BumpPart.Minor, PreReleaseSuffix: "alpha"));
        Assert.Equal("1.3.0-alpha", result.ToString());
    }

    [Fact]
    public void Bump_with_build_metadata_overrides_existing_buildvars()
    {
        var result = new SemVer("1.2.3+old").Bump(new BumpSpec(BumpPart.Patch, BuildMetadata: "new"));
        Assert.Equal("1.2.4+new", result.ToString());
    }

    [Fact]
    public void Bump_without_overrides_preserves_suffix_and_buildvars()
    {
        var result = new SemVer("1.2.3-alpha+sha").Bump(BumpPart.Patch);
        Assert.Equal("1.2.4-alpha+sha", result.ToString());
    }

    [Theory]
    [InlineData("major", BumpPart.Major)]
    [InlineData("minor", BumpPart.Minor)]
    [InlineData("patch", BumpPart.Patch)]
    [InlineData("revision", BumpPart.Revision)]
    [InlineData("anything-else", BumpPart.Revision)]
    [InlineData("", BumpPart.Revision)]
    [InlineData(null, BumpPart.Revision)]
    public void ParsePart_maps_known_strings_and_falls_through_to_revision(string? raw, BumpPart expected)
    {
        Assert.Equal(expected, SemVer.ParsePart(raw));
    }
}
