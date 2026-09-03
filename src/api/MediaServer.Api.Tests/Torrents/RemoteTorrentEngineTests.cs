using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Torrents;

public sealed class RemoteTorrentEngineTests
{
    [Fact]
    public void ToMountRelative_UnderMountRoot_ReturnsMountLabelAndIncomingRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "catalogs", "media");
        var save = Path.Combine(root, ".incoming", "abc123");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(save, [new CatalogMount("media", root)]);

        Assert.Equal("media", label);
        Assert.Equal(".incoming/abc123", relative);
    }

    [Fact]
    public void ToMountRelative_CatalogSubdirectoryUnderMount_PreservesSubdirectoryAndLabel()
    {
        // Mount root is the shared host path; the catalog sits in a subdirectory of it.
        var mount = Path.Combine(Path.GetTempPath(), "mnt", "catalogRoots");
        var save = Path.Combine(mount, "media", ".incoming", "abc");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(save, [new CatalogMount("downloads", mount)]);

        Assert.Equal("downloads", label);
        Assert.Equal("media/.incoming/abc", relative);
    }

    [Fact]
    public void ToMountRelative_PicksTheMatchingMountAmongSeveral()
    {
        var movies = Path.Combine(Path.GetTempPath(), "mnt", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "mnt", "tv");
        var save = Path.Combine(tv, "Anime", ".incoming", "abc");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(
            save, [new CatalogMount("movies", movies), new CatalogMount("tv", tv)]);

        Assert.Equal("tv", label);
        Assert.Equal("Anime/.incoming/abc", relative);
    }

    [Fact]
    public void ToMountRelative_NoMatchingMount_FallsBackToTrailingSegmentsWithNullLabel()
    {
        var save = Path.Combine(Path.GetTempPath(), "somewhere", ".incoming", "abc");

        var (label, relative) = RemoteTorrentEngine.ToMountRelative(
            save, [new CatalogMount("other", Path.Combine(Path.GetTempPath(), "other", "root"))]);

        Assert.Null(label);
        Assert.Equal(".incoming/abc", relative);
    }

    // ---- DHT status fan-out ----

    private static DhtStatus Dht(bool enabled = true, bool running = true, string? state = "Ready", int nodes = 42) =>
        new(enabled, running, state, nodes);

    [Fact]
    public void IsReportableDhtChange_FirstStatus_IsReported() =>
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(null, Dht()));

    [Fact]
    public void IsReportableDhtChange_NodeCountChurn_IsNotReported() =>
        // The routing table grows constantly; that alone must not push an event per change.
        Assert.False(RemoteTorrentEngine.IsReportableDhtChange(Dht(nodes: 42), Dht(nodes: 87)));

    [Fact]
    public void IsReportableDhtChange_TableBecomingEmpty_IsReported() =>
        // Running with an empty table is what "enabled but not working" looks like, so this edge matters.
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(nodes: 42), Dht(nodes: 0)));

    [Fact]
    public void IsReportableDhtChange_TableFillingUp_IsReported() =>
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(nodes: 0), Dht(nodes: 1)));

    [Theory]
    [InlineData("Initialising")]
    [InlineData("NotReady")]
    public void IsReportableDhtChange_StateTransition_IsReported(string state) =>
        // Initialising vs NotReady is the difference between "starting" and "broken" in the UI.
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(state: "Ready"), Dht(state: state)));

    [Fact]
    public void IsReportableDhtChange_EngineStoppingRunningDht_IsReported() =>
        Assert.True(RemoteTorrentEngine.IsReportableDhtChange(Dht(), Dht(running: false, state: null, nodes: 0)));

    [Fact]
    public void IsReportableDhtChange_IdenticalStatus_IsNotReported() =>
        Assert.False(RemoteTorrentEngine.IsReportableDhtChange(Dht(), Dht()));

    // ---- VPN status fan-out ----

    private static VpnStatus Vpn(
        bool connected = true, string? exitIp = "203.0.113.7", string? profile = "nl-ams",
        string? pending = null, string? error = null, DateTimeOffset? checkedAt = null) =>
        new(connected, "tun0", "10.8.0.2", exitIp, "NL", checkedAt ?? DateTimeOffset.UnixEpoch, profile, pending, error);

    [Fact]
    public void IsReportableVpnChange_FirstStatus_IsReported() =>
        Assert.True(RemoteTorrentEngine.IsReportableVpnChange(null, Vpn()));

    [Fact]
    public void IsReportableVpnChange_CheckedAtAlone_IsNotReported() =>
        // The engine stamps every poll; that alone must not push an event per poll.
        Assert.False(RemoteTorrentEngine.IsReportableVpnChange(Vpn(), Vpn(checkedAt: DateTimeOffset.UnixEpoch.AddMinutes(5))));

    [Fact]
    public void IsReportableVpnChange_TunnelOrExitChange_IsReported()
    {
        Assert.True(RemoteTorrentEngine.IsReportableVpnChange(Vpn(), Vpn(connected: false)));
        Assert.True(RemoteTorrentEngine.IsReportableVpnChange(Vpn(), Vpn(exitIp: "198.51.100.1")));
    }

    [Fact]
    public void IsReportableVpnChange_ProfileTrio_IsReported()
    {
        // A switch starting, landing, or failing is exactly what the picker waits for.
        Assert.True(RemoteTorrentEngine.IsReportableVpnChange(Vpn(), Vpn(pending: "de-fra")));
        Assert.True(RemoteTorrentEngine.IsReportableVpnChange(Vpn(pending: "de-fra"), Vpn(profile: "de-fra")));
        Assert.True(RemoteTorrentEngine.IsReportableVpnChange(Vpn(), Vpn(error: "openvpn exited: AUTH_FAILED")));
    }

    // ---- VPN profiles over the wire ----

    private sealed class StubHandler(HttpStatusCode status, string? body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private static RemoteTorrentEngine Engine(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://engine.local/") },
            new MediaServerSettings(),
            NullLogger<RemoteTorrentEngine>.Instance);

    [Fact]
    public async Task GetVpnProfilesAsync_ParsesTheEngineList()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"active":"nl-ams","profiles":[{"id":"de-fra","remote":"de.example:1194"},{"id":"nl-ams","remote":null}]}""");
        using var engine = Engine(handler);

        var profiles = await engine.GetVpnProfilesAsync(CancellationToken.None);

        Assert.Equal("/vpn/profiles", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("nl-ams", profiles!.Active);
        Assert.Equal(["de-fra", "nl-ams"], profiles.Profiles.Select(profile => profile.Id).ToArray());
        Assert.Equal("de.example:1194", profiles.Profiles[0].Remote);
        Assert.Null(profiles.Profiles[1].Remote);
    }

    [Fact]
    public async Task GetVpnProfilesAsync_EngineWithoutTheRoute_IsNull()
    {
        // torrent-engine older than 0.8.0: nothing to pick from, not an error — the picker stays an indicator.
        using var engine = Engine(new StubHandler(HttpStatusCode.NotFound, null));

        Assert.Null(await engine.GetVpnProfilesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SelectVpnProfileAsync_SendsTheIdAsJson_AndReturnsTheCurrentStatus()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted,
            """{"connected":true,"tunnelInterface":"tun0","tunnelAddress":"10.8.0.2","exitIp":"203.0.113.7","exitCountry":"NL","checkedAt":"2026-09-03T10:00:00Z","profile":"nl-ams","pendingProfile":null,"lastError":null}""");
        using var engine = Engine(handler);

        var status = await engine.SelectVpnProfileAsync("de-fra", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("/vpn/profile", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"id\":\"de-fra\"", handler.RequestBody);
        // The engine answers with what runs *now*; the switch itself arrives later over the event stream.
        Assert.Equal("nl-ams", status.Profile);
        Assert.Equal("nl-ams", engine.GetVpnStatus()!.Profile);
    }

    [Fact]
    public async Task SelectVpnProfileAsync_UnknownProfile_RelaysTheEngineMessage()
    {
        using var engine = Engine(new StubHandler(HttpStatusCode.NotFound,
            """{"error":"No VPN profile 'zz' (configured: de-fra, nl-ams)."}"""));

        var exception = await Assert.ThrowsAsync<EngineRequestException>(() => engine.SelectVpnProfileAsync("zz", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains("de-fra, nl-ams", exception.Message);
    }

    [Fact]
    public async Task SelectVpnProfileAsync_EngineWithoutTheRoute_ExplainsTheVersionGap()
    {
        // A bare 404 (no engine error body) is the route missing, which must not read as "profile not found".
        using var engine = Engine(new StubHandler(HttpStatusCode.NotFound, null));

        var exception = await Assert.ThrowsAsync<EngineRequestException>(() => engine.SelectVpnProfileAsync("nl-ams", CancellationToken.None));

        Assert.Contains("0.8.0", exception.Message);
    }
}
