using MediaServer.Api.Configuration;

namespace MediaServer.Api.Tests;

/// <summary>
/// Settings for fixtures that only need a display language. Several read surfaces now rank artwork by
/// language (<see cref="MediaServer.Api.Metadata.ImageSelection"/>), so they take
/// <see cref="MediaServerSettings"/>; a fixture that is not about languages says so by using
/// <see cref="English"/> rather than restating the list.
/// </summary>
internal static class TestSettings
{
    /// <summary>A single supported language, English — what the fixtures assumed before artwork was ranked.</summary>
    public static MediaServerSettings English { get; } = new() { SupportedLanguages = ["en-US"] };

    /// <summary>Settings for a specific language order, for tests that are about the ranking itself.</summary>
    public static MediaServerSettings For(params string[] languages) => new() { SupportedLanguages = languages };
}
