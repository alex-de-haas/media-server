import { InfuseAccessSection } from "@/components/infuse-access-section";
import { LibraryHealthSection } from "@/components/library-health-section";
import { MediaBackfillSection } from "@/components/media-backfill-section";
import { ReleaseGroupSettingsSection } from "@/components/release-group-settings-section";
import { RemovedTitlesSection } from "@/components/removed-titles-section";
import { WatchHistorySection } from "@/components/watch-history-section";

export default function SettingsPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
      <div className="flex flex-col gap-6">
        <ReleaseGroupSettingsSection />
        <InfuseAccessSection />
        <WatchHistorySection />
        <LibraryHealthSection />
        <MediaBackfillSection />
        <RemovedTitlesSection />
      </div>
    </>
  );
}
