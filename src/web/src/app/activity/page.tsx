import { ActivitySection } from "@/components/activity-section";
import { TranscodeSection } from "@/components/transcode-section";

export default function ActivityPage() {
  return (
    <div className="flex flex-col gap-6">
      <ActivitySection />
      <TranscodeSection />
    </div>
  );
}
