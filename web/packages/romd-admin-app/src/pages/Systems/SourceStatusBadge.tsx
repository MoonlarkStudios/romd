import { Badge } from '@mantine/core';
import type { SourceLifecycleStatus } from '@romd/admin-api-client';

const STATUS_COLORS: Record<SourceLifecycleStatus, string> = {
  Active: 'green',
  Discontinued: 'orange',
  Disabled: 'red',
};

export interface SourceStatusBadgeProps {
  status: SourceLifecycleStatus;
}

/** Lifecycle status of a catalog source: green while live, warning colors once dormant. */
export function SourceStatusBadge({ status }: SourceStatusBadgeProps) {
  return (
    <Badge size="xs" variant="light" radius="sm" color={STATUS_COLORS[status]}>
      {status}
    </Badge>
  );
}
