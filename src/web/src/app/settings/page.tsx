import { InfuseAccessSection } from "@/components/infuse-access-section";
import { ReleaseGroupSettingsSection } from "@/components/release-group-settings-section";
import { WatchHistorySection } from "@/components/watch-history-section";

export default function SettingsPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
      <div className="flex flex-col gap-6">
        <ReleaseGroupSettingsSection />
        <InfuseAccessSection />
        <WatchHistorySection />
      </div>
    </>
  );
}
