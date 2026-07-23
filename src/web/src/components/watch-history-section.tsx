"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { History, RefreshCw } from "lucide-react";
import { toast } from "@/lib/toast";
import { mediaServer, type WatchHistoryProvider } from "@/lib/media-server";
import { connectionBadge } from "@/lib/watch-history";
import { errorMessage } from "@/lib/ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { WatchHistoryConnectDialog } from "@/components/watch-history-connect-dialog";
import { WatchHistorySyncDialog } from "@/components/watch-history-sync-dialog";

const PROVIDERS_KEY = ["watch-history-providers"] as const;

/** Absolute day + time, or a dash when the timestamp is absent. */
function when(value: string | null): string {
  return value ? new Date(value).toLocaleString() : "—";
}

export function WatchHistorySection() {
  const providers = useQuery({ queryKey: PROVIDERS_KEY, queryFn: mediaServer.listWatchHistoryProviders });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Watch history providers</CardTitle>
        <CardDescription>
          Keep an external service in step with what you’ve watched here. Each account links to your
          own login only — no one else’s.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4 text-sm">
        {providers.isPending && <Skeleton className="h-16 w-full" />}
        {providers.isError && (
          <p className="text-destructive">Couldn’t load providers: {errorMessage(providers.error)}</p>
        )}
        {providers.data?.length === 0 && (
          <p className="text-muted-foreground">No watch history providers are available.</p>
        )}
        {providers.data?.map((provider, index) => (
          <div key={provider.key} className="flex flex-col gap-4">
            {index > 0 && <Separator />}
            <ProviderRow provider={provider} />
          </div>
        ))}
      </CardContent>
    </Card>
  );
}

function ProviderRow({ provider }: { provider: WatchHistoryProvider }) {
  const queryClient = useQueryClient();
  const [connectOpen, setConnectOpen] = useState(false);
  const [syncOpen, setSyncOpen] = useState(false);

  const refresh = () => queryClient.invalidateQueries({ queryKey: PROVIDERS_KEY });

  const disconnect = useMutation({
    mutationFn: () => mediaServer.disconnectWatchHistoryProvider(provider.key),
    onSuccess: () => {
      refresh();
      toast.success(`Disconnected ${provider.displayName}`);
    },
    onError: (error) => toast.error("Couldn’t disconnect", { description: errorMessage(error) }),
  });

  const connection = provider.connection;
  const badge = connectionBadge(connection);
  const needsReconnect = badge === "reconnect";

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <History className="size-4 text-muted-foreground" />
          <span className="font-medium">{provider.displayName}</span>
          {badge === "reconnect" && <Badge variant="destructive">Reconnect needed</Badge>}
          {badge === "connected" && <Badge variant="secondary">Connected</Badge>}
          {badge === "none" && <Badge variant="outline">Not connected</Badge>}
        </div>
      </div>

      {!provider.isConfigured ? (
        <p className="text-muted-foreground">
          An administrator hasn’t set up {provider.displayName} for this instance yet, so it can’t be
          connected here.
        </p>
      ) : connection ? (
        <div className="flex flex-col gap-1 text-muted-foreground">
          {connection.accountName && (
            <span>
              Signed in as <span className="text-foreground font-medium">{connection.accountName}</span>
            </span>
          )}
          <span>Last sync: {when(connection.lastSyncAt)}</span>
          {/* Delivery health is distinct from an explicit sync — a change can reach the provider in
              the background without a sync ever having run. */}
          <span>Last delivery: {when(connection.lastDeliveryAt)}</span>
          {needsReconnect && connection.lastError && (
            <span className="text-destructive">{connection.lastError}</span>
          )}
        </div>
      ) : (
        <p className="text-muted-foreground">
          Connect your {provider.displayName} account to sync watched state. Keep {provider.displayName}{" "}
          enabled in only one media app to avoid two systems fighting over the same history.
        </p>
      )}

      {provider.isConfigured && (
        <div className="flex flex-wrap gap-2">
          {connection && !needsReconnect && (
            <Button size="sm" onClick={() => setSyncOpen(true)}>
              <RefreshCw /> Sync with {provider.displayName}
            </Button>
          )}
          {!connection && (
            <Button size="sm" onClick={() => setConnectOpen(true)}>
              Connect
            </Button>
          )}
          {needsReconnect && (
            <Button size="sm" onClick={() => setConnectOpen(true)}>
              Reconnect
            </Button>
          )}
          {connection && (
            <Button variant="ghost" size="sm" onClick={() => disconnect.mutate()} disabled={disconnect.isPending}>
              Disconnect
            </Button>
          )}
        </div>
      )}

      <WatchHistoryConnectDialog
        providerKey={provider.key}
        providerName={provider.displayName}
        open={connectOpen}
        onOpenChange={setConnectOpen}
        onConnected={() => {
          refresh();
          toast.success(`Connected ${provider.displayName}`);
        }}
      />
      <WatchHistorySyncDialog
        providerKey={provider.key}
        providerName={provider.displayName}
        open={syncOpen}
        onOpenChange={setSyncOpen}
        onApplied={refresh}
      />
    </div>
  );
}
