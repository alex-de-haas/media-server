using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Configuration;

/// <summary>
/// Read/write access to the operator-editable <see cref="AppSettings"/> singleton row. Centralizes the
/// get-or-create and the release-group normalization (trim, drop blanks, case-insensitive dedupe) so the
/// identify path and the settings endpoints agree on the stored shape.
/// </summary>
public sealed class AppSettingsService(MediaServerDbContext database)
{
    /// <summary>Current custom release groups, or an empty list when nothing is configured yet.</summary>
    public async Task<IReadOnlyList<string>> GetCustomReleaseGroupsAsync(CancellationToken cancellationToken)
    {
        var row = await database.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(settings => settings.Id == AppSettings.SingletonId, cancellationToken);
        return row?.CustomReleaseGroups ?? [];
    }

    /// <summary>Replaces the custom release groups with the normalized form of <paramref name="groups"/>.</summary>
    public async Task<IReadOnlyList<string>> UpdateCustomReleaseGroupsAsync(
        IEnumerable<string>? groups, CancellationToken cancellationToken)
    {
        var normalized = Normalize(groups);

        var row = await database.AppSettings
            .FirstOrDefaultAsync(settings => settings.Id == AppSettings.SingletonId, cancellationToken);
        if (row is null)
        {
            row = new AppSettings { Id = AppSettings.SingletonId };
            database.AppSettings.Add(row);
        }

        row.CustomReleaseGroups = normalized;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return normalized;
    }

    /// <summary>Trims entries, drops null/blank ones, and de-duplicates case-insensitively while keeping order.</summary>
    internal static List<string> Normalize(IEnumerable<string>? groups) =>
        (groups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
