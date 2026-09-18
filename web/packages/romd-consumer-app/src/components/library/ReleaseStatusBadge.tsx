import { Badge } from '@mantine/core';

interface ReleaseStatusBadgeProps {
  isComplete: boolean;
}

export function ReleaseStatusBadge({ isComplete }: ReleaseStatusBadgeProps) {
  return (
    <Badge color={isComplete ? 'mint' : 'bronze'}>
      {isComplete ? 'Complete' : 'Partial'}
    </Badge>
  );
}
