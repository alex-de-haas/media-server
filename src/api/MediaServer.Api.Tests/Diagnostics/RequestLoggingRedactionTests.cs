using MediaServer.Api.Diagnostics;

namespace MediaServer.Api.Tests.Diagnostics;

/// <summary>
/// The request log is written to an operator-readable file and, since PLAYBACK_DIAGNOSTICS, outside
/// Development too. Jellyfin accepts a reusable access token as the <c>api_key</c> query parameter on
/// media and image URLs, so an unredacted query would persist working credentials on disk.
/// </summary>
public sealed class RequestLoggingRedactionTests
{
    [Theory]
    [InlineData("?api_key=abc123", "?api_key=***")]
    [InlineData("?ApiKey=abc123", "?ApiKey=***")]
    [InlineData("?API_KEY=abc123", "?API_KEY=***")]
    public void RedactQuery_MasksTheJellyfinTokenParameters(string query, string expected) =>
        Assert.Equal(expected, RequestLoggingMiddleware.RedactQuery(query));

    [Fact]
    public void RedactQuery_MasksOnlyTheCredential_LeavingTheRestByteIdentical()
    {
        var redacted = RequestLoggingMiddleware.RedactQuery(
            "?userId=70ccd5fe&api_key=secret-token&PositionTicks=123&MediaSourceId=src-1");

        Assert.Equal("?userId=70ccd5fe&api_key=***&PositionTicks=123&MediaSourceId=src-1", redacted);
    }

    [Fact]
    public void RedactQuery_MasksUnknownCredentialShapedParameters()
    {
        // Broader than the two parameters accepted today, so a future credential parameter is
        // redacted by default rather than leaking until someone remembers this file.
        Assert.Equal("?X-Emby-Token=***", RequestLoggingMiddleware.RedactQuery("?X-Emby-Token=abc"));
        Assert.Equal("?accessToken=***", RequestLoggingMiddleware.RedactQuery("?accessToken=abc"));
        Assert.Equal("?client_secret=***", RequestLoggingMiddleware.RedactQuery("?client_secret=abc"));
        Assert.Equal("?password=***", RequestLoggingMiddleware.RedactQuery("?password=abc"));
        Assert.Equal("?signature=***", RequestLoggingMiddleware.RedactQuery("?signature=abc"));
    }

    [Fact]
    public void RedactQuery_KeepsTheFieldsPhase0NeedsToRead()
    {
        // The observation matrix reads these back; redacting them would defeat the diagnostic.
        const string query = "?ItemId=abc&PositionTicks=999&PlaySessionId=sess-1&MediaSourceId=src&DatePlayed=2026-07-22";

        Assert.Equal(query, RequestLoggingMiddleware.RedactQuery(query));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RedactQuery_HandlesAnAbsentQuery(string? query) =>
        Assert.Equal(string.Empty, RequestLoggingMiddleware.RedactQuery(query));

    [Fact]
    public void RedactQuery_MasksEveryOccurrence_AndSurvivesAnEmptyValue()
    {
        Assert.Equal(
            "?api_key=***&other=1&token=***",
            RequestLoggingMiddleware.RedactQuery("?api_key=a&other=1&token=b"));
        Assert.Equal("?api_key=***", RequestLoggingMiddleware.RedactQuery("?api_key="));
    }
}
