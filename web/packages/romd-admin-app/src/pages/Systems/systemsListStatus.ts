import type { DatCatalogSubscription, ManagedSystem } from '@romd/admin-api-client';

export function groupSystemSubscriptions(subscriptions: DatCatalogSubscription[]) {
  const groups = new Map<string, DatCatalogSubscription[]>();
  for (const subscription of subscriptions) {
    if (!subscription.systemKey) continue;
    const group = groups.get(subscription.systemKey);
    if (group) group.push(subscription);
    else
      groups.set(subscription.systemKey, [
        subscription,
      ]);
  }
  return groups;
}

export function systemSubscriptionStatus(subscriptions: DatCatalogSubscription[]) {
  const reviews = subscriptions.filter(
    (subscription) =>
      subscription.state === 'ReadyToImport' || subscription.state === 'UpdateAvailable',
  );
  return {
    firstDats: reviews.filter((subscription) => !subscription.activeDatId),
    updates: reviews.filter((subscription) => subscription.activeDatId),
    failed: subscriptions.filter((subscription) => subscription.state === 'CheckFailed'),
    unchecked: subscriptions.filter((subscription) => subscription.state === 'NotChecked'),
    applying: subscriptions.filter((subscription) => subscription.state === 'Applying'),
    paused: subscriptions.filter((subscription) => subscription.automaticChecksPaused),
    current: subscriptions.some((subscription) => subscription.state === 'UpToDate'),
  };
}

export function catalogLabel(state: string): string {
  switch (state) {
    case 'Ready':
      return 'Ready';
    case 'NeedsCatalog':
      return 'No DAT installed';
    case 'Processing':
      return 'Processing';
    case 'NeedsAttention':
      return 'Needs attention';
    default:
      return 'Unknown';
  }
}

export function systemNeedsAttention(
  system: ManagedSystem,
  status: ReturnType<typeof systemSubscriptionStatus>,
) {
  return (
    system.state === 'NeedsCatalog' ||
    system.state === 'NeedsAttention' ||
    status.firstDats.length > 0 ||
    status.updates.length > 0 ||
    status.failed.length > 0
  );
}

export function systemUpdateLabels(
  system: ManagedSystem,
  status: ReturnType<typeof systemSubscriptionStatus>,
  subscriptionState: 'available' | 'pending' | 'unavailable' | 'restricted',
) {
  const notices: {
    label: string;
    color: string;
  }[] = [];
  if (status.firstDats.length > 0)
    notices.push({
      label:
        status.firstDats.length === 1
          ? 'First DAT to review'
          : `${status.firstDats.length} first DATs to review`,
      color: 'orange',
    });
  if (status.updates.length > 0)
    notices.push({
      label:
        status.updates.length === 1
          ? 'Update to review'
          : `${status.updates.length} updates to review`,
      color: 'orange',
    });
  if (status.failed.length > 0)
    notices.push({
      label: status.failed.length === 1 ? 'Check failed' : `${status.failed.length} checks failed`,
      color: 'orange',
    });
  if (status.applying.length > 0)
    notices.push({
      label:
        status.applying.length > 1
          ? `${status.applying.length} imports in progress`
          : status.applying[0].activeDatId
            ? 'Applying update'
            : 'Importing DAT',
      color: 'blue',
    });
  if (notices.length > 0) return notices;
  if (subscriptionState === 'restricted')
    return [
      {
        label: '—',
        color: 'dimmed',
      },
    ];
  if (subscriptionState !== 'available')
    return [
      {
        label: subscriptionState === 'pending' ? 'Loading status…' : 'Status unavailable',
        color: 'dimmed',
      },
    ];
  if (status.paused.length > 0)
    return [
      {
        label: 'Checks paused',
        color: 'dimmed',
      },
    ];
  if (status.unchecked.length > 0)
    return [
      {
        label: 'Not checked',
        color: 'dimmed',
      },
    ];
  if (status.current)
    return [
      {
        label: 'Up to date',
        color: 'dimmed',
      },
    ];
  if (system.catalogCount > 0)
    return [
      {
        label: 'Manual updates',
        color: 'dimmed',
      },
    ];
  return [
    {
      label: '—',
      color: 'dimmed',
    },
  ];
}
