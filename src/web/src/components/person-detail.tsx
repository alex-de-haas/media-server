"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, Film, User } from "lucide-react";
import { mediaServer, type PersonFilmographyEntry } from "@/lib/media-server";
import { PosterCard, detailHref, parsePersonId } from "@/components/poster-card";
import { EmptyState } from "@/components/states";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

/** Person page: profile header (image, name, biography, known-for) plus a filmography grid of library
 *  titles the person is credited on. Reached only via cast links, so the back control returns to the
 *  originating detail page. Addressed by the provider identity its cast members carry. */
export function PersonDetail({ id }: { id: string }) {
  const { provider, providerId } = parsePersonId(id);
  const person = useQuery({
    queryKey: ["person", provider, providerId],
    queryFn: () => mediaServer.getPerson(provider, providerId),
  });

  if (person.isPending) {
    return <PersonDetailSkeleton />;
  }

  if (person.isError || !person.data) {
    return (
      <div className="flex flex-col gap-3">
        <BackButton />
        <p className="text-muted-foreground text-sm">This person could not be found.</p>
      </div>
    );
  }

  const data = person.data;
  const born = formatPersonDate(data.birthday);
  const died = formatPersonDate(data.deathday);

  return (
    <div className="flex flex-col gap-6">
      <BackButton />

      <header className="flex flex-col gap-4 sm:flex-row sm:gap-6">
        <div className="bg-secondary aspect-[2/3] w-28 shrink-0 overflow-hidden rounded-md shadow-lg ring-1 ring-black/10 sm:w-40">
          {data.profileUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={data.profileUrl} alt={data.name} className="h-full w-full object-cover" />
          ) : (
            <div className="text-muted-foreground flex h-full w-full items-center justify-center">
              <User className="size-10" aria-hidden />
            </div>
          )}
        </div>

        <div className="flex min-w-0 flex-col gap-3">
          <div className="flex flex-col gap-1">
            <h1 className="font-serif text-3xl leading-tight font-medium sm:text-4xl">{data.name}</h1>
            {data.knownForDepartment && (
              <p className="text-muted-foreground text-sm">Known for {data.knownForDepartment}</p>
            )}
          </div>

          {(born || died || data.placeOfBirth) && (
            <div className="text-muted-foreground flex flex-col gap-0.5 text-sm">
              {(born || data.placeOfBirth) && (
                <p>
                  <span className="text-foreground/70">Born </span>
                  {[born, data.placeOfBirth && `in ${data.placeOfBirth}`].filter(Boolean).join(" ")}
                </p>
              )}
              {died && (
                <p>
                  <span className="text-foreground/70">Died </span>
                  {died}
                </p>
              )}
            </div>
          )}

          <Biography text={data.biography} />
        </div>
      </header>

      <Filmography cast={data.cast} crew={data.crew} />
    </div>
  );
}

function BackButton() {
  const router = useRouter();
  return (
    <button
      type="button"
      onClick={() => router.back()}
      className="text-muted-foreground hover:text-foreground inline-flex w-fit items-center gap-1.5 text-sm"
    >
      <ArrowLeft className="size-4" aria-hidden /> Back
    </button>
  );
}

// Long-form biography, clamped to a few lines with a show-more/less toggle once it gets long. Renders
// nothing when the provider has no biography.
function Biography({ text }: { text: string | null }) {
  const [expanded, setExpanded] = useState(false);
  if (!text) {
    return null;
  }

  const isLong = text.length > 320;

  return (
    <div className="flex max-w-2xl flex-col items-start gap-1">
      <p className={cn("text-sm leading-relaxed", isLong && !expanded && "line-clamp-5")}>{text}</p>
      {isLong && (
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          className="text-brand text-sm font-medium hover:underline"
        >
          {expanded ? "Show less" : "Show more"}
        </button>
      )}
    </div>
  );
}

// Acting credits, then crew credits grouped by department — each a poster grid reusing PosterCard, the
// same tiles the library grids use. Filmography entries route to the credited title's detail page.
function Filmography({
  cast,
  crew,
}: {
  cast: PersonFilmographyEntry[];
  crew: { department: string; credits: PersonFilmographyEntry[] }[];
}) {
  const sections = [
    { title: "Acting", entries: cast },
    ...crew.map((group) => ({ title: group.department, entries: group.credits })),
  ].filter((section) => section.entries.length > 0);

  if (sections.length === 0) {
    return <EmptyState icon={Film}>No filmography in your library yet.</EmptyState>;
  }

  return (
    <div className="flex flex-col gap-6">
      {sections.map((section) => (
        <section key={section.title} className="flex flex-col gap-3">
          <h2 className="text-lg font-semibold tracking-tight">{section.title}</h2>
          <Separator />
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
            {section.entries.map((entry) => (
              <PosterCard
                key={`${entry.id}:${entry.character ?? entry.job ?? ""}`}
                href={detailHref(entry.kind, entry.id)}
                title={entry.title}
                subtitle={entrySubtitle(entry)}
                posterUrl={entry.posterUrl}
                userData={null}
              />
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}

function entrySubtitle(entry: PersonFilmographyEntry): string | null {
  const role = entry.character ?? entry.job;
  return [entry.year?.toString(), role].filter(Boolean).join(" · ") || null;
}

// Pin locale + UTC so SSR and client format identically (avoids a hydration mismatch); the UI is
// English-only. Provider dates are "YYYY-MM-DD"; anything shorter/partial is shown verbatim.
const personDateFormatter = new Intl.DateTimeFormat("en", {
  year: "numeric",
  month: "long",
  day: "numeric",
  timeZone: "UTC",
});
function formatPersonDate(value: string | null): string | null {
  if (!value) {
    return null;
  }
  const date = new Date(value);
  if (value.length < 10 || Number.isNaN(date.getTime())) {
    return value;
  }
  return personDateFormatter.format(date);
}

function PersonDetailSkeleton() {
  return (
    <div className="flex flex-col gap-6">
      <Skeleton className="h-5 w-16" />
      <div className="flex flex-col gap-4 sm:flex-row sm:gap-6">
        <Skeleton className="aspect-[2/3] w-28 shrink-0 rounded-md sm:w-40" />
        <div className="flex flex-1 flex-col gap-3 pt-2">
          <Skeleton className="h-9 w-2/3" />
          <Skeleton className="h-4 w-40" />
          <Skeleton className="mt-2 h-20 w-full max-w-2xl" />
        </div>
      </div>
      <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6">
        {Array.from({ length: 6 }).map((_, index) => (
          <Skeleton key={index} className="aspect-[2/3] w-full rounded-md" />
        ))}
      </div>
    </div>
  );
}
