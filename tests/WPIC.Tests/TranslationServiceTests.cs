namespace WPIC.Tests;

using WPIC.Services;
using Xunit;

public class TranslationServiceTests
{
    [Fact]
    public void ParseResponse_JoinsSegments()
    {
        const string json = """[[["Привет","Hello",null,null,10],[" мир"," world",null,null,10]],null,"en"]""";
        Assert.Equal("Привет мир", TranslationService.ParseResponse(json));
    }

    [Fact]
    public void ParseResponse_SingleSegment()
    {
        const string json = """[[["Bonjour","Hello",null,null,10]],null,"en"]""";
        Assert.Equal("Bonjour", TranslationService.ParseResponse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    public void ParseResponse_Invalid_ReturnsNull(string json) =>
        Assert.Null(TranslationService.ParseResponse(json));
}