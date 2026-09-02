using System.Text.Json;

namespace MediaServer.Api.Tests.Mcp;

/// <summary>
/// The manifest's declaration of where this app's MCP surface lives, checked against the endpoints it
/// actually has.
/// </summary>
/// <remarks>
/// This is not a style check. Core resolves `interfaces.mcp[].endpoint` against the app's own endpoint
/// list to learn where to send traffic, and a name that matches nothing fails at the host with
/// "endpoint not found" — after the app has started, with every tool present and every unit test
/// green. The first version of this declaration named `api`, copied from the reference app, while
/// this app's endpoints were `ui` and `jellyfin`; nothing in the build or the suite could see it.
/// </remarks>
public sealed class ManifestInterfaceTests
{
    [Fact]
    public void Every_mcp_interface_names_an_endpoint_that_exists()
    {
        var manifest = Manifest();
        var endpoints = manifest.GetProperty("endpoints").EnumerateArray()
            .Select(endpoint => endpoint.GetProperty("key").GetString())
            .ToHashSet(StringComparer.Ordinal);

        var interfaces = manifest.GetProperty("interfaces").GetProperty("mcp").EnumerateArray().ToArray();

        Assert.NotEmpty(interfaces);
        foreach (var mcp in interfaces)
        {
            var named = mcp.GetProperty("endpoint").GetString();
            Assert.True(
                endpoints.Contains(named!),
                $"interfaces.mcp names endpoint '{named}', which the manifest does not declare. "
                + $"Declared: {string.Join(", ", endpoints)}");
        }
    }

    [Fact]
    public void Every_endpoint_names_a_service_and_a_port_that_exist()
    {
        // The same mistake one level down, and the reason the endpoint was missing in the first place:
        // the API's internal port had no endpoint, so there was nothing correct to point at.
        var manifest = Manifest();
        var ports = manifest.GetProperty("services").EnumerateArray()
            .SelectMany(service => service.GetProperty("runtimes").EnumerateObject()
                .SelectMany(runtime => runtime.Value.TryGetProperty("ports", out var declared)
                    ? declared.EnumerateArray().Select(port =>
                        (Service: service.GetProperty("key").GetString(), Port: port.GetProperty("key").GetString()))
                    : []))
            .ToHashSet();

        foreach (var endpoint in manifest.GetProperty("endpoints").EnumerateArray())
        {
            var key = endpoint.GetProperty("key").GetString();
            var pair = (endpoint.GetProperty("service").GetString(), endpoint.GetProperty("port").GetString());
            // Named rather than left to the default output: this test exists to diagnose exactly this
            // class of mistake, and a failure reading "collection did not contain" would make the reader
            // go looking for which endpoint it meant.
            Assert.True(
                ports.Contains(pair),
                $"endpoint '{key}' names {pair.Item1}.{pair.Item2}, which no service declares. "
                + $"Declared: {string.Join(", ", ports.Select(port => $"{port.Service}.{port.Port}"))}");
        }
    }

    private static JsonElement Manifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "manifest.json")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory?.FullName ?? ".", "manifest.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }
}
