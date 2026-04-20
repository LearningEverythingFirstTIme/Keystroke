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

    [Fact]
    public void ApplyPreset_ConfiguresChatAndEmailAllowList()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowAllExceptBlocked,
            BlockedProcesses = ["game"]
        };

        PerAppSettings.ApplyPreset(config, PerAppSettings.PresetChatAndEmailOnly);

        Assert.Equal(PerAppSettings.AllowListedOnly, config.AppFilteringMode);
        Assert.Contains("discord", config.AllowedProcesses);
        Assert.Contains("outlook", config.AllowedProcesses);
        Assert.Empty(config.BlockedProcesses);
    }

    [Fact]
    public void ApplyPreset_ManualAllowListClearsAllowedApps()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowListedOnly,
            AllowedProcesses = ["discord", "slack"]
        };

        PerAppSettings.ApplyPreset(config, PerAppSettings.PresetManualAllowList);

        Assert.Equal(PerAppSettings.AllowListedOnly, config.AppFilteringMode);
        Assert.Empty(config.AllowedProcesses);
    }

    [Fact]
    public void IsEnabled_UnknownProcessStaysAllowedWhenNoGuardsConfigured()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowAllExceptBlocked,
            BlockedProcesses = []
        };

        Assert.True(PerAppSettings.IsEnabled(config, ""));
        Assert.True(PerAppSettings.IsEnabled(config, null));
    }

    [Fact]
    public void IsEnabled_UnknownProcessIsBlockedWhenAllowListModeActive()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowListedOnly,
            AllowedProcesses = ["discord"]
        };

        Assert.False(PerAppSettings.IsEnabled(config, ""));
        Assert.False(PerAppSettings.IsEnabled(config, null));
    }

    [Fact]
    public void IsEnabled_UnknownProcessIsBlockedWhenBlockListNonEmpty()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowAllExceptBlocked,
            BlockedProcesses = ["discord"]
        };

        Assert.False(PerAppSettings.IsEnabled(config, ""));
    }

    [Fact]
    public void GetAvailabilityReason_ExplainsWhyProcessIsSuppressed()
    {
        var config = new AppConfig
        {
            AppFilteringMode = PerAppSettings.AllowListedOnly,
            AllowedProcesses = ["discord"]
        };

        var reason = PerAppSettings.GetAvailabilityReason(config, "slack");

        Assert.Equal("Not on the allow list.", reason);
    }
}
