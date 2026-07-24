import { CalendarView } from "@/components/calendar-view";

/**
 * The mode and month are read here, on the server, rather than through `useSearchParams` in the
 * client component: on a statically rendered route that hook yields no params until hydration, and a
 * click landing in that window would navigate from the wrong month.
 */
export default async function CalendarPage({
  searchParams,
}: {
  searchParams: Promise<{ view?: string | string[]; month?: string | string[] }>;
}) {
  const query = await searchParams;
  return <CalendarView view={single(query.view)} month={single(query.month)} />;
}

function single(value: string | string[] | undefined): string | null {
  return Array.isArray(value) ? (value[0] ?? null) : (value ?? null);
}
