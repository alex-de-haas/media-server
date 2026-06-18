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
