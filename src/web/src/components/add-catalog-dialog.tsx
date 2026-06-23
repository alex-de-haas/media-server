"use client";

import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type CatalogType } from "@/lib/media-server";
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

const CATALOG_TYPES: CatalogType[] = ["Movie", "Series", "Anime"];

// Joins a mount base with the operator-typed sub-path into the absolute root the backend validates.
// Preserves the base (incl. a root like "/") when no sub-path is given, rather than trimming it away.
function joinRoot(base: string, relative: string) {
  const cleaned = relative.trim().replace(/^[\\/]+|[\\/]+$/g, "");
  if (!cleaned) return base;
  const baseWithSeparator = base.endsWith("/") || base.endsWith("\\") ? base : `${base}/`;
  return `${baseWithSeparator}${cleaned}`;
}

export function AddCatalogDialog() {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const mounts = useQuery({ queryKey: ["catalog-mounts"], queryFn: mediaServer.listCatalogMounts });
  const nameId = useId();
  const typeId = useId();
  const mountId = useId();
  const relativePathId = useId();
  const freeRootId = useId();

  const [name, setName] = useState("");
  const [type, setType] = useState<CatalogType>("Movie");
  const [defaultKeepSeeding, setDefaultKeepSeeding] = useState(false);
  // Mount-relative entry (used when Hosty injects catalog-root mounts)…
  const [mountPath, setMountPath] = useState("");
  const [relativePath, setRelativePath] = useState("");
  // …or a free-text absolute root when no mounts are injected (standalone local runs).
  const [freeRoot, setFreeRoot] = useState("");

  const hasMounts = (mounts.data?.length ?? 0) > 0;
  const selectedMount = mountPath || mounts.data?.[0]?.path || "";
  const root = hasMounts ? joinRoot(selectedMount, relativePath) : freeRoot.trim();

  const create = useMutation({
    mutationFn: () => mediaServer.createCatalog({ name: name.trim(), type, root, defaultKeepSeeding }),
    onSuccess: () => {
      setName("");
      setRelativePath("");
      setFreeRoot("");
      setOpen(false);
      queryClient.invalidateQueries({ queryKey: ["catalogs"] });
      queryClient.invalidateQueries({ queryKey: ["catalog-usage"] });
      toast.success("Catalog added");
    },
    onError: (error) => toast.error("Couldn’t add catalog", { description: errorMessage(error) }),
  });

  const canSubmit = name.trim().length > 0 && root.length > 0 && !create.isPending;

  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}>
        <Plus />
        Add catalog
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add catalog</DialogTitle>
            <DialogDescription>A destination on one filesystem; it holds files/ and library/.</DialogDescription>
          </DialogHeader>

          <form
            className="flex flex-col gap-3 text-sm"
            onSubmit={(e) => {
              e.preventDefault();
              if (canSubmit) create.mutate();
            }}
          >
            <div className="grid gap-3 sm:grid-cols-[1fr_10rem]">
              <Field>
                <FieldLabel htmlFor={nameId}>Name</FieldLabel>
                <Input id={nameId} placeholder="Movies" value={name} onChange={(e) => setName(e.target.value)} required />
              </Field>
              <Field>
                <FieldLabel htmlFor={typeId}>Type</FieldLabel>
                <Select
                  value={type}
                  onValueChange={(value) => setType(value as CatalogType)}
                  items={CATALOG_TYPES.map((value) => ({ value, label: value }))}
                >
                  <SelectTrigger id={typeId} className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {CATALOG_TYPES.map((value) => (
                      <SelectItem key={value} value={value}>
                        {value}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
            </div>

            {hasMounts ? (
              <>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field>
                    <FieldLabel htmlFor={mountId}>Mount</FieldLabel>
                    <Select
                      value={selectedMount}
                      onValueChange={(value) => setMountPath((value as string | null) ?? "")}
                      items={(mounts.data ?? []).map((mount) => ({ value: mount.path, label: mount.label }))}
                    >
                      <SelectTrigger id={mountId} className="w-full">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {mounts.data?.map((mount) => (
                          <SelectItem key={mount.path} value={mount.path}>
                            {mount.label}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </Field>
                  <Field>
                    <FieldLabel htmlFor={relativePathId}>Path within mount</FieldLabel>
                    <Input
                      id={relativePathId}
                      placeholder="movies"
                      value={relativePath}
                      onChange={(e) => setRelativePath(e.target.value)}
                    />
                  </Field>
                </div>
                <p className="text-muted-foreground text-xs">
                  Catalog root: <span className="text-foreground font-mono break-all">{root}</span>
                </p>
              </>
            ) : (
              <Field>
                <FieldLabel htmlFor={freeRootId}>Catalog root (absolute path)</FieldLabel>
                <Input id={freeRootId} placeholder="/path/to/media/movies" value={freeRoot} onChange={(e) => setFreeRoot(e.target.value)} required />
              </Field>
            )}

            <label className="flex items-center gap-2">
              <Checkbox checked={defaultKeepSeeding} onCheckedChange={(checked) => setDefaultKeepSeeding(checked === true)} />
              <span>Keep seeding by default</span>
            </label>


            <DialogFooter className="mt-2">
              <Button type="button" variant="ghost" size="sm" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" size="sm" disabled={!canSubmit}>
                {create.isPending ? "Adding…" : "Add catalog"}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}
