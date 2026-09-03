import { describe, expect, it } from "vitest";
import { vpnKind, vpnLabel, vpnTooltip } from "@/lib/vpn";
import type { VpnStatus } from "@/lib/media-server";

function status(overrides: Partial<VpnStatus> = {}): VpnStatus {
  return {
    connected: true,
    tunnelInterface: "tun0",
    tunnelAddress: "10.8.0.2",
    exitIp: "203.0.113.7",
    exitCountry: "NL",
    checkedAt: "2026-09-03T10:00:00Z",
    profile: "nl-ams",
    pendingProfile: null,
    lastError: null,
    ...overrides,
  };
}

describe("vpnKind", () => {
  it("reports a pending profile as switching, whatever the tunnel is doing", () => {
    // Mid-switch the tunnel is briefly down; that is progress, not an outage.
    expect(vpnKind(status({ pendingProfile: "de-fra", connected: false }))).toBe("switching");
    expect(vpnKind(status({ pendingProfile: "de-fra" }))).toBe("switching");
  });

  it("otherwise follows connectivity", () => {
    expect(vpnKind(status())).toBe("up");
    expect(vpnKind(status({ connected: false }))).toBe("down");
  });
});

describe("vpnLabel", () => {
  it("names the profile and the exit country while up", () => {
    expect(vpnLabel(status())).toBe("VPN · nl-ams · NL");
  });

  it("keeps the older reading against an engine without profiles", () => {
    expect(vpnLabel(status({ profile: null }))).toBe("VPN · NL");
    expect(vpnLabel(status({ profile: null, exitCountry: null }))).toBe("VPN · 203.0.113.7");
    expect(vpnLabel(status({ profile: null, exitCountry: null, exitIp: null }))).toBe("VPN");
  });

  it("shows the profile alone before the exit check has answered", () => {
    expect(vpnLabel(status({ exitCountry: null, exitIp: null }))).toBe("VPN · nl-ams");
  });

  it("says off while down and switching while a switch is pending", () => {
    expect(vpnLabel(status({ connected: false }))).toBe("VPN off");
    expect(vpnLabel(status({ pendingProfile: "de-fra" }))).toBe("VPN · switching…");
  });
});

describe("vpnTooltip", () => {
  it("describes the egress with profile, exit and tunnel", () => {
    expect(vpnTooltip(status())).toBe(
      "Traffic egresses through the VPN — profile nl-ams, exit 203.0.113.7 · NL, tunnel 10.8.0.2.",
    );
  });

  it("falls back to a bare up message when nothing else is known", () => {
    expect(vpnTooltip(status({ profile: null, exitIp: null, exitCountry: null, tunnelAddress: null }))).toBe("VPN tunnel is up.");
  });

  it("names the profile a switch is moving to", () => {
    expect(vpnTooltip(status({ pendingProfile: "de-fra", connected: false }))).toBe(
      "Switching to profile de-fra — transfers pause until the new tunnel is up.",
    );
  });

  it("explains a down tunnel and appends the engine's last error", () => {
    expect(vpnTooltip(status({ connected: false, lastError: "openvpn exited: AUTH_FAILED" }))).toBe(
      "VPN tunnel is down — torrent traffic is blocked by the killswitch. Last error: openvpn exited: AUTH_FAILED",
    );
  });

  it("keeps the last error even while up, since it explains why this profile runs", () => {
    expect(vpnTooltip(status({ lastError: "selected profile 'zz' is not in the profiles folder" }))).toContain(
      "Last error: selected profile 'zz'",
    );
  });
});
