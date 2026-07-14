"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { errorMessage } from "@/lib/ui";
import { formatDateKey, leadLabel, releaseTypeLabel } from "@/lib/calendar";
import { watchlistApi, type Reminder, type ReminderState } from "@/lib/watchlist";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Drawer,
  DrawerContent,
  DrawerDescription,
  DrawerHeader,
  DrawerTitle,
} from "@/components/ui/drawer";
import { Input } from "@/components/ui/input";
import { QueryState } from "@/components/states";

const STATE_LABEL: Record<ReminderState, string> = {
  scheduled: "Scheduled",
  recurring: "Recurring",
  released: "Released",
  pending: "Pending",
};

function StatePill({ reminder }: { reminder: Reminder }) {
  // Pending is the interesting state (type set, date unknown) — give it the filled badge.
  const variant = reminder.state === "pending" ? "default" : reminder.state === "released" ? "outline" : "secondary";
  return <Badge variant={variant}>{STATE_LABEL[reminder.state]}</Badge>;
}

/**
 * The Reminders slide-over: the user's reminders as poster rows — type · lead · time plus a state pill
 * (scheduled / recurring / released / pending) — each deletable, with a name filter on top. This is where
 * pending reminders (date not announced yet) live until the sync binds them.
 */
export function RemindersDrawer({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const [filter, setFilter] = useState("");
  const queryClient = useQueryClient();

  const reminders = useQuery({ queryKey: ["reminders"], queryFn: watchlistApi.listReminders, enabled: open });

  const remove = useMutation({
    mutationFn: (reminder: Reminder) => watchlistApi.deleteReminder(reminder.id),
    onSuccess: (_, reminder) => {
      toast.success(`Reminder removed — “${reminder.title}”`);
      void queryClient.invalidateQueries({ queryKey: ["reminders"] });
      void queryClient.invalidateQueries({ queryKey: ["watchlist"] });
      void queryClient.invalidateQueries({ queryKey: ["watchlist-calendar"] });
    },
    onError: (error) => toast.error("Couldn’t remove the reminder", { description: errorMessage(error) }),
  });

  const query = filter.trim().toLowerCase();

  return (
    <Drawer open={open} onOpenChange={onOpenChange} swipeDirection="right">
      <DrawerContent>
        <DrawerHeader>
          <DrawerTitle>Reminders</DrawerTitle>
          <DrawerDescription>What you’ve asked to be notified about.</DrawerDescription>
        </DrawerHeader>

        <div className="flex min-h-0 flex-1 flex-col gap-3 p-4">
          <Input value={filter} placeholder="Filter by name…" onChange={(event) => setFilter(event.target.value)} />

          <div className="-mr-2 flex min-h-0 flex-1 flex-col gap-1 overflow-y-auto pr-2">
            <QueryState query={reminders} empty="No reminders yet — pick a date on the calendar to set one.">
              {(items) =>
                items
                  .filter((reminder) => !query || reminder.title.toLowerCase().includes(query))
                  .map((reminder) => (
                    <div key={reminder.id} className="hover:bg-secondary/60 flex items-center gap-3 rounded-md p-1.5">
                      <div className="bg-secondary h-16 w-11 shrink-0 overflow-hidden rounded">
                        {reminder.posterUrl && (
                          // eslint-disable-next-line @next/next/no-img-element
                          <img src={reminder.posterUrl} alt="" className="h-full w-full object-cover" />
                        )}
                      </div>
                      <div className="min-w-0 flex-1">
                        <p className="truncate font-medium">{reminder.title}</p>
                        <p className="text-muted-foreground truncate text-xs">
                          {reminder.releaseType === "EpisodeAir" ? "Episode airs" : releaseTypeLabel(reminder.releaseType)}
                          {" · "}
                          {leadLabel(reminder.leadDays)}
                          {" · "}
                          {reminder.notifyAt}
                        </p>
                        {reminder.date && (
                          <p className="text-muted-foreground truncate text-xs">{formatDateKey(reminder.date)}</p>
                        )}
                      </div>
                      <div className="flex shrink-0 items-center gap-1.5">
                        <StatePill reminder={reminder} />
                        <Button
                          variant="ghost"
                          size="icon-sm"
                          aria-label="Delete reminder"
                          disabled={remove.isPending}
                          onClick={() => remove.mutate(reminder)}
                        >
                          <Trash2 />
                        </Button>
                      </div>
                    </div>
                  ))
              }
            </QueryState>
          </div>
        </div>
      </DrawerContent>
    </Drawer>
  );
}
