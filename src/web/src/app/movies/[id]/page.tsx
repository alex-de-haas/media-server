import { MediaDetail } from "@/components/media-detail";
import { catalogSearchParam, withCatalog } from "@/lib/catalog-navigation";

export default async function MovieDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ catalog?: string | string[] }>;
}) {
  const [{ id }, query] = await Promise.all([params, searchParams]);
  return <MediaDetail id={id} backHref={withCatalog("/movies", catalogSearchParam(query.catalog))} backLabel="Movies" />;
}
