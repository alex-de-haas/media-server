import { Suspense } from "react";
import { LibraryManagement } from "@/components/library-management";
export default function Page() {
  return <Suspense fallback={<p>Loading management…</p>}><LibraryManagement kind="Movie" /></Suspense>;
}
