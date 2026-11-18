import { Group, SimpleGrid, Stack, Text, Textarea, TextInput } from '@mantine/core';
import classes from './SystemSetupFlow.module.css';

export type SystemPresentation = {
  name: string;
  description: string;
};

export function SystemPresentationFields({
  value,
  onChange,
  systemKey,
  onKeyChange,
  manufacturer,
  disabled,
}: {
  value: SystemPresentation;
  onChange: (value: SystemPresentation) => void;
  systemKey: string;
  onKeyChange?: (key: string) => void;
  manufacturer?: string | null;
  disabled: boolean;
}) {
  return (
    <Stack
      gap="sm"
      className={classes.presentation}
    >
      <SimpleGrid
        cols={{
          base: 1,
          xs: 2,
        }}
        spacing="sm"
      >
        <TextInput
          label="Display name"
          required
          maxLength={200}
          value={value.name}
          disabled={disabled}
          onChange={(event) =>
            onChange({
              ...value,
              name: event.currentTarget.value,
            })
          }
        />
        <TextInput
          label="System key"
          description={
            onKeyChange
              ? 'Starts with local-. Use lowercase letters, numbers and hyphens.'
              : 'Fixed for this system.'
          }
          required={!!onKeyChange}
          maxLength={50}
          value={systemKey}
          readOnly={!onKeyChange}
          disabled={disabled && !!onKeyChange}
          onChange={onKeyChange ? (event) => onKeyChange(event.currentTarget.value) : undefined}
        />
      </SimpleGrid>
      {manufacturer && (
        <Group gap="xs">
          <Text
            size="sm"
            fw={500}
          >
            Manufacturer
          </Text>
          <Text
            size="sm"
            c="dimmed"
          >
            {manufacturer}
          </Text>
        </Group>
      )}
      <Textarea
        label="Short description"
        placeholder="Optional"
        autosize
        minRows={2}
        maxRows={4}
        maxLength={2000}
        value={value.description}
        disabled={disabled}
        onChange={(event) =>
          onChange({
            ...value,
            description: event.currentTarget.value,
          })
        }
      />
    </Stack>
  );
}
