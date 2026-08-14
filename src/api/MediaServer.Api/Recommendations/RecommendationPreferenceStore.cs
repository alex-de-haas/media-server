using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>
/// One user's recommendation settings: which sources they narrowed the feed to, and how hard the
/// engine should push against TMDb's popularity ordering.
/// </summary>
/// <remarks>
/// A store rather than a field on the feed service, because the two settings are read from opposite
/// ends of the pipeline — the feed service picks sources, the built-in engine reads the dial while it
/// scores — and both must write the same row without one clobbering the other's column.
/// </remarks>
public sealed class RecommendationPreferenceStore(MediaServerDbContext database)
{
    /// <summary>The furthest the dial goes. Beyond this the collaborative signal stops meaning much.</summary>
    internal const double MaxPopularityBias = 2.0;

    /// <summary>
    /// This user's <b>Popular ↔ Deep cuts</b> setting, or zero when they have never touched it.
    /// </summary>
    /// <remarks>
    /// Zero is the value that leaves the feed ranking exactly as it did before the dial existed, which
    /// is what an absent preference should mean.
    /// </remarks>
    public async Task<double> PopularityBiasAsync(int appUserId, CancellationToken cancellationToken)
    {
        var stored = await database.RecommendationPreferences.AsNoTracking()
            .Where(row => row.AppUserId == appUserId)
            .Select(row => (double?)row.PopularityBias)
            .FirstOrDefaultAsync(cancellationToken);

        return Math.Clamp(stored ?? 0, 0, MaxPopularityBias);
    }

    /// <summary>Stores the dial. Out-of-range values are refused rather than clamped.</summary>
    /// <returns>False when the value is outside 0…<see cref="MaxPopularityBias"/>.</returns>
    public async Task<bool> SetPopularityBiasAsync(
        int appUserId, double bias, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (double.IsNaN(bias) || bias < 0 || bias > MaxPopularityBias)
        {
            return false;
        }

        var preference = await database.RecommendationPreferences
            .FirstOrDefaultAsync(row => row.AppUserId == appUserId, cancellationToken);

        if (preference is null)
        {
            // Sources stays null — "every available source", the default. Setting the dial is not a
            // statement about which sources are on.
            database.RecommendationPreferences.Add(new RecommendationPreference
            {
                Id = Guid.NewGuid(), AppUserId = appUserId, PopularityBias = bias, UpdatedAt = now,
            });
        }
        else
        {
            preference.PopularityBias = bias;
            preference.UpdatedAt = now;
        }

        await database.SaveChangesAsync(cancellationToken);
        return true;
    }
}
