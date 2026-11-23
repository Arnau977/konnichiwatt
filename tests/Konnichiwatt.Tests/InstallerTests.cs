using static Konnichiwatt.Core.Installer;

namespace Konnichiwatt.Tests;

public class InstallerTests
{
    private static readonly Version V030 = new(0, 3, 0), V031 = new(0, 3, 1);

    [Fact]
    public void Downloaded_copy_with_nothing_installed_offers_install() =>
        Assert.Equal(LaunchAction.OfferInstall, Decide(true, false, false, null, V031, false));

    [Fact]
    public void Newer_download_offers_to_update_the_installed_copy() =>
        Assert.Equal(LaunchAction.OfferUpdate, Decide(true, false, false, V030, V031, false));

    [Theory]
    [InlineData("0.3.1")]
    [InlineData("0.4.0")]
    public void Same_or_older_download_just_opens_the_installed_copy(string installed) =>
        Assert.Equal(LaunchAction.OpenInstalled, Decide(true, false, false, Version.Parse(installed), V031, false));

    [Fact]
    public void Installed_copy_runs_normally() =>
        Assert.Equal(LaunchAction.Run, Decide(true, false, true, V031, V031, true));

    [Fact]
    public void Portable_copy_never_offers_to_install() =>
        Assert.Equal(LaunchAction.Run, Decide(true, true, false, null, V031, false));

    [Fact]
    public void Copy_set_up_by_hand_before_the_installer_keeps_working() =>
        Assert.Equal(LaunchAction.Run, Decide(true, false, false, null, V031, startsWithWindows: true));

    [Fact]
    public void Development_build_runs_normally() =>
        Assert.Equal(LaunchAction.Run, Decide(false, false, false, null, V031, false));

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Konnichiwatt\Konnichiwatt.exe", @"c:\users\A\appdata\local\programs\konnichiwatt\KONNICHIWATT.EXE", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Konnichiwatt\..\Konnichiwatt\Konnichiwatt.exe", @"C:\Users\a\AppData\Local\Programs\Konnichiwatt\Konnichiwatt.exe", true)]
    [InlineData(@"C:\Users\a\Downloads\Konnichiwatt.exe", @"C:\Users\a\AppData\Local\Programs\Konnichiwatt\Konnichiwatt.exe", false)]
    [InlineData(null, @"C:\x.exe", false)]
    public void Paths_compare_case_insensitively_and_normalized(string? a, string b, bool same) =>
        Assert.Equal(same, SamePath(a, b));
}
