import { Suspense } from "react";
import { LibraryManagement } from "@/components/library-management";
export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <Suspense fallback={<p>Loading management…</p>}><LibraryManagement kind="Series" id={id} /></Suspense>;
}
