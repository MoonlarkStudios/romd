import { Alert, Button, Skeleton, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import type { ManagedSystem } from '@romd/admin-api-client';
import { IconChevronRight, IconSearch } from '@tabler/icons-react';
import { useManagedSystems } from '../../hooks/api/useManagedSystems';
import { SystemLogo, useSystemIcons } from './SystemLogo';
import classes from './SystemSetupFlow.module.css';

export function SystemSetupPicker({
  search,
  onSearch,
  selectedKey,
  getHint,
  onChoose,
  disabled,
}: {
  search: string;
  onSearch: (search: string) => void;
  selectedKey?: string;
  getHint: (system: ManagedSystem) => string | undefined;
  onChoose: (system: ManagedSystem) => void;
  disabled: boolean;
}) {
  const [query] = useDebouncedValue(search.trim(), 180);
  const choices = useManagedSystems(query, {
    orderByName: true,
  });
  const icons = useSystemIcons();
  const searching = search.trim() !== query || choices.isPending;
  return (
    <Stack gap="sm">
      <TextInput
        label="Find a system"
        placeholder="Search by name or alias"
        leftSection={<IconSearch size={16} />}
        value={search}
        onChange={(event) => onSearch(event.currentTarget.value)}
        data-autofocus
      />
      {searching ? (
        <Skeleton
          h={144}
          aria-label="Finding systems"
        />
      ) : choices.isError ? (
        <Alert
          color="red"
          role="alert"
        >
          {choices.error.message}
          <Button
            variant="subtle"
            onClick={() => void choices.refetch()}
          >
            Retry
          </Button>
        </Alert>
      ) : choices.data.length ? (
        <Stack
          className={classes.systemChoices}
          gap={0}
          role="group"
          aria-label="Supported systems"
        >
          {choices.data.map((system) => {
            const hint = getHint(system);
            return (
              <UnstyledButton
                key={system.key}
                type="button"
                aria-label={`Choose ${system.name}`}
                aria-pressed={selectedKey === undefined ? undefined : selectedKey === system.key}
                className={classes.systemChoice}
                disabled={disabled}
                onClick={() => onChoose(system)}
              >
                <SystemLogo
                  icon={icons.get(system.key)}
                  size="sm"
                />
                <Stack
                  component="span"
                  gap={2}
                  className={classes.choiceLabel}
                >
                  <Text
                    component="span"
                    fw={600}
                    size="sm"
                  >
                    {system.name}
                  </Text>
                  <Text
                    component="span"
                    size="xs"
                    c="dimmed"
                  >
                    {[
                      system.shortName,
                      hint,
                    ]
                      .filter(Boolean)
                      .join(' · ')}
                  </Text>
                </Stack>
                <IconChevronRight
                  size={16}
                  aria-hidden="true"
                />
              </UnstyledButton>
            );
          })}
        </Stack>
      ) : (
        <Text
          size="sm"
          c="dimmed"
          role="status"
        >
          No matching systems. Try another name or alias.
        </Text>
      )}
    </Stack>
  );
}
