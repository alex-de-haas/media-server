import { MediaDetail } from "@/components/media-detail";

export default async function MovieDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <MediaDetail id={id} backHref="/movies" backLabel="Movies" />;
}
