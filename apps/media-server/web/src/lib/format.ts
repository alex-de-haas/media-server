export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || bytes <= 0) {
    return "0 B";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, exponent);
  return `${value.toFixed(value >= 10 || exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}

export function formatSpeed(bytesPerSecond: number | null | undefined): string {
  if (!bytesPerSecond) {
    return "—";
  }
  return `${formatBytes(bytesPerSecond)}/s`;
}

export function formatPercent(value: number | null | undefined): string {
  return value == null ? "—" : `${value.toFixed(1)}%`;
}

// .NET runtime ticks (100ns units) → "1h 56m" / "42m". Null when unknown.
export function formatRuntime(ticks: number | null | undefined): string | null {
  if (!ticks || ticks <= 0) {
    return null;
  }
  const totalMinutes = Math.round(ticks / 600_000_000);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
}

// ISO timestamp → coarse "just now" / "5m ago" / "3h ago" / "2d ago". Null when unparseable.
export function formatTimeAgo(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) {
    return null;
  }
  const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
  if (seconds < 45) {
    return "just now";
  }
  // Below 60s we already returned "just now"; clamp so 45–59s reads "1m ago", never "0m ago".
  const minutes = Math.max(1, Math.floor(seconds / 60));
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }
  return `${Math.floor(hours / 24)}d ago`;
}

export function formatEta(seconds: number | null | undefined): string {
  if (seconds == null || seconds <= 0 || !Number.isFinite(seconds)) {
    return "—";
  }
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = Math.floor(seconds % 60);
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  if (minutes > 0) {
    return `${minutes}m ${secs}s`;
  }
  return `${secs}s`;
}
