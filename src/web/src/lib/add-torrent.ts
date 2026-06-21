import type { AddTorrentInput } from "@/lib/media-server";

export interface TorrentFile {
  name: string;
  size: number;
  base64: string;
}

export interface AddTorrentTask {
  /** Human-readable source name, used in the partial-success summary toast. */
  label: string;
  input: AddTorrentInput;
}

/**
 * Expands the dialog's selection into one add request per source: a single magnet link (if present)
 * plus each selected .torrent file. The backend accepts exactly one source per request, so files cannot
 * be folded into a single call — they are added sequentially, all into the same catalog.
 */
export function buildAddTorrentTasks(options: {
  catalogId: string;
  magnet?: string;
  files: readonly TorrentFile[];
  keepSeeding: boolean;
}): AddTorrentTask[] {
  const { catalogId, magnet, files, keepSeeding } = options;
  const tasks: AddTorrentTask[] = [];

  const trimmedMagnet = magnet?.trim();
  if (trimmedMagnet) {
    tasks.push({ label: "magnet link", input: { catalogId, magnet: trimmedMagnet, keepSeeding } });
  }

  for (const file of files) {
    tasks.push({ label: file.name, input: { catalogId, torrentFileBase64: file.base64, keepSeeding } });
  }

  return tasks;
}
