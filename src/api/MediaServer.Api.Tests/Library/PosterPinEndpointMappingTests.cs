using MediaServer.Api.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// What a refused poster pin becomes on the wire. The distinction matters to whoever has to act on it: a
/// <c>404</c> sends the caller looking for a missing item, a <c>400</c> tells them the tag was wrong.
/// </summary>
public sealed class PosterPinEndpointMappingTests
{
    private static int StatusOf(IResult result) => result switch
    {
        ProblemHttpResult problem => problem.StatusCode,
        IStatusCodeHttpResult coded => coded.StatusCode ?? 0,
        _ => 0,
    };

    [Theory]
    [InlineData(PinPosterResult.Ok, StatusCodes.Status204NoContent)]
    // The item exists and was found; only the tag is wrong — including the tag of one of its own backdrops.
    [InlineData(PinPosterResult.InvalidTag, StatusCodes.Status400BadRequest)]
    [InlineData(PinPosterResult.NotFound, StatusCodes.Status404NotFound)]
    public void APinAnswersWithTheStatusThatNamesWhatWentWrong(PinPosterResult result, int expected) =>
        Assert.Equal(expected, StatusOf(LibraryEndpoints.ToResult(result)));
}
