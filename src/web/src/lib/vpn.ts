import type { VpnStatus } from "@/lib/media-server";

// How the Activity header presents the tunnel: up, down, or mid-switch. A switch is the engine bringing up
// another OpenVPN profile — the tunnel dips on the way and the engine pauses transfers meanwhile, so it
// deserves its own presentation rather than reading as an outage.
export type VpnKind = "up" | "down" | "switching";

export function vpnKind(status: VpnStatus): VpnKind {
  // Nullable, not falsy: the engine sends null for "no switch in flight", and the picker keys off the same test.
  if (status.pendingProfile != null) return "switching";
  return status.connected ? "up" : "down";
}

// Pill text. The profile id names *which* tunnel and the exit country says where it comes out; once several
// profiles exist either alone is ambiguous, so both are shown when known. An engine without profiles keeps
// the older `VPN · NL` reading.
export function vpnLabel(status: VpnStatus): string {
  switch (vpnKind(status)) {
    case "switching":
      return "VPN · switching…";
    case "down":
      return "VPN off";
    case "up": {
      const parts = [status.profile, status.exitCountry ?? status.exitIp].filter(Boolean);
      return parts.length > 0 ? `VPN · ${parts.join(" · ")}` : "VPN";
    }
  }
}

// Tooltip / menu header: the same story in a sentence, plus the engine's last error when it has one. An error
// is kept even while the tunnel is up — it explains why *this* profile runs (the engine fell back to it).
export function vpnTooltip(status: VpnStatus): string {
  const lines: string[] = [];
  switch (vpnKind(status)) {
    case "switching":
      lines.push(`Switching to profile ${status.pendingProfile} — transfers pause until the new tunnel is up.`);
      break;
    case "down":
      lines.push("VPN tunnel is down — torrent traffic is blocked by the killswitch.");
      break;
    case "up": {
      // exitIp and exitCountry can be independently null, so surface whichever is known.
      const exitValue = [status.exitIp, status.exitCountry].filter(Boolean).join(" · ");
      const parts = [
        status.profile ? `profile ${status.profile}` : null,
        exitValue ? `exit ${exitValue}` : null,
        status.tunnelAddress ? `tunnel ${status.tunnelAddress}` : null,
      ].filter(Boolean);
      lines.push(parts.length > 0 ? `Traffic egresses through the VPN — ${parts.join(", ")}.` : "VPN tunnel is up.");
      break;
    }
  }
  if (status.lastError) {
    lines.push(`Last error: ${status.lastError}`);
  }
  return lines.join(" ");
}
