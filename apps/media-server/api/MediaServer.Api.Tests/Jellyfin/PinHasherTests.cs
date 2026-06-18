using MediaServer.Api.Jellyfin.Auth;

namespace MediaServer.Api.Tests.Jellyfin;

public sealed class PinHasherTests
{
    private readonly Argon2idPinHasher _hasher = new();

    [Fact]
    public void Verifies_a_correct_pin()
    {
        var encoded = _hasher.Hash("123456");

        Assert.True(_hasher.Verify("123456", encoded));
    }

    [Fact]
    public void Rejects_an_incorrect_pin()
    {
        var encoded = _hasher.Hash("123456");

        Assert.False(_hasher.Verify("654321", encoded));
    }

    [Fact]
    public void Hash_is_salted_so_two_hashes_of_the_same_pin_differ()
    {
        Assert.NotEqual(_hasher.Hash("123456"), _hasher.Hash("123456"));
    }

    [Fact]
    public void Never_stores_the_plaintext_pin()
    {
        var encoded = _hasher.Hash("864213");

        Assert.DoesNotContain("864213", encoded);
        Assert.StartsWith("argon2id$", encoded);
    }

    [Fact]
    public void Rejects_a_malformed_hash()
    {
        Assert.False(_hasher.Verify("123456", "not-a-valid-hash"));
    }
}
