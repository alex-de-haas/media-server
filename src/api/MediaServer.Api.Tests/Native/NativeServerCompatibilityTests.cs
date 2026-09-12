using System.Text.Json;
using System.Text.Json.Serialization;
using MediaServer.Api.Native;

namespace MediaServer.Api.Tests.Native;

public sealed class NativeServerCompatibilityTests
{
    [Theory]
    [InlineData(JsonIgnoreCondition.Never)]
    [InlineData(JsonIgnoreCondition.WhenWritingDefault)]
    public void ServerDescription_RetainsDisabledCapabilities_ForExistingV1Decoders(JsonIgnoreCondition ignore)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = ignore };
        var response = new NativeServerDescription("Home", "com.haas.media-server", NativeSurface.Version,
            null, new NativeServerCapabilities(true, true, true));

        var json = JsonSerializer.Serialize(response, options);
        // Models the existing client's required Bool. An omitted field must fail this regression,
        // rather than silently taking the C# default value as an optional field would.
        var legacy = JsonSerializer.Deserialize<ExistingServerDescription>(json, options)!;
        Assert.False(legacy.Capabilities.Trakt);
        Assert.True(legacy.Capabilities.Recommendations);
        Assert.Equal("1", response.SurfaceVersion);
    }

    private sealed record ExistingServerDescription(ExistingCapabilities Capabilities);
    private sealed record ExistingCapabilities(bool Recommendations)
    {
        [JsonRequired]
        public bool Trakt { get; init; }
    }
}
