import { MediaDetail } from "@/components/media-detail";
import { catalogSearchParam, removedSearchParam, withCatalog, withRemoved } from "@/lib/catalog-navigation";

export default async function MovieDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ catalog?: string | string[]; removed?: string | string[] }>;
}) {
  const [{ id }, query] = await Promise.all([params, searchParams]);
  return <MediaDetail id={id} backHref={withRemoved(withCatalog("/movies", catalogSearchParam(query.catalog)), removedSearchParam(query.removed))} backLabel="Movies" />;
}
