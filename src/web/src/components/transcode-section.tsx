"use client";

import { useQuery } from "@tanstack/react-query";
import { mediaServer, type TranscodeJob } from "@/lib/media-server";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { TranscodeJobRow, isTranscodeActive } from "@/components/transcode";

/** Activity-page card listing every transcode job (re-encodes), polled while any is active. */
export function TranscodeSection() {
  const jobs = useQuery({
    queryKey: ["transcode-jobs"],
    queryFn: mediaServer.listTranscodeJobs,
    refetchInterval: (query) => {
      const data = (query.state.data ?? []) as TranscodeJob[];
      return data.some(isTranscodeActive) ? 2000 : false;
    },
  });

  const all = jobs.data ?? [];
  // Nothing to show until the operator has started a conversion — keep the page clean.
  if (all.length === 0) {
    return null;
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Conversions</CardTitle>
        <CardDescription>Re-encoding movie sources into smaller versions.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        {all.map((job) => (
          <TranscodeJobRow key={job.id} job={job} />
        ))}
      </CardContent>
    </Card>
  );
}
