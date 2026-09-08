"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useSession } from "@/components/app-shell";
import { CatalogsSection } from "@/components/catalogs-section";
import { InfuseAccessSection } from "@/components/infuse-access-section";
import { ReleaseGroupSettingsSection } from "@/components/release-group-settings-section";
import { WatchHistorySection } from "@/components/watch-history-section";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

export function SettingsTabs() {
  const { role } = useSession();
  const router = useRouter();
  const searchParams = useSearchParams();
  const isAdmin = role === "admin";
  const tab = isAdmin && searchParams.get("tab") === "catalogs" ? "catalogs" : "general";

  return (
    <Tabs value={tab} onValueChange={(value) => {
      const params = new URLSearchParams(searchParams.toString());
      if (value === "catalogs") params.set("tab", "catalogs");
      else params.delete("tab");
      const query = params.toString();
      router.push(query ? `/settings?${query}` : "/settings", { scroll: false });
    }} className="gap-6">
      <TabsList aria-label="Settings sections">
        <TabsTrigger value="general">General</TabsTrigger>
        {isAdmin && <TabsTrigger value="catalogs">Catalogs</TabsTrigger>}
      </TabsList>
      <TabsContent value="general" className="flex flex-col gap-6">
        <ReleaseGroupSettingsSection />
        <InfuseAccessSection />
        <WatchHistorySection />
      </TabsContent>
      {isAdmin && <TabsContent value="catalogs"><CatalogsSection /></TabsContent>}
    </Tabs>
  );
}
