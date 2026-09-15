import { GroupDetail } from "@/components/group-grid";
export default async function GroupPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <GroupDetail id={id} />;
}
