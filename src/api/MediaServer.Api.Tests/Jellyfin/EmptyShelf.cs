using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// A shelf with nothing on it, for the suites that are not about recommendations.
/// </summary>
/// <remarks>
/// An empty shelf is also the state that must not change their expectations: the Recommended view is
/// only advertised when there is something to put in it, so these suites should see exactly the views
/// they saw before it existed.
/// </remarks>
internal sealed class EmptyShelf : IRecommendationShelf
{
    public Task<IReadOnlyList<MediaItem>> GetAsync(int appUserId, int? limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaItem>>([]);

    public Task<bool> AnyAsync(int appUserId, CancellationToken cancellationToken) => Task.FromResult(false);
}
