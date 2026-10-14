using MediaServer.Api.Native;

namespace MediaServer.Api.Transcoding;

/// <summary>Reuses media URL signing with a distinct purpose: an export ticket never grants library playback.</summary>
public sealed class ExportDownloadTokens(NativeUrlSigningKey key, TimeProvider time)
{
    private readonly NativeUrlTokenService tokens = new(key, time, "export-v1");
    public string Mint(Guid id) => tokens.Mint(0, id, NativeUrlTokenMethods.Read, TimeSpan.FromMinutes(5));
    public bool Validate(string? token, Guid id, string method) => tokens.Validate(token, id, method).IsValid;
}
