"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { managementApi, managementHref } from "@/lib/management";
import { toast } from "@/lib/toast";
import { errorMessage } from "@/lib/ui";

/** Resolve a job's current owner only when followed: jobs may outlive a merge or deletion. */
export function ManagementJobLink({ id, sourceId }: { id: string; sourceId?: string }) {
  const router = useRouter();
  const [pending, setPending] = useState(false);
  return <Button size="sm" variant="ghost" disabled={pending} onClick={async () => {
    setPending(true);
    try {
      const item = await managementApi.location(id);
      const href = managementHref(item.kind, item.id, item.seriesId ?? undefined);
      router.push(sourceId ? `${href}${href.includes("?") ? "&" : "?"}source=${sourceId}#source-${sourceId}` : href);
    } catch (error) { toast.error("The item may have been removed or merged", { description: errorMessage(error) }); }
    finally { setPending(false); }
  }}>Manage</Button>;
}
