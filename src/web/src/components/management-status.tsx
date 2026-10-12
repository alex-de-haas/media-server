import { CircleCheck, CircleHelp, CircleX, Clock } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { availabilityLabels, type Availability } from "@/lib/management";

const states = {
  available: { icon: CircleCheck, variant: "success" },
  blocked: { icon: CircleX, variant: "destructive" },
  pending: { icon: Clock, variant: "info" },
  unknown: { icon: CircleHelp, variant: "secondary" },
} as const;

/** Color supplements the stored assessment's label and icon; unknown is never a success. */
export function ManagementAvailability({ value }: { value: Availability }) {
  const { icon: Icon, variant } = states[value];
  return <Badge variant={variant}><Icon aria-hidden />{availabilityLabels[value]}</Badge>;
}
