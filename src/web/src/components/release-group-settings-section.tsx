"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer } from "@/lib/media-server";
import { inputClass, errorMessage } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

/**
 * Manages the operator-editable list of release groups stripped from file names before identification.
 * Edits are local until saved; the whole list is sent back on Save (the backend normalizes + dedupes).
 */
export function ReleaseGroupSettingsSection() {
  const queryClient = useQueryClient();
  const settings = useQuery({ queryKey: ["app-settings"], queryFn: mediaServer.getSettings });

  // `edited` is null until the user touches the list; the displayed groups otherwise track the server
  // copy. This derives edit state from the query without a state-sync effect (which the linter forbids).
  const [edited, setEdited] = useState<string[] | null>(null);
  const [draft, setDraft] = useState("");

  const saved = settings.data?.customReleaseGroups ?? [];
  const groups = edited ?? saved;

  const save = useMutation({
    mutationFn: (next: string[]) => mediaServer.updateSettings({ customReleaseGroups: next }),
    onSuccess: (result) => {
      queryClient.setQueryData(["app-settings"], result);
      setEdited(null);
      toast.success("Release groups saved");
    },
    onError: (error) => toast.error("Couldn’t save release groups", { description: errorMessage(error) }),
  });

  const addDraft = () => {
    const value = draft.trim();
    setDraft("");
    if (!value || groups.some((group) => group.toLowerCase() === value.toLowerCase())) return;
    setEdited([...groups, value]);
  };

  const dirty = edited !== null && (groups.length !== saved.length || groups.some((group, index) => group !== saved[index]));

  return (
    <Card>
      <CardHeader>
        <CardTitle>Release group filtering</CardTitle>
        <CardDescription>
          Tokens removed from a file name before it’s identified — e.g. <span className="font-mono">LostFilm.TV</span>,{" "}
          <span className="font-mono">RARBG</span>. Matched as whole words, case-insensitive.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        <div className="flex flex-wrap gap-2">
          {groups.length ? (
            groups.map((group) => (
              <Badge key={group} variant="secondary" className="gap-1 font-mono">
                {group}
                <button
                  type="button"
                  aria-label={`Remove ${group}`}
                  onClick={() => setEdited(groups.filter((item) => item !== group))}
                  className="text-muted-foreground hover:text-foreground -mr-0.5 rounded-full"
                >
                  <X className="size-3" />
                </button>
              </Badge>
            ))
          ) : (
            <span className="text-muted-foreground">No release groups configured.</span>
          )}
        </div>

        <form
          className="flex gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            addDraft();
          }}
        >
          <input
            className={`${inputClass} h-8 flex-1`}
            placeholder="Add a group, e.g. RARBG"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
          />
          <Button type="submit" variant="secondary" size="sm" disabled={!draft.trim()}>
            Add
          </Button>
        </form>

        <div className="flex items-center gap-3">
          <Button size="sm" disabled={!dirty || save.isPending} onClick={() => save.mutate(groups)}>
            {save.isPending ? "Saving…" : "Save"}
          </Button>
          {dirty && !save.isPending && <span className="text-muted-foreground text-xs">Unsaved changes</span>}
        </div>
      </CardContent>
    </Card>
  );
}
