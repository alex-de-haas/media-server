import { ApiError } from "@/lib/api";

// Shared input styling for the lightweight forms across the management pages.
export const inputClass =
  "h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm outline-none focus-visible:ring-1 focus-visible:ring-ring";

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message || `Request failed (${error.status}).`;
  }
  return error instanceof Error ? error.message : "Unexpected error.";
}
