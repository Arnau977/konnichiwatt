namespace Konnichiwatt.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("0.2.1", "0.2.0", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.1.9", "0.2.0", false)]
    [InlineData("not-a-version", "0.2.0", false)]
    public void Only_strictly_newer_versions_are_offered(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Updater.IsNewer(candidate, Version.Parse(current)));

    [Fact]
    public void Four_part_assembly_version_compares_like_three_parts() =>
        Assert.False(Updater.IsNewer("0.2.0", new Version(0, 2, 0, 0)));

    [Theory]
    [InlineData("https://github.com/x/y/releases/latest/download/Konnichiwatt.exe", true)]
    [InlineData("http://example.com/Konnichiwatt.exe", false)]   // no plain HTTP
    [InlineData("file:///C:/Konnichiwatt.exe", false)]
    [InlineData("Konnichiwatt.exe", false)]
    public void Only_https_downloads_are_accepted(string url, bool ok) =>
        Assert.Equal(ok, Updater.IsSafeUrl(url));

    [Fact]
    public void Manifest_json_round_trips()
    {
        var json = """{"version":"0.3.0","url":"https://h/K.exe","sha256":"ABC","notes":"Nuevo"}""";
        var m = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(json)!;
        Assert.Equal("0.3.0", m.Version);
        Assert.Equal("https://h/K.exe", m.Url);
        Assert.Equal("ABC", m.Sha256);
    }
}
