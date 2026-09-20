"use client";

import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";

export function DeleteWatchDialog({ open, onOpenChange, title, detail, removed = false, pending, onConfirm }: {
  open: boolean; onOpenChange: (open: boolean) => void; title?: string; detail?: string | null;
  removed?: boolean; pending: boolean; onConfirm: () => void;
}) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>Delete this play?</AlertDialogTitle>
          <AlertDialogDescription>
            Removes one recorded play of <span className="text-foreground font-medium">{title}</span>
            {detail && <> ({detail})</>} from your history. The play count follows. This cannot be undone.
            {removed && " If this is your last watch, rating or favorite, the movie disappears from your removed list. Its record is deleted when nobody has any marks left."}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel size="sm" disabled={pending}>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" size="sm" disabled={pending} onClick={onConfirm}>
            {pending ? "Deleting…" : "Delete"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
