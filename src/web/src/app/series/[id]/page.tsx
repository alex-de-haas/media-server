import { MediaDetail } from "@/components/media-detail";
import { catalogSearchParam, withCatalog } from "@/lib/catalog-navigation";

export default async function SeriesDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ catalog?: string | string[] }>;
}) {
  const [{ id }, query] = await Promise.all([params, searchParams]);
  return <MediaDetail id={id} backHref={withCatalog("/series", catalogSearchParam(query.catalog))} backLabel="Series" />;
}
