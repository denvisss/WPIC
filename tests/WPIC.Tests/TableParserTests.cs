using WPIC.Services;
using Xunit;

namespace WPIC.Tests;

public class TableParserTests
{
    [Fact]
    public void Parse_SearchOutput_ReadsRows()
    {
        var lines = new[]
        {
            "Name                  Id                    Version       Source",
            "-------------------------------------------------------------------",
            "Mozilla Firefox       Mozilla.Firefox       132.0.2       winget",
            "Mozilla Firefox ESR   Mozilla.Firefox.ESR   128.4.0       winget",
            ""
        };

        var rows = TableParser.Parse(lines);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Mozilla Firefox", rows[0].Name);
        Assert.Equal("Mozilla.Firefox", rows[0].Id);
        Assert.Equal("132.0.2", rows[0].Version);
        Assert.Equal("winget", rows[0].Source);
    }

    [Fact]
    public void Parse_UpgradeOutput_ReadsAvailableVersion()
    {
        var lines = new[]
        {
            "Name              Id                Version    Available    Source",
            "--------------------------------------------------------------------",
            "Mozilla Firefox   Mozilla.Firefox   132.0.1    132.0.2      winget"
        };

        var rows = TableParser.Parse(lines);

        var row = Assert.Single(rows);
        Assert.Equal("132.0.1", row.Version);
        Assert.Equal("132.0.2", row.Extra);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void Parse_SingleSpaceBeforeSource_StillReadsSource()
    {
        var lines = new[]
        {
            "Name            Id              Version          Available             Source",
            "--------------------------------------------------------------------------------",
            "Google Chrome   Google.Chrome   130.0.6723.117   131.0.6778.86 winget"
        };

        var rows = TableParser.Parse(lines);

        var row = Assert.Single(rows);
        Assert.Equal("131.0.6778.86", row.Extra);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void Parse_LocalizedHeaders_AreIgnored()
    {
        var lines = new[]
        {
            "Имя               Идентификатор     Версия    Источник",
            "--------------------------------------------------------",
            "VLC media player  VideoLAN.VLC      3.0.21    winget"
        };

        var rows = TableParser.Parse(lines);

        var row = Assert.Single(rows);
        Assert.Equal("VideoLAN.VLC", row.Id);
    }

    [Fact]
    public void Parse_JunkAndFooterLines_AreSkipped()
    {
        var lines = new[]
        {
            "Name   Id   Version   Source",
            "------------------------------",
            "Some App   Some.App   n/a   winget",
            "No installed package found matching input criteria."
        };

        var rows = TableParser.Parse(lines);

        Assert.Empty(rows);
    }
}