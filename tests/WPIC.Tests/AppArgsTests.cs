using WPIC;
using Xunit;

namespace WPIC.Tests;

public class AppArgsTests
{
    [Fact]
    public void Parse_ReadsThemeLangAndSearch()
    {
        AppArgs.Parse(new[] { "--theme=light", "--lang=en", "--search=yandex" });

        Assert.Equal("light", AppArgs.ThemeOverride);
        Assert.Equal("en", AppArgs.LangOverride);
        Assert.Equal("yandex", AppArgs.SearchQuery);
    }

    [Fact]
    public void Parse_DecodesEscapedSearchAndLowercasesOverrides()
    {
        AppArgs.Parse(new[] { "--theme=DARK", "--lang=RU", "--search=%D1%8F%D0%BD%D0%B4%D0%B5%D0%BA%D1%81" });

        Assert.Equal("dark", AppArgs.ThemeOverride);
        Assert.Equal("ru", AppArgs.LangOverride);
        Assert.Equal("яндекс", AppArgs.SearchQuery);
    }

    [Fact]
    public void Parse_IgnoresUnknownArgs()
    {
        AppArgs.Parse(new[] { "--theme=dark", "--lang=ru" });
        AppArgs.Parse(new[] { "--whatever=1", "plain" });

        Assert.Equal("dark", AppArgs.ThemeOverride);
        Assert.Equal("ru", AppArgs.LangOverride);
    }
}