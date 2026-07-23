namespace MediaServer.Api.WatchHistory;

/// <summary>What Settings needs to render one provider without knowing anything about it.</summary>
/// <param name="Key">Stable provider key.</param>
/// <param name="DisplayName">Name for the operator's eyes.</param>
/// <param name="IsConfigured">Whether the operator has supplied this provider's application settings.</param>
/// <param name="Capabilities">What the adapter can do.</param>
public sealed record WatchHistoryProviderDescriptor(
    string Key, string DisplayName, bool IsConfigured, WatchHistoryCapabilities Capabilities);

/// <summary>
/// Resolves adapters by stable key, the same shape the metadata providers use. Callers hold keys, not
/// types, so nothing outside an adapter mentions a provider by name.
/// </summary>
public interface IWatchHistoryProviderRegistry
{
    /// <summary>Every registered provider, for Settings.</summary>
    IReadOnlyList<WatchHistoryProviderDescriptor> Describe();

    /// <summary>The adapter for <paramref name="providerKey"/>, or null when no such provider is registered.</summary>
    IWatchHistoryProvider? Find(string providerKey);

    /// <summary>The authorization collaborator for <paramref name="providerKey"/>, or null when it has none.</summary>
    IWatchHistoryProviderAuthorization? FindAuthorization(string providerKey);
}

/// <summary>
/// Registry over the adapters registered in DI. Keys are matched ordinally and case-insensitively so a
/// key arriving from a route or a stored row resolves the same way regardless of casing, while still
/// being an exact identifier rather than a culture-dependent comparison.
/// </summary>
public sealed class WatchHistoryProviderRegistry : IWatchHistoryProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IWatchHistoryProvider> providers;
    private readonly IReadOnlyDictionary<string, IWatchHistoryProviderAuthorization> authorizations;

    public WatchHistoryProviderRegistry(
        IEnumerable<IWatchHistoryProvider> providers,
        IEnumerable<IWatchHistoryProviderAuthorization> authorizations)
    {
        // A duplicate key would make resolution depend on registration order — a bug that would only
        // show up as one provider mysteriously shadowing another, so fail at startup instead.
        this.providers = ToLookup(providers, provider => provider.Key, nameof(IWatchHistoryProvider));
        this.authorizations = ToLookup(authorizations, authorization => authorization.ProviderKey, nameof(IWatchHistoryProviderAuthorization));
    }

    public IReadOnlyList<WatchHistoryProviderDescriptor> Describe() =>
        [.. providers.Values
            .OrderBy(provider => provider.Key, StringComparer.Ordinal)
            .Select(provider => new WatchHistoryProviderDescriptor(
                provider.Key,
                provider.DisplayName,
                FindAuthorization(provider.Key)?.IsConfigured ?? false,
                provider.Capabilities))];

    public IWatchHistoryProvider? Find(string providerKey) =>
        string.IsNullOrWhiteSpace(providerKey) ? null : providers.GetValueOrDefault(providerKey.Trim());

    public IWatchHistoryProviderAuthorization? FindAuthorization(string providerKey) =>
        string.IsNullOrWhiteSpace(providerKey) ? null : authorizations.GetValueOrDefault(providerKey.Trim());

    private static IReadOnlyDictionary<string, T> ToLookup<T>(IEnumerable<T> items, Func<T, string> key, string what)
    {
        var lookup = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var itemKey = key(item);
            if (string.IsNullOrWhiteSpace(itemKey))
            {
                throw new InvalidOperationException($"A registered {what} has an empty provider key.");
            }

            // A padded key is a typo in the adapter, not something to paper over: accepting it would
            // leak the untrimmed form through Describe() into settings and API responses, while
            // lookups matched the trimmed one. Reject it where it can still be fixed cheaply.
            if (itemKey != itemKey.Trim())
            {
                throw new InvalidOperationException(
                    $"The provider key '{itemKey}' registered for a {what} has leading or trailing whitespace.");
            }

            if (!lookup.TryAdd(itemKey, item))
            {
                throw new InvalidOperationException($"More than one {what} is registered for provider key '{itemKey}'.");
            }
        }

        return lookup;
    }
}
