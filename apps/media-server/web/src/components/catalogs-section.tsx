"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { mediaServer, type CatalogType } from "@/lib/media-server";
import { formatBytes } from "@/lib/format";
import { inputClass, errorMessage } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { QueryState } from "@/components/states";

const CATALOG_TYPES: CatalogType[] = ["Movie", "Series", "Anime"];

export function CatalogsSection() {
  const queryClient = useQueryClient();
  const catalogs = useQuery({ queryKey: ["catalogs"], queryFn: mediaServer.listCatalogs });
  const [name, setName] = useState("");
  const [type, setType] = useState<CatalogType>("Movie");
  const [root, setRoot] = useState("");
  const [defaultKeepSeeding, setDefaultKeepSeeding] = useState(false);

  const create = useMutation({
    mutationFn: () => mediaServer.createCatalog({ name, type, root, defaultKeepSeeding }),
    onSuccess: () => {
      setName("");
      setRoot("");
      queryClient.invalidateQueries({ queryKey: ["catalogs"] });
    },
  });

  const remove = useMutation({
    mutationFn: (id: string) => mediaServer.deleteCatalog(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["catalogs"] }),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Catalogs</CardTitle>
        <CardDescription>Destinations on one filesystem; each holds files/ and library/.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4 text-sm">
        <QueryState query={catalogs} empty="No catalogs yet. Add one to start.">
          {(items) => (
            <ul className="flex flex-col gap-2">
              {items.map((catalog) => (
                <li key={catalog.id} className="flex items-center justify-between gap-3 rounded-md border p-2">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="font-medium">{catalog.name}</span>
                      <Badge variant="secondary">{catalog.type}</Badge>
                      {!catalog.online && <Badge variant="destructive">offline</Badge>}
                    </div>
                    <p className="text-muted-foreground truncate">{catalog.root}</p>
                  </div>
                  <div className="flex shrink-0 items-center gap-3">
                    <span className="text-muted-foreground">{formatBytes(catalog.freeBytes)} free</span>
                    <Button variant="ghost" size="sm" onClick={() => remove.mutate(catalog.id)}>
                      Remove
                    </Button>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </QueryState>

        <form
          className="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_8rem_2fr_auto]"
          onSubmit={(event) => {
            event.preventDefault();
            create.mutate();
          }}
        >
          <input className={inputClass} placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
          <select className={inputClass} value={type} onChange={(e) => setType(e.target.value as CatalogType)}>
            {CATALOG_TYPES.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
          <input className={inputClass} placeholder="/mnt/media/movies" value={root} onChange={(e) => setRoot(e.target.value)} required />
          <Button type="submit" disabled={create.isPending}>
            {create.isPending ? "Adding…" : "Add catalog"}
          </Button>
          <label className="text-muted-foreground flex items-center gap-2 sm:col-span-4">
            <input type="checkbox" checked={defaultKeepSeeding} onChange={(e) => setDefaultKeepSeeding(e.target.checked)} />
            Keep seeding by default
          </label>
        </form>
        {create.isError && <p className="text-destructive">{errorMessage(create.error)}</p>}
      </CardContent>
    </Card>
  );
}
