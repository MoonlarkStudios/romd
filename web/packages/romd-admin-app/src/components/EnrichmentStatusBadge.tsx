import { Badge, type MantineColor } from '@mantine/core';
import {
  IconAlertTriangle,
  IconCheck,
  IconLoader,
  IconQuestionMark,
  IconSearch,
  IconX,
} from '@tabler/icons-react';
import type { ReactNode } from 'react';

type EnrichmentStatus = 'None' | 'Pending' | 'Completed' | 'Failed' | 'NotFound' | 'LowConfidence';

interface StatusConfig {
  color: MantineColor;
  label: string;
  icon: ReactNode;
}

const STATUS_CONFIG: Record<EnrichmentStatus, StatusConfig> = {
  None: {
    color: 'gray',
    label: 'Not Enriched',
    icon: <IconQuestionMark size={12} />,
  },
  Pending: {
    color: 'blue',
    label: 'Enriching...',
    icon: <IconLoader size={12} />,
  },
  Completed: {
    color: 'green',
    label: 'Enriched',
    icon: <IconCheck size={12} />,
  },
  Failed: {
    color: 'red',
    label: 'Failed',
    icon: <IconX size={12} />,
  },
  NotFound: {
    color: 'orange',
    label: 'Not Found',
    icon: <IconSearch size={12} />,
  },
  LowConfidence: {
    color: 'yellow',
    label: 'Low Confidence',
    icon: <IconAlertTriangle size={12} />,
  },
};

interface EnrichmentStatusBadgeProps {
  status: string;
  size?: 'xs' | 'sm' | 'md' | 'lg' | 'xl';
}

/**
 * Badge showing the current enrichment status of a title.
 */
export function EnrichmentStatusBadge({ status, size = 'sm' }: EnrichmentStatusBadgeProps) {
  const config = STATUS_CONFIG[status as EnrichmentStatus] ?? STATUS_CONFIG.None;

  return (
    <Badge variant="light" color={config.color} size={size} leftSection={config.icon}>
      {config.label}
    </Badge>
  );
}
