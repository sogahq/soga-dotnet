using System.Text.Json;
using Soga.Configuration;
using Soga.Conversations;

namespace Soga.Tests.Conversations;

public sealed class SogaMetadataTests
{
    private static readonly SogaConversationOptions Limits = new() { MaximumMetadataBytes = 200, MaximumMetadataProperties = 3 };

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static (string? Json, string? Error) Validate(Dictionary<string, JsonElement>? metadata) => SogaMetadata.Validate(metadata, Limits);

    [Fact]
    public void Validate_WhenEmptyOrMissing_ReturnsNothing()
    {
        Assert.Equal((null, null), Validate(null));
        Assert.Equal((null, null), Validate([]));
    }

    [Fact]
    public void Validate_AcceptsFlatStringNumberAndBooleanValues()
    {
        var (json, error) = Validate(new() { ["order.id"] = Json("1042"), ["paid"] = Json("true"), ["note"] = Json("\"hi\"") });

        Assert.Null(error);
        Assert.NotNull(json);
    }

    [Fact]
    public void Validate_ProducesTheSameCanonicalJsonRegardlessOfKeyOrder()
    {
        var a = Validate(new() { ["b"] = Json("1"), ["a"] = Json("2") });
        var b = Validate(new() { ["a"] = Json("2"), ["b"] = Json("1") });

        Assert.Equal(a.Json, b.Json);
        Assert.Equal("{\"a\":2,\"b\":1}", a.Json);
    }

    [Theory]
    [InlineData("{\"x\":1}")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public void Validate_RejectsNestingAndNull(string value)
    {
        var (json, error) = Validate(new() { ["key"] = Json(value) });

        Assert.Null(json);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad/key")]
    [InlineData("emoji-\U0001F600")]
    public void Validate_RejectsUnsafeKeys(string key)
    {
        Assert.NotNull(Validate(new() { [key] = Json("1") }).Error);
    }

    [Fact]
    public void Validate_RejectsTooLongKeysAndStringValues()
    {
        Assert.NotNull(Validate(new() { [new string('k', 51)] = Json("1") }).Error);
        Assert.NotNull(Validate(new() { ["k"] = Json("\"" + new string('v', 501) + "\"") }).Error);
    }

    [Fact]
    public void Validate_EnforcesPropertyCountAndByteLimits()
    {
        var tooMany = new Dictionary<string, JsonElement> { ["a"] = Json("1"), ["b"] = Json("1"), ["c"] = Json("1"), ["d"] = Json("1") };
        var tooBig = new Dictionary<string, JsonElement> { ["a"] = Json("\"" + new string('x', 150) + "\""), ["b"] = Json("\"" + new string('y', 150) + "\"") };

        Assert.NotNull(Validate(tooMany).Error);
        Assert.NotNull(Validate(tooBig).Error);
    }

    [Fact]
    public void Validate_CountsBytesNotCharacters()
    {
        // 60 four-byte characters is 240 bytes, over the 200 byte limit though only 60 characters long.
        var (_, error) = Validate(new() { ["k"] = Json("\"" + string.Concat(Enumerable.Repeat("\U0001F600", 60)) + "\"") });

        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_RoundTripsWhatValidateProduced()
    {
        var (json, _) = Validate(new() { ["order"] = Json("7") });

        var parsed = SogaMetadata.Parse(json);

        Assert.Equal(7, parsed!["order"].GetInt32());
        Assert.Null(SogaMetadata.Parse(null));
    }
}
