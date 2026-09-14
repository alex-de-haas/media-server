using MediaServer.Api.Library;
using MediaServer.Api.WatchHistory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class WatchHistoryEndpointMappingTests
{
    private static int StatusOf(IResult result) => result switch
    {
        ProblemHttpResult problem => problem.StatusCode,
        IStatusCodeHttpResult coded => coded.StatusCode ?? 0,
        _ => 0,
    };

    [Theory]
    [InlineData(SetWatchedAtStatus.Updated, StatusCodes.Status204NoContent)]
    // Unknown and someone else's entry arrive here as the same status, so the route cannot be used to
    // probe for one — the boundary the deletion route already enforces.
    [InlineData(SetWatchedAtStatus.NotFound, StatusCodes.Status404NotFound)]
    // The state is fine; the request is not.
    [InlineData(SetWatchedAtStatus.FutureInstant, StatusCodes.Status400BadRequest)]
    public void SettingAPlaysTimeAnswersWithoutRevealingWhoseEntryItIs(SetWatchedAtStatus status, int expected) =>
        Assert.Equal(expected, StatusOf(WatchHistoryEndpoints.ToResult(status)));

    [Theory]
    [InlineData(LogWatchStatus.Recorded, StatusCodes.Status200OK)]
    [InlineData(LogWatchStatus.ItemNotFound, StatusCodes.Status404NotFound)]
    // A folder exists, so 404 would send the caller looking for the wrong bug.
    [InlineData(LogWatchStatus.NotPlayable, StatusCodes.Status400BadRequest)]
    [InlineData(LogWatchStatus.FutureInstant, StatusCodes.Status400BadRequest)]
    public void LoggingAWatchAnswersTheReasonItWasRefused(LogWatchStatus status, int expected) =>
        Assert.Equal(
            expected,
            StatusOf(LibraryEndpoints.ToResult(new LogWatchResult(status, status == LogWatchStatus.Recorded ? new UserItemDataDto("key") : null))));

}
