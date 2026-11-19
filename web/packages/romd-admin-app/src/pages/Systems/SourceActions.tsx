import { ActionIcon, Menu } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { Dat, DatCatalogSubscription, SourceLifecycleStatus } from '@romd/admin-api-client';
import { IconDots, IconDownload, IconUpload } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useLocation } from 'react-router';
import { downloadDatSource } from '../../hooks/api/useDats';
import { usePermissions } from '../../hooks/usePermissions';
import { ConfirmSourceStatusModal } from './ConfirmSourceStatusModal';
import { StopSubscriptionModal } from './SourceSubscriptionActions';

export function sourcePath(systemKey: string, dat: Dat) {
  return `/systems/${systemKey}/sources/${dat.sourceId ?? dat.id}`;
}

export function SourceActions({
  dat,
  path,
  showUpdates = true,
  offerManualUpload = false,
  lifecycle = false,
  subscription,
}: {
  dat: Dat;
  path: string;
  showUpdates?: boolean;
  offerManualUpload?: boolean;
  /** The source page's own menu: status changes and the update method live here. */
  lifecycle?: boolean;
  subscription?: DatCatalogSubscription;
}) {
  const { canManageSources } = usePermissions();
  const { state: returnState } = useLocation();
  const [status, setStatus] = useState<SourceLifecycleStatus | 'Delete' | null>(null);
  const [stopSubscription, setStopSubscription] = useState(false);
  const applying = subscription?.state === 'Applying';
  return (
    <>
      <Menu position="bottom-end">
        <Menu.Target>
          <ActionIcon
            aria-label={`Actions for ${dat.name}`}
            title="Source actions"
            variant="subtle"
            color="gray"
          >
            <IconDots size={18} />
          </ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          {showUpdates && (
            <Menu.Item
              component={Link}
              to={`${path}?tab=updates`}
              state={returnState}
            >
              Updates
            </Menu.Item>
          )}
          <Menu.Item
            leftSection={<IconDownload size={16} />}
            onClick={() =>
              void downloadDatSource(dat).catch(() =>
                notifications.show({
                  color: 'red',
                  message: 'Could not download the installed DAT.',
                }),
              )
            }
          >
            Download installed DAT
          </Menu.Item>
          {offerManualUpload && (
            <Menu.Item
              component={Link}
              to={`${path}?tab=updates`}
              state={returnState}
              leftSection={<IconUpload size={16} />}
            >
              Upload new version
            </Menu.Item>
          )}
          {lifecycle && subscription && (
            <Menu.Item
              disabled={applying}
              onClick={() => setStopSubscription(true)}
            >
              {subscription.activeDatId
                ? 'Switch to manual updates'
                : 'Remove pending subscription'}
            </Menu.Item>
          )}
          {lifecycle && canManageSources && (
            <>
              <Menu.Divider />
              {(
                [
                  'Active',
                  'Discontinued',
                  'Disabled',
                ] as const
              )
                .filter((value) => value !== dat.sourceStatus)
                .map((value) => (
                  <Menu.Item
                    key={value}
                    color={value === 'Active' ? undefined : 'orange'}
                    onClick={() => setStatus(value)}
                  >
                    {value === 'Active'
                      ? 'Re-enable source'
                      : value === 'Discontinued'
                        ? 'Mark discontinued'
                        : 'Disable source'}
                  </Menu.Item>
                ))}
              <Menu.Item
                color="red"
                onClick={() => setStatus('Delete')}
              >
                Delete source permanently
              </Menu.Item>
            </>
          )}
        </Menu.Dropdown>
      </Menu>
      {subscription && (
        <StopSubscriptionModal
          subscription={subscription}
          opened={stopSubscription}
          onClose={() => setStopSubscription(false)}
        />
      )}
      <ConfirmSourceStatusModal
        dat={dat}
        targetStatus={status}
        onClose={() => setStatus(null)}
      />
    </>
  );
}
