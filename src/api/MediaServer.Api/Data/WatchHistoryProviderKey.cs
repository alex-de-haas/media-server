namespace MediaServer.Api.Data;

/// <summary>
/// Canonical form of a watched-history provider key as stored.
/// </summary>
/// <remarks>
/// The registry resolves keys case-insensitively, but SQLite's unique indexes are case-sensitive by
/// default. Without a canonical stored form, <c>Trakt</c> and <c>trakt</c> would satisfy the
/// <c>(AppUserId, ProviderKey)</c> uniqueness separately — two connections to one account for one user
/// — while lookups written either way would miss rows. Normalizing where the value is assigned keeps
/// the index's notion of identity the same as the registry's.
/// </remarks>
internal static class WatchHistoryProviderKey
{
    /// <summary>Trims and lowercases a key; null and blank collapse to null.</summary>
    public static string? Normalize(string? providerKey) =>
        string.IsNullOrWhiteSpace(providerKey) ? null : providerKey.Trim().ToLowerInvariant();

    /// <summary>Same, for the columns that are required rather than optional.</summary>
    public static string NormalizeRequired(string? providerKey) => Normalize(providerKey) ?? string.Empty;
}
