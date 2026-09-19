using Xunit;

namespace Dev.Tests;

// Boundary tests for Vidyano app discovery: the marker file, the zero/one/many
// partition, and the explicit-name override that resolves ambiguity.
public sealed class VidyanoAppLocatorTests
{
    private const string App = "/repo/App/App.csproj";
    private const string Lib = "/repo/App.Library/App.Library.csproj";

    private static FakeFileSystem WithApp(FakeFileSystem fs, string projectDir) =>
        fs.WithFile($"{projectDir}/App_Data/model.json", "{}");

    [Fact]
    public void Single_project_with_model_file_resolves()
    {
        var fs = WithApp(new FakeFileSystem(), "/repo/App");
        var result = VidyanoAppLocator.Resolve([App, Lib], null, fs);

        Assert.Equal(App, result.App!.Value.ProjectPath);
        Assert.Equal("App", result.App!.Value.Name);
        Assert.Null(result.Code);
    }

    [Fact]
    public void Model_file_under_wwwroot_also_marks_an_app()
    {
        // The Vidyano.Service samples keep App_Data inside wwwroot.
        var fs = new FakeFileSystem().WithFile("/repo/App/wwwroot/App_Data/model.json", "{}");
        var result = VidyanoAppLocator.Resolve([App], null, fs);

        Assert.Equal(App, result.App!.Value.ProjectPath);
    }

    [Fact]
    public void No_project_with_a_model_file_fails_as_no_vidyano_project()
    {
        var result = VidyanoAppLocator.Resolve([App, Lib], null, new FakeFileSystem());

        Assert.Null(result.App);
        Assert.Equal("no_vidyano_project", result.Code);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Empty_workspace_fails_as_no_vidyano_project()
    {
        var result = VidyanoAppLocator.Resolve([], null, new FakeFileSystem());

        Assert.Null(result.App);
        Assert.Equal("no_vidyano_project", result.Code);
    }

    [Fact]
    public void Two_apps_without_a_name_are_ambiguous_and_list_both()
    {
        var fs = WithApp(WithApp(new FakeFileSystem(), "/repo/App"), "/repo/Admin");
        var result = VidyanoAppLocator.Resolve([App, "/repo/Admin/Admin.csproj"], null, fs);

        Assert.Null(result.App);
        Assert.Equal("ambiguous_vidyano_project", result.Code);
        Assert.Equal(new[] { "App", "Admin" }, result.Candidates);
        Assert.Contains("App, Admin", result.Message);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    public void An_explicit_name_picks_one_of_several_apps_case_insensitively(string requested)
    {
        var fs = WithApp(WithApp(new FakeFileSystem(), "/repo/App"), "/repo/Admin");
        var result = VidyanoAppLocator.Resolve([App, "/repo/Admin/Admin.csproj"], requested, fs);

        Assert.Equal("/repo/Admin/Admin.csproj", result.App!.Value.ProjectPath);
    }

    [Fact]
    public void An_unmatched_name_fails_and_lists_the_real_candidates()
    {
        var fs = WithApp(new FakeFileSystem(), "/repo/App");
        var result = VidyanoAppLocator.Resolve([App, Lib], "App.Library", fs);

        Assert.Null(result.App);
        Assert.Equal("no_vidyano_project", result.Code);
        // The library is a project but not an app, so it is never a candidate.
        Assert.Equal(new[] { "App" }, result.Candidates);
    }
}
