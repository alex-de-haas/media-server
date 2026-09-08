import { Suspense } from "react";
import { SettingsTabs } from "@/components/settings-tabs";

export default function SettingsPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
      <Suspense fallback={<p className="text-muted-foreground text-sm">Loading settings…</p>}>
        <SettingsTabs />
      </Suspense>
    </>
  );
}
