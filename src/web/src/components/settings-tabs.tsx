"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useSession } from "@/components/app-shell";
import { TemporaryDownloadsSection } from "@/components/temporary-downloads-section";
import { GroupsSection } from "@/components/groups-section";
import { CatalogsSection } from "@/components/catalogs-section";
import { InfuseAccessSection } from "@/components/infuse-access-section";
import { ReleaseGroupSettingsSection } from "@/components/release-group-settings-section";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

export function SettingsTabs() {
  const { role } = useSession();
  const router = useRouter();
  const searchParams = useSearchParams();
  const isAdmin = role === "admin";
  const requested = searchParams.get("tab");
  const tab = isAdmin && (requested === "catalogs" || requested === "groups") ? requested : "general";

  return (
    <Tabs value={tab} onValueChange={(value) => {
      const params = new URLSearchParams(searchParams.toString());
      if (value === "catalogs" || value === "groups") params.set("tab", value);
      else params.delete("tab");
      const query = params.toString();
      router.push(query ? `/settings?${query}` : "/settings", { scroll: false });
    }} className="gap-6">
      <TabsList aria-label="Settings sections">
        <TabsTrigger value="general">General</TabsTrigger>
        {isAdmin && <TabsTrigger value="catalogs">Catalogs</TabsTrigger>}
        {isAdmin && <TabsTrigger value="groups">Groups</TabsTrigger>}
      </TabsList>
      <TabsContent value="general" className="flex flex-col gap-6">
        <ReleaseGroupSettingsSection />
        <InfuseAccessSection />
        {isAdmin && <TemporaryDownloadsSection />}
      </TabsContent>
      {isAdmin && <TabsContent value="catalogs"><CatalogsSection /></TabsContent>}
      {isAdmin && <TabsContent value="groups"><GroupsSection /></TabsContent>}
    </Tabs>
  );
}
