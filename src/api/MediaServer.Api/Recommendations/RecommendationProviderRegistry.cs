namespace MediaServer.Api.Recommendations;

/// <summary>What the UI needs to render one source without knowing anything about it.</summary>
public sealed record RecommendationProviderDescriptor(string Key, string DisplayName);

/// <summary>
/// Resolves recommendation adapters by stable key, the same shape the watched-history and metadata
/// registries use.
/// </summary>
public interface IRecommendationProviderRegistry
{
    /// <summary>Every registered provider, in a stable order.</summary>
    IReadOnlyList<RecommendationProviderDescriptor> Describe();

    /// <summary>The adapter for <paramref name="key"/>, or null when no such provider is registered.</summary>
    IRecommendationProvider? Find(string key);

    /// <summary>Only the providers that can answer for this user right now.</summary>
    Task<IReadOnlyList<IRecommendationProvider>> AvailableForAsync(
        int appUserId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecommendationProviderRegistry"/>
public sealed class RecommendationProviderRegistry : IRecommendationProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IRecommendationProvider> providers;
    private readonly ILogger<RecommendationProviderRegistry> logger;

    public RecommendationProviderRegistry(
        IEnumerable<IRecommendationProvider> providers, ILogger<RecommendationProviderRegistry> logger)
    {
        this.logger = logger;
        var lookup = new Dictionary<string, IRecommendationProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Key))
            {
                throw new InvalidOperationException("A registered recommendation provider has an empty key.");
            }

            // A padded key would leak the untrimmed form into API responses while lookups matched the
            // trimmed one; reject it where it is still cheap to fix.
            if (provider.Key != provider.Key.Trim())
            {
                throw new InvalidOperationException(
                    $"The recommendation provider key '{provider.Key}' has leading or trailing whitespace.");
            }

            // A duplicate key would make resolution depend on registration order — a bug that surfaces
            // only as one provider silently shadowing another.
            if (!lookup.TryAdd(provider.Key, provider))
            {
                throw new InvalidOperationException(
                    $"More than one recommendation provider is registered for key '{provider.Key}'.");
            }
        }

        this.providers = lookup;
    }

    public IReadOnlyList<RecommendationProviderDescriptor> Describe() =>
        [.. providers.Values
            .OrderBy(provider => provider.Key, StringComparer.Ordinal)
            .Select(provider => new RecommendationProviderDescriptor(provider.Key, provider.DisplayName))];

    public IRecommendationProvider? Find(string key) =>
        string.IsNullOrWhiteSpace(key) ? null : providers.GetValueOrDefault(key.Trim());

    public async Task<IReadOnlyList<IRecommendationProvider>> AvailableForAsync(
        int appUserId, CancellationToken cancellationToken)
    {
        var available = new List<IRecommendationProvider>();
        foreach (var provider in providers.Values.OrderBy(provider => provider.Key, StringComparer.Ordinal))
        {
            try
            {
                if (await provider.IsAvailableAsync(appUserId, cancellationToken))
                {
                    available.Add(provider);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // An optional source that cannot even answer "am I available" must not take the feed
                // down with it; the remaining providers still have something to say.
                logger.LogWarning(
                    exception, "Recommendation provider {Key} failed its availability check.", provider.Key);
            }
        }

        return available;
    }
}
