import type { ReactNode } from "react";

// A horizontally-scrolling row of poster tiles for the Home page (Continue watching / Next up / …).
export function Rail({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
      <div className="flex gap-3 overflow-x-auto pb-1">{children}</div>
    </section>
  );
}

// Fixed-width slot so rail tiles keep a consistent poster size while the row scrolls.
export function RailItem({ children }: { children: ReactNode }) {
  return <div className="w-28 shrink-0 sm:w-32">{children}</div>;
}
