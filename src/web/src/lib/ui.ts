import { ApiError } from "@/lib/api";

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message || `Request failed (${error.status}).`;
  }
  return error instanceof Error ? error.message : "Unexpected error.";
}
