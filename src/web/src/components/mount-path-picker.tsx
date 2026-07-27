"use client";

import { useId } from "react";
import type { CatalogMount } from "@/lib/media-server";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

// Joins a mount base with the operator-typed sub-path, for the preview of where the catalog will live.
// Preserves the base (incl. a root like "/") when no sub-path is given, rather than trimming it away.
export function joinRoot(base: string, relative: string) {
  const cleaned = normalizeRelativePath(relative);
  if (!cleaned) return base;
  const baseWithSeparator = base.endsWith("/") || base.endsWith("\\") ? base : `${base}/`;
  return `${baseWithSeparator}${cleaned}`;
}

// Mirrors CatalogRootResolver.Normalize on the API side: forward slashes, no leading/trailing separator,
// "." and ".." resolved away. A path that climbs out of the mount reduces to "" here and is rejected by
// the API, so the preview never shows a location the server would refuse to store.
export function normalizeRelativePath(relative: string) {
  const segments: string[] = [];
  for (const segment of relative.trim().split(/[\\/]+/)) {
    if (segment === "" || segment === ".") continue;
    if (segment === "..") segments.pop();
    else segments.push(segment);
  }
  return segments.join("/");
}

// The label to actually submit: the picker falls back to the first configured mount for display, so an
// unavailable stored label (its mount was removed or renamed) must not be what gets sent.
export function effectiveMountLabel(mounts: CatalogMount[], label: string) {
  return mounts.find((mount) => mount.label === label)?.label ?? mounts[0]?.label ?? "";
}

/**
 * Picks where a catalog lives: which Hosty catalog-root mount, plus the path within it. The mount
 * **label** is what gets stored — the absolute path a mount has differs between the dev and docker
 * runtimes, the label does not. Shared by the add and re-anchor dialogs.
 */
export function MountPathPicker({
  mounts,
  mountLabel,
  onMountLabelChange,
  relativePath,
  onRelativePathChange,
}: {
  mounts: CatalogMount[];
  mountLabel: string;
  onMountLabelChange: (label: string) => void;
  relativePath: string;
  onRelativePathChange: (path: string) => void;
}) {
  const mountId = useId();
  const relativePathId = useId();
  const selected = mounts.find((mount) => mount.label === effectiveMountLabel(mounts, mountLabel));

  return (
    <>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field>
          <FieldLabel htmlFor={mountId}>Mount</FieldLabel>
          <Select
            value={selected?.label ?? ""}
            onValueChange={(value) => onMountLabelChange((value as string | null) ?? "")}
            items={mounts.map((mount) => ({ value: mount.label, label: mount.label }))}
          >
            <SelectTrigger id={mountId} className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {mounts.map((mount) => (
                <SelectItem key={mount.label} value={mount.label}>
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
            onChange={(event) => onRelativePathChange(event.target.value)}
          />
        </Field>
      </div>
      <p className="text-muted-foreground text-xs">
        Catalog root in this runtime:{" "}
        <span className="text-foreground font-mono break-all">{joinRoot(selected?.path ?? "", relativePath)}</span>
      </p>
    </>
  );
}
