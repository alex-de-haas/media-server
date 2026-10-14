using MediaServer.Api.Native;
using MediaServer.Api.Transcoding;

namespace MediaServer.Api.Tests.Transcoding;

public sealed class ExportDownloadTokensTests
{
    [Fact]
    public void Export_tickets_are_bound_to_job_read_methods_expiry_and_signing_purpose()
    {
        var clock = new Clock();
        var key = new NativeUrlSigningKey(new byte[32]);
        var exports = new ExportDownloadTokens(key, clock);
        var playback = new NativeUrlTokenService(key, clock);
        var id = Guid.NewGuid();
        var ticket = exports.Mint(id);
        Assert.True(exports.Validate(ticket, id, "GET"));
        Assert.True(exports.Validate(ticket, id, "HEAD"));
        Assert.False(exports.Validate(ticket, Guid.NewGuid(), "GET"));
        Assert.False(exports.Validate(ticket, id, "DELETE"));
        Assert.False(playback.Validate(ticket, id, "GET").IsValid);
        Assert.False(exports.Validate(playback.Mint(1, id, NativeUrlTokenMethods.Read), id, "GET"));
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.False(exports.Validate(ticket, id, "GET"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
