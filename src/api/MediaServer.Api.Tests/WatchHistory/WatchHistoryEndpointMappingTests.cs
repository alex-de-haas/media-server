using System.Text.Json;
using System.Text.Json.Serialization;
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
    // The instance-wide JSON config registers this converter, so both preview counts and apply skip
    // reasons reach the browser as enum names. Mirror it here to pin the wire shape.
    private static readonly JsonSerializerOptions EnumNamed = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
        Assert.Null(WatchHistoryEndpoints.ToResponse((WatchHistoryProviderConnection?)null));
    }

    [Fact]
    public void AnAbsentScopeMeansEverything()
    {
        // The popup's default — sync the whole library — arrives as no body at all.
        var scope = WatchHistoryEndpoints.ToScope(null);
        Assert.Empty(scope.CatalogIds);
        Assert.Empty(scope.Kinds);
    }

    [Fact]
    public void AScopeWithNullAxesIsTreatedAsEmptyNotNull()
    {
        // A JSON body that names neither axis must not reach the service as a null list.
        var scope = WatchHistoryEndpoints.ToScope(new WatchHistorySyncScopeRequest(null, null));
        Assert.Empty(scope.CatalogIds);
        Assert.Empty(scope.Kinds);
    }

    [Fact]
    public void AScopeCarriesItsCatalogsAndKindsThrough()
    {
        var catalog = Guid.NewGuid();
        var scope = WatchHistoryEndpoints.ToScope(
            new WatchHistorySyncScopeRequest([catalog], [WatchHistoryMediaKind.Episode]));
        Assert.Equal(catalog, Assert.Single(scope.CatalogIds));
        Assert.Equal(WatchHistoryMediaKind.Episode, Assert.Single(scope.Kinds));
    }

    [Fact]
    public void APreviewResponseKeysItsCountsByClassificationName()
    {
        // The UI switches on these names; an index would silently mislabel the tallies.
        var preview = new WatchHistorySyncPreview(
            RunId: Guid.NewGuid(),
            Scope: WatchHistorySyncScope.Everything,
            Counts: new Dictionary<WatchHistorySyncClassification, int>
            {
                [WatchHistorySyncClassification.RemoteOnly] = 3,
            },
            Sample:
            [
                new WatchHistorySyncEntry(
                    Guid.NewGuid(), "The Matrix", WatchHistorySyncClassification.RemoteOnly, 0, 1),
            ],
            HasPendingOutboundWork: false,
            HasTerminalOutboundWork: false,
            AggregateCountsMayCollapse: false);

        var response = WatchHistoryEndpoints.ToResponse(preview);

        Assert.Equal(preview.RunId, response.RunId);
        Assert.Equal(3, response.Counts[WatchHistorySyncClassification.RemoteOnly]);
        var serialized = JsonSerializer.Serialize(response, EnumNamed);
        Assert.Contains("\"RemoteOnly\":3", serialized, StringComparison.Ordinal);
        Assert.Contains("The Matrix", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApplyResultKeysItsSkipsBySkipReasonName()
    {
        // The apply endpoint returns this record directly; the Sync popup switches on the skip-reason
        // names, so an index here would silently mislabel why items were set aside.
        var result = new WatchHistorySyncApplyResult(
            Imported: 2,
            Exported: 1,
            Unchanged: 5,
            Skipped: new Dictionary<WatchHistorySyncSkip, int>
            {
                [WatchHistorySyncSkip.ExportFailed] = 3,
                [WatchHistorySyncSkip.AmbiguousLocalIdentity] = 1,
            });

        var serialized = JsonSerializer.Serialize(result, EnumNamed);

        Assert.Contains("\"ExportFailed\":3", serialized, StringComparison.Ordinal);
        Assert.Contains("\"AmbiguousLocalIdentity\":1", serialized, StringComparison.Ordinal);
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
