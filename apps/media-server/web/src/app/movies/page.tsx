import { LibraryGrid } from "@/components/library-grid";

export default function MoviesPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold tracking-tight">Movies</h1>
      <LibraryGrid kind="Movie" />
    </>
  );
}
