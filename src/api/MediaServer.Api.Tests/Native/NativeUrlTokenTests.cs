using MediaServer.Api.Native;

namespace MediaServer.Api.Tests.Native;

public sealed class NativeUrlTokenTests
{
    private static readonly Guid Source = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSource = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (NativeUrlTokenService Service, FixedTime Time) Build()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var key = new NativeUrlSigningKey(new byte[32]);
        return (new NativeUrlTokenService(key, time), time);
    }

    [Fact]
    public void Accepts_its_own_token_for_the_source_and_method_it_was_minted_for()
    {
        var (service, _) = Build();

        var token = service.Mint(appUserId: 7, Source, NativeUrlTokenMethods.Read);
        var result = service.Validate(token, Source, "GET");

        Assert.True(result.IsValid);
        Assert.Equal(7, result.AppUserId);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("head")]
    public void Allows_the_read_methods_a_player_actually_issues(string method)
    {
        var (service, _) = Build();

        var token = service.Mint(appUserId: 1, Source, NativeUrlTokenMethods.Read);

        Assert.True(service.Validate(token, Source, method).IsValid);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void Refuses_methods_the_token_was_not_minted_for(string method)
    {
        var (service, _) = Build();

        var token = service.Mint(appUserId: 1, Source, NativeUrlTokenMethods.Read);
        var result = service.Validate(token, Source, method);

        Assert.False(result.IsValid);
        Assert.Equal(NativeUrlTokenFailure.MethodNotAllowed, result.Failure);
    }

    [Fact]
    public void Refuses_a_token_minted_for_a_different_media_source()
    {
        var (service, _) = Build();

        // The point of binding to a source rather than a title: two editions of one film are two
        // sources, and a token for one must not open the other.
        var token = service.Mint(appUserId: 1, Source, NativeUrlTokenMethods.Read);
        var result = service.Validate(token, OtherSource, "GET");

        Assert.False(result.IsValid);
        Assert.Equal(NativeUrlTokenFailure.WrongSource, result.Failure);
    }

    [Fact]
    public void Survives_a_whole_film_and_then_expires()
    {
        var (service, time) = Build();
        var token = service.Mint(appUserId: 1, Source, NativeUrlTokenMethods.Read);

        // A token that expires between two Range requests of one file is a broken token, so the
        // default lifetime has to outlast a long film with pauses.
        time.Now = time.Now.AddHours(4);
        Assert.True(service.Validate(token, Source, "GET").IsValid);

        time.Now = time.Now.AddHours(9);
        var expired = service.Validate(token, Source, "GET");
        Assert.False(expired.IsValid);
        Assert.Equal(NativeUrlTokenFailure.Expired, expired.Failure);
    }

    [Fact]
    public void Refuses_a_token_whose_payload_was_edited()
    {
        var (service, _) = Build();
        var token = service.Mint(appUserId: 1, Source, NativeUrlTokenMethods.Read);

        // Push the expiry far into the future while keeping the original signature.
        var parts = token.Split('.');
        parts[4] = DateTimeOffset.UtcNow.AddYears(5).ToUnixTimeSeconds().ToString();
        var forged = string.Join('.', parts);

        var result = service.Validate(forged, Source, "GET");

        Assert.False(result.IsValid);
        Assert.Equal(NativeUrlTokenFailure.BadSignature, result.Failure);
    }

    [Fact]
    public void Refuses_a_token_signed_with_another_instances_key()
    {
        var time = new FixedTime(DateTimeOffset.UnixEpoch.AddYears(56));
        var mine = new NativeUrlTokenService(new NativeUrlSigningKey(new byte[32]), time);
        var theirs = new NativeUrlTokenService(new NativeUrlSigningKey(Enumerable.Repeat((byte)9, 32).ToArray()), time);

        var token = theirs.Mint(appUserId: 1, Source, NativeUrlTokenMethods.Read);

        Assert.Equal(NativeUrlTokenFailure.BadSignature, mine.Validate(token, Source, "GET").Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_an_absent_token_as_missing_rather_than_malformed(string? token)
    {
        var (service, _) = Build();

        Assert.Equal(NativeUrlTokenFailure.Missing, service.Validate(token, Source, "GET").Failure);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("v1.1.deadbeef")]
    public void Refuses_a_token_that_is_not_shaped_like_one(string token)
    {
        var (service, _) = Build();

        Assert.False(service.Validate(token, Source, "GET").IsValid);
    }
}
