using MediaServer.Api.Data;
using MediaServer.Api.WatchHistory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// The endpoint group's pure mapping decisions: what status a provider failure becomes, and what a
/// connection looks like on the wire. Both fail quietly if wrong — a mis-mapped status leaves the UI
/// showing the wrong remedy, and an over-shared field leaks a credential.
/// </summary>
public sealed class WatchHistoryEndpointMappingTests
{
    private static int StatusOf(IResult result) => result switch
    {
        ProblemHttpResult problem => problem.StatusCode,
        IStatusCodeHttpResult coded => coded.StatusCode ?? 0,
        _ => 0,
    };

    [Theory]
    // Nothing is wrong with the request, so these are conflicts with the current state, not 400s.
    [InlineData(WatchHistoryFailure.Unsupported, StatusCodes.Status409Conflict)]
    [InlineData(WatchHistoryFailure.IdentityRejected, StatusCodes.Status409Conflict)]
    [InlineData(WatchHistoryFailure.AuthenticationRequired, StatusCodes.Status409Conflict)]
    [InlineData(WatchHistoryFailure.RateLimited, StatusCodes.Status429TooManyRequests)]
    // The provider is at fault, not this app: 502 says "try again", 500 would say "we are broken".
    [InlineData(WatchHistoryFailure.Transient, StatusCodes.Status502BadGateway)]
    [InlineData(WatchHistoryFailure.ContractViolation, StatusCodes.Status502BadGateway)]
    public void ProviderFailuresMapToStatusesThatTellTheUserWhatToDo(WatchHistoryFailure failure, int expected) =>
        Assert.Equal(expected, StatusOf(WatchHistoryEndpoints.ToProblem(failure, "detail")));

    [Fact]
    public void AConnectionResponseCarriesNoCredentialMaterial()
    {
        var connection = new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(),
            AppUserId = 1,
            ProviderKey = "trakt",
            SecretKey = "trakt.connection.abc.tokens",
            ProviderAccountId = "alex-slug",
            ProviderAccountName = "alex",
            Status = WatchHistoryConnectionStatus.Connected,
            ConnectedAt = DateTimeOffset.UnixEpoch,
        };

        var response = WatchHistoryEndpoints.ToResponse(connection);

        Assert.Equal("trakt", response!.ProviderKey);
        Assert.Equal("alex", response.AccountName);
        // The secrets-store key is not a credential, but it names where one lives; there is no reason
        // for a browser to see it.
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.DoesNotContain("SecretKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trakt.connection", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingConnectionMapsToNull()
    {
        // Settings renders "not connected" from this rather than from a sentinel row.
        Assert.Null(WatchHistoryEndpoints.ToResponse(null));
    }

    [Fact]
    public void AReconnectRequiredConnectionSurfacesItsStatusAndReason()
    {
        var response = WatchHistoryEndpoints.ToResponse(new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(),
            AppUserId = 1,
            ProviderKey = "trakt",
            SecretKey = "k",
            Status = WatchHistoryConnectionStatus.RequiresReconnect,
            LastError = "Trakt rejected the stored refresh token.",
            ConnectedAt = DateTimeOffset.UnixEpoch,
        });

        Assert.Equal("RequiresReconnect", response!.Status);
        Assert.Equal("Trakt rejected the stored refresh token.", response.LastError);
    }
}
