import { LibraryGrid } from "@/components/library-grid";

export default function SeriesPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold tracking-tight">Series</h1>
      <LibraryGrid kind="Series" />
    </>
  );
}
