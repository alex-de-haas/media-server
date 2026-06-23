"use client";

import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { KeyRound } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer } from "@/lib/media-server";
import { errorMessage } from "@/lib/ui";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";

export function InfuseAccessSection() {
  const queryClient = useQueryClient();
  const credential = useQuery({ queryKey: ["jellyfin-credential"], queryFn: mediaServer.getJellyfinCredential });
  const pinId = useId();
  const [pin, setPin] = useState("");
  const [secret, setSecret] = useState<{ username: string; pin: string | null; serverUrl: string | null } | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["jellyfin-credential"] });
  const create = useMutation({
    mutationFn: () => mediaServer.createJellyfinCredential(pin),
    onSuccess: (result) => {
      setSecret(result);
      setPin("");
      invalidate();
      toast.success("Credential created");
    },
    onError: (error) => toast.error("Couldn’t create credential", { description: errorMessage(error) }),
  });
  const revoke = useMutation({
    mutationFn: () => mediaServer.revokeJellyfinCredential(),
    onSuccess: () => {
      setSecret(null);
      invalidate();
      toast.success("Credential revoked");
    },
    onError: (error) => toast.error("Couldn’t revoke credential", { description: errorMessage(error) }),
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
            <Badge variant="secondary">PIN set</Badge>
            {status.locked && <Badge variant="destructive">temporarily locked</Badge>}
            {status.permanentlyLocked && <Badge variant="destructive">locked — regenerate</Badge>}
          </div>
        ) : (
          <p className="text-muted-foreground">No Infuse credential yet.</p>
        )}

        {secret && (
          <Alert>
            <KeyRound />
            <AlertTitle>Save this — it is shown only once.</AlertTitle>
            <AlertDescription className="flex flex-col gap-0.5">
              <span>
                Username: <span className="text-foreground font-mono">{secret.username}</span>
              </span>
              {secret.pin && (
                <span>
                  PIN: <span className="text-foreground font-mono text-base">{secret.pin}</span>
                </span>
              )}
            </AlertDescription>
          </Alert>
        )}

        <div className="flex flex-wrap items-end gap-2">
          <Field className="w-40">
            <FieldLabel htmlFor={pinId}>PIN (optional, 6–8 digits)</FieldLabel>
            <Input
              id={pinId}
              inputMode="numeric"
              placeholder="auto-generate"
              value={pin}
              onChange={(e) => setPin(e.target.value.replace(/[^0-9]/g, "").slice(0, 8))}
            />
          </Field>
          <Button onClick={() => create.mutate()} disabled={create.isPending}>
            {create.isPending ? "Saving…" : status?.hasCredential ? "Regenerate" : "Create credential"}
          </Button>
          {status?.hasCredential && (
            <Button variant="ghost" onClick={() => revoke.mutate()} disabled={revoke.isPending}>
              Revoke
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
