using Imposter.Abstractions;
using MediaServer.Api.Hosty;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: GenerateImposter(typeof(IHostyCoreSecrets))]

namespace MediaServer.Api.Tests.Hosty;

public sealed class LegacyCredentialCleanupTests
{
    private const string Connection = "trakt.connection.11112222333344445555666677778888.tokens";
    private const string Authorization = "trakt.authorization.11112222333344445555666677778888.device";

    [Fact]
    public async Task Cleanup_RemovesOnlyExactObsoleteKeys_AndCanRunAgain()
    {
        var keys = new List<string>
        {
            Connection, Authorization, "another-service.token",
            "trakt.connection.short.tokens", Connection + ".backup", "prefix." + Authorization,
            Connection + "\n",
        };
        var removed = new List<string>();
        var secrets = IHostyCoreSecrets.Imposter();
        secrets.ListSecretKeysAsync(Arg<CancellationToken>.Any())
            .Returns((CancellationToken _) => Task.FromResult<IReadOnlyList<string>>(keys.ToArray()));
        secrets.DeleteSecretAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns((string key, CancellationToken _) =>
            {
                removed.Add(key);
                keys.Remove(key);
                return Task.CompletedTask;
            });
        var service = new LegacyCredentialCleanup(secrets.Instance(), NullLogger<LegacyCredentialCleanup>.Instance);

        Assert.True(await service.TryCleanupAsync(default));
        Assert.True(await service.TryCleanupAsync(default));

        Assert.Equal([Connection, Authorization], removed);
        Assert.Equal(5, keys.Count);
    }

    [Fact]
    public async Task Cleanup_AfterPartialFailure_RetriesTheRemainingKeys()
    {
        var keys = new List<string> { Connection, Authorization };
        var available = false;
        var secrets = IHostyCoreSecrets.Imposter();
        secrets.ListSecretKeysAsync(Arg<CancellationToken>.Any())
            .Returns((CancellationToken _) => Task.FromResult<IReadOnlyList<string>>(keys.ToArray()));
        secrets.DeleteSecretAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns((string key, CancellationToken _) =>
            {
                if (key == Authorization && !available)
                    throw new CoreSecretsUnavailableException("Unavailable");
                keys.Remove(key);
                return Task.CompletedTask;
            });
        var service = new LegacyCredentialCleanup(secrets.Instance(), NullLogger<LegacyCredentialCleanup>.Instance);

        Assert.False(await service.TryCleanupAsync(default));
        Assert.Equal([Authorization], keys);
        available = true;
        Assert.True(await service.TryCleanupAsync(default));
        Assert.Empty(keys);
    }

    [Fact]
    public async Task Cleanup_MissingKeys_Succeeds()
    {
        var secrets = IHostyCoreSecrets.Imposter();
        secrets.ListSecretKeysAsync(Arg<CancellationToken>.Any())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        var service = new LegacyCredentialCleanup(secrets.Instance(), NullLogger<LegacyCredentialCleanup>.Instance);

        Assert.True(await service.TryCleanupAsync(default));
    }
}
