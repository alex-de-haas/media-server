using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>
/// A provider that can say which of its titles changed recently, so a refresh can visit those instead of
/// the whole library.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="IMetadataProvider"/> because it is a capability, not a requirement: a
/// provider that cannot answer this simply has no incremental refresh, and its titles are refreshed when
/// someone asks for them.
/// </remarks>
public interface IMetadataChangeFeed
{
    /// <summary>The provider key these ids belong to, matching <see cref="IMetadataProvider.Key"/>.</summary>
    string Key { get; }

    /// <summary>
    /// The furthest back a single query may reach. A gap longer than this cannot be answered at all —
    /// the provider has stopped keeping it.
    /// </summary>
    TimeSpan MaxWindow { get; }

    /// <summary>
    /// Provider ids of works of <paramref name="kind"/> that changed between the two instants. Null when
    /// the provider could not answer — which is not the same as "nothing changed", and callers must not
    /// treat it as a clean bill of health.
    /// </summary>
    Task<IReadOnlyCollection<string>?> GetChangedAsync(
        MediaKind kind, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
}
