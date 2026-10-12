import { redirect } from "next/navigation";
import { managementHref } from "@/lib/management";

// Retain old bookmarks without retaining a separate movie management screen.
export default async function Page({ params, searchParams }: {
  params: Promise<{ id: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const [{ id }, search] = await Promise.all([params, searchParams]);
  const preserved = Object.fromEntries(["source", "removed", "return"].flatMap(key =>
    typeof search[key] === "string" ? [[key, search[key]]] : []));
  redirect(managementHref("Movie", id, undefined, preserved));
}
