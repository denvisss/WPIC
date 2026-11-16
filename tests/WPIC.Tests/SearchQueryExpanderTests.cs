using WPIC.Services;
using Xunit;

namespace WPIC.Tests;

public class SearchQueryExpanderTests
{
    [Fact]
    public void Expand_CyrillicAlias_AddsLatinVariant()
    {
        var variants = SearchQueryExpander.Expand("яндекс");
        Assert.Contains("яндекс", variants);
        Assert.Contains("yandex", variants);
    }

    [Fact]
    public void Expand_MultiWordQuery_MapsEachWord()
    {
        var variants = SearchQueryExpander.Expand("Яндекс Браузер");
        Assert.Contains("yandex browser", variants);
    }

    [Fact]
    public void Expand_UnknownCyrillicWord_FallsBackToTranslit()
    {
        var variants = SearchQueryExpander.Expand("ромашка");
        Assert.Contains("romashka", variants);
    }

    [Fact]
    public void Expand_LatinQuery_AddsRussianAlias()
    {
        var variants = SearchQueryExpander.Expand("yandex");
        Assert.Contains("яндекс", variants);
    }

    [Fact]
    public void Expand_EmptyQuery_ReturnsNothing()
    {
        Assert.Empty(SearchQueryExpander.Expand("   "));
    }

    [Fact]
    public void Expand_VariantsAreDistinctAndLimited()
    {
        var variants = SearchQueryExpander.Expand("chrome");
        Assert.Equal(variants.Count, variants.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.InRange(variants.Count, 1, 4);
    }

    [Theory]
    [InlineData("яндекс", "yandeks")]
    [InlineData("щавель", "shchavel")]
    [InlineData("Телеграм", "telegram")]
    public void Translit_RuToEn(string input, string expected)
    {
        Assert.Equal(expected, SearchQueryExpander.TranslitRuToEn(input));
    }
}