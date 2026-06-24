import { CollectionDetail } from "@/components/collection-detail";

export default async function CollectionDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CollectionDetail id={id} />;
}
