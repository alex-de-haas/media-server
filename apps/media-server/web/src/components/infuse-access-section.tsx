"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { mediaServer } from "@/lib/media-server";
import { inputClass, errorMessage } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function InfuseAccessSection() {
  const queryClient = useQueryClient();
  const credential = useQuery({ queryKey: ["jellyfin-credential"], queryFn: mediaServer.getJellyfinCredential });
  const [pin, setPin] = useState("");
  const [secret, setSecret] = useState<{ username: string; pin: string | null; serverUrl: string | null } | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["jellyfin-credential"] });
  const create = useMutation({
    mutationFn: () => mediaServer.createJellyfinCredential(pin),
    onSuccess: (result) => {
      setSecret(result);
      setPin("");
      invalidate();
    },
  });
  const revoke = useMutation({
    mutationFn: () => mediaServer.revokeJellyfinCredential(),
    onSuccess: () => {
      setSecret(null);
      invalidate();
    },
  });

  const status = credential.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Infuse access</CardTitle>
        <CardDescription>
          Create a username + PIN to sign in from a Jellyfin client (e.g. Infuse). The PIN is shown once.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        {status?.serverUrl ? (
          <p className="text-muted-foreground">
            Server URL: <span className="font-mono break-all">{status.serverUrl}</span>
          </p>
        ) : (
          <p className="text-muted-foreground">
            No public server URL configured yet — set up the Jellyfin ingress to connect external clients.
          </p>
        )}

        {status?.hasCredential ? (
          <div className="flex flex-wrap items-center gap-2">
            <span>
              Signed in as <span className="font-medium">{status.username}</span>
            </span>
            {status.locked && <Badge variant="destructive">temporarily locked</Badge>}
            {status.permanentlyLocked && <Badge variant="destructive">locked — regenerate</Badge>}
          </div>
        ) : (
          <p className="text-muted-foreground">No Infuse credential yet.</p>
        )}

        {secret && (
          <div className="rounded-md border border-dashed p-3">
            <p className="font-medium">Save this — it is shown only once.</p>
            <p>
              Username: <span className="font-mono">{secret.username}</span>
            </p>
            {secret.pin && (
              <p>
                PIN: <span className="font-mono text-base">{secret.pin}</span>
              </p>
            )}
          </div>
        )}

        <div className="flex flex-wrap items-end gap-2">
          <label className="flex flex-col gap-1">
            <span className="text-muted-foreground text-xs">PIN (optional, 6–8 digits)</span>
            <input
              className={`${inputClass} w-40`}
              inputMode="numeric"
              placeholder="auto-generate"
              value={pin}
              onChange={(e) => setPin(e.target.value.replace(/[^0-9]/g, "").slice(0, 8))}
            />
          </label>
          <Button onClick={() => create.mutate()} disabled={create.isPending}>
            {create.isPending ? "Saving…" : status?.hasCredential ? "Regenerate" : "Create credential"}
          </Button>
          {status?.hasCredential && (
            <Button variant="ghost" onClick={() => revoke.mutate()} disabled={revoke.isPending}>
              Revoke
            </Button>
          )}
        </div>
        {create.isError && <p className="text-destructive">{errorMessage(create.error)}</p>}
      </CardContent>
    </Card>
  );
}
