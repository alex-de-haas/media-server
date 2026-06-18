import { MediaDetail } from "@/components/media-detail";

export default async function SeriesDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <MediaDetail id={id} backHref="/series" backLabel="Series" />;
}
