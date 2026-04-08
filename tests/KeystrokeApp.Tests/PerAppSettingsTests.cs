using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class PerAppSettingsTests
{
    [Fact]
    public void NormalizeProcessName_StripsExeAndLowercases()
    {
        var normalized = PerAppSettings.NormalizeProcessName("Discord.EXE");

        Assert.Equal("discord", normalized);
    }

    [Fact]
    public void ParseProcessList_DeduplicatesAcrossLinesAndCommas()
    {
        var parsed = PerAppSettings.ParseProcessList("discord.exe\r\nslack, Discord");

        Assert.Equal(["discord", "slack"], parsed);
    }

    [Fact]
    public void IsEnabled_AllowsEverywhereExceptBlocked_InDefaultMode()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowAllExceptBlocked,
            BlockedProcesses = ["discord"]
        };

        Assert.False(PerAppSettings.IsEnabled(config, "Discord"));
        Assert.True(PerAppSettings.IsEnabled(config, "slack"));
    }

    [Fact]
    public void IsEnabled_OnlyAllowsListedApps_InAllowListMode()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowListedOnly,
            AllowedProcesses = ["slack", "discord"]
        };

        Assert.True(PerAppSettings.IsEnabled(config, "discord"));
        Assert.False(PerAppSettings.IsEnabled(config, "notepad"));
    }

    [Fact]
    public void IsEnabled_BlockedEntriesWinOverAllowedEntries()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowListedOnly,
            AllowedProcesses = ["discord"],
            BlockedProcesses = ["discord"]
        };

        Assert.False(PerAppSettings.IsEnabled(config, "discord"));
    }
}
