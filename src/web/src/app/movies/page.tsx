import { LibraryGrid } from "@/components/library-grid";
import { catalogSearchParam } from "@/lib/catalog-navigation";

export default async function MoviesPage({ searchParams }: { searchParams: Promise<{ catalog?: string | string[] }> }) {
  const catalogId = catalogSearchParam((await searchParams).catalog);
  return <LibraryGrid title="Movies" kind="Movie" catalogId={catalogId} />;
}
