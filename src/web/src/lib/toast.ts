import { toast as sonnerToast, type ExternalToast } from "sonner";

// Error toasts must not auto-dismiss: a failed action should stay on screen until the user reads it
// and closes it (the <Toaster> renders a close button). Other variants keep sonner's defaults. Only
// the default duration is overridden, so a caller can still pass an explicit `duration` to opt out.
const error: typeof sonnerToast.error = (message, options?: ExternalToast) =>
  sonnerToast.error(message, { duration: Infinity, ...options });

// Drop-in replacement for sonner's `toast`: preserves the callable form and every method
// (success, promise, dismiss, …) while making errors persistent. Import from here instead of "sonner".
export const toast: typeof sonnerToast = Object.assign(
  (...args: Parameters<typeof sonnerToast>) => sonnerToast(...args),
  sonnerToast,
  { error },
);
