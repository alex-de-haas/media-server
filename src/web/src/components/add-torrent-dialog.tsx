"use client";

import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, X } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer } from "@/lib/media-server";
import { buildAddTorrentTasks, type TorrentFile } from "@/lib/add-torrent";
import { formatBytes } from "@/lib/format";
import { errorMessage } from "@/lib/ui";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

export function AddTorrentDialog() {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const catalogSelectId = useId();
  const magnetId = useId();
  const filesId = useId();
  const [catalogId, setCatalogId] = useState("");
  const [magnet, setMagnet] = useState("");
  const [keepSeeding, setKeepSeeding] = useState(false);
  const [files, setFiles] = useState<TorrentFile[]>([]);

  const selectedCatalog = catalogs.data?.find((catalog) => catalog.id === catalogId);
  const sourceCount = (magnet.trim() ? 1 : 0) + files.length;

  // Each source is its own request (the backend takes one per call); run them sequentially so adds of the
  // same info hash don't race, and report a partial-success summary rather than failing the whole batch.
  const add = useMutation({
    mutationFn: async () => {
      const tasks = buildAddTorrentTasks({ catalogId, magnet, files, keepSeeding });
      let added = 0;
      const failures: string[] = [];
      for (const task of tasks) {
        try {
          await mediaServer.addTorrent(task.input);
          added += 1;
        } catch (error) {
          failures.push(`${task.label}: ${errorMessage(error)}`);
        }
      }
      return { added, failures };
    },
    onSuccess: ({ added, failures }) => {
      queryClient.invalidateQueries({ queryKey: ["downloads"] });
      queryClient.invalidateQueries({ queryKey: ["ingest"] });

      if (added > 0) {
        setMagnet("");
        setFiles([]);
        setOpen(false);
        const suffix = failures.length > 0 ? `, ${failures.length} failed` : "";
        toast.success(`Added ${added} torrent${added === 1 ? "" : "s"}${suffix}`);
      } else {
        toast.error("Couldn’t add torrent", { description: failures.join("\n") });
      }
    },
    onError: (error) => toast.error("Couldn’t add torrent", { description: errorMessage(error) }),
  });

  async function onFiles(fileList: FileList | null) {
    if (!fileList || fileList.length === 0) {
      return;
    }
    const picked = await Promise.all(
      Array.from(fileList).map(async (file) => ({ name: file.name, size: file.size, base64: await toBase64(file) })),
    );
    // Skip files already selected (same name + size) so re-picking doesn't duplicate them.
    setFiles((current) => {
      const seen = new Set(current.map((file) => `${file.name}:${file.size}`));
      return [...current, ...picked.filter((file) => !seen.has(`${file.name}:${file.size}`))];
    });
  }

  function removeFile(index: number) {
    setFiles((current) => current.filter((_, i) => i !== index));
  }

  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}>
        <Plus />
        Add torrent
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add torrent</DialogTitle>
            <DialogDescription>Magnet link or .torrent files, into a chosen catalog.</DialogDescription>
          </DialogHeader>

          <form
            className="flex flex-col gap-3 text-sm"
            onSubmit={(e) => {
              e.preventDefault();
              if (catalogId && sourceCount > 0) add.mutate();
            }}
          >
            <Field>
              <FieldLabel htmlFor={catalogSelectId}>Catalog</FieldLabel>
              <div className="flex flex-wrap items-center gap-3">
                <Select
                  value={catalogId || null}
                  onValueChange={(value) => setCatalogId((value as string | null) ?? "")}
                  items={(catalogs.data ?? []).map((catalog) => ({ value: catalog.id, label: `${catalog.name} (${catalog.type})` }))}
                >
                  <SelectTrigger id={catalogSelectId} className="w-full max-w-xs">
                    <SelectValue placeholder="Select a catalog…" />
                  </SelectTrigger>
                  <SelectContent>
                    {catalogs.data?.map((catalog) => (
                      <SelectItem key={catalog.id} value={catalog.id}>
                        {catalog.name} ({catalog.type})
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {selectedCatalog && (
                  <span className="text-muted-foreground text-xs">{formatBytes(selectedCatalog.freeBytes)} free</span>
                )}
              </div>
            </Field>

            <Field>
              <FieldLabel htmlFor={magnetId}>Magnet link</FieldLabel>
              <Input
                id={magnetId}
                placeholder="magnet:?xt=urn:btih:…"
                value={magnet}
                onChange={(e) => setMagnet(e.target.value)}
              />
            </Field>

            <Field>
              <FieldLabel htmlFor={filesId}>…or one or more .torrent files</FieldLabel>
              <input
                id={filesId}
                type="file"
                accept=".torrent"
                multiple
                onChange={(e) => {
                  void onFiles(e.target.files);
                  e.target.value = ""; // allow re-picking a file after it was removed
                }}
                className="text-muted-foreground file:bg-secondary file:text-secondary-foreground file:mr-3 file:rounded-md file:border-0 file:px-2.5 file:py-1 file:text-xs text-xs"
              />
            </Field>

            {files.length > 0 && (
              <ul className="flex flex-col gap-1">
                {files.map((file, index) => (
                  <li
                    key={`${file.name}:${file.size}`}
                    className="flex items-center justify-between gap-2 rounded-md border px-2 py-1 text-xs"
                  >
                    <span className="truncate" title={file.name}>
                      {file.name}
                    </span>
                    <div className="flex shrink-0 items-center gap-2">
                      <span className="text-muted-foreground">{formatBytes(file.size)}</span>
                      <button
                        type="button"
                        aria-label={`Remove ${file.name}`}
                        className="text-muted-foreground hover:text-foreground rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        onClick={() => removeFile(index)}
                      >
                        <X className="size-3.5" />
                      </button>
                    </div>
                  </li>
                ))}
              </ul>
            )}

            <label className="flex items-center gap-2">
              <Checkbox checked={keepSeeding} onCheckedChange={(checked) => setKeepSeeding(checked === true)} />
              <span>Keep seeding after download completes</span>
            </label>

            <DialogFooter className="mt-2">
              <Button type="button" variant="ghost" size="sm" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" size="sm" disabled={add.isPending || !catalogId || sourceCount === 0}>
                {add.isPending ? "Adding…" : sourceCount > 1 ? `Add ${sourceCount} torrents` : "Add torrent"}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}

// FileReader does the base64 encoding natively (non-blocking), unlike a per-character JS loop which
// freezes the UI on larger .torrent files. readAsDataURL yields "data:...;base64,XXXX" — strip the prefix.
function toBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      resolve(result.slice(result.indexOf(",") + 1));
    };
    reader.onerror = () => reject(reader.error ?? new Error("Failed to read file"));
    reader.readAsDataURL(file);
  });
}
