import { Badge, Combobox, Group, Loader, Text, TextInput, useCombobox } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { useCatalogSearch } from '../hooks/api/useCatalogSearch';

interface TitleSearchSelectProps {
  /** Current selected title ID */
  value: string | null;
  /** Callback when a title is selected */
  onChange: (titleId: string | null, title: CatalogTitle | null) => void;
  /** Placeholder text */
  placeholder?: string;
  /** Label for the input */
  label?: string;
  /** Titles to exclude from results (e.g., current title in merge) */
  excludeIds?: string[];
  /** Error message */
  error?: string;
  /** Whether the input is disabled */
  disabled?: boolean;
}

/**
 * Async select for searching and selecting titles.
 * Uses the catalog search API with debounced type-ahead.
 */
export function TitleSearchSelect({
  value,
  onChange,
  placeholder = 'Search titles...',
  label,
  excludeIds = [],
  error,
  disabled,
}: TitleSearchSelectProps) {
  const combobox = useCombobox({
    onDropdownClose: () => combobox.resetSelectedOption(),
  });

  const [search, setSearch] = useState('');
  const [debouncedSearch] = useDebouncedValue(search, 300);
  const [selectedTitle, setSelectedTitle] = useState<CatalogTitle | null>(null);

  const { data, isLoading } = useCatalogSearch(
    { query: debouncedSearch || undefined },
    { enabled: debouncedSearch.length >= 2 }
  );

  const titles = data?.pages.flatMap((p) => p.items).filter((t) => !excludeIds.includes(t.id)) ?? [];

  const handleSelect = (title: CatalogTitle) => {
    setSelectedTitle(title);
    setSearch(title.name);
    onChange(title.id, title);
    combobox.closeDropdown();
  };

  const handleClear = () => {
    setSelectedTitle(null);
    setSearch('');
    onChange(null, null);
  };

  const options = titles.map((title) => (
    <Combobox.Option key={title.id} value={title.id}>
      <Group gap="sm" wrap="nowrap">
        <Text size="sm" truncate style={{ flex: 1 }}>
          {title.name}
        </Text>
        {title.genre && (
          <Badge size="xs" variant="light" color="gray">
            {title.genre}
          </Badge>
        )}
      </Group>
    </Combobox.Option>
  ));

  return (
    <Combobox
      store={combobox}
      onOptionSubmit={(val) => {
        const title = titles.find((t) => t.id === val);
        if (title) {
          handleSelect(title);
        }
      }}
    >
      <Combobox.Target>
        <TextInput
          label={label}
          placeholder={placeholder}
          value={search}
          onChange={(event) => {
            setSearch(event.currentTarget.value);
            if (selectedTitle && event.currentTarget.value !== selectedTitle.name) {
              setSelectedTitle(null);
              onChange(null, null);
            }
            combobox.openDropdown();
            combobox.updateSelectedOptionIndex();
          }}
          onClick={() => combobox.openDropdown()}
          onFocus={() => combobox.openDropdown()}
          onBlur={() => {
            combobox.closeDropdown();
            if (!selectedTitle) {
              setSearch('');
            }
          }}
          leftSection={<IconSearch size={16} />}
          rightSection={isLoading ? <Loader size={16} /> : null}
          error={error}
          disabled={disabled}
        />
      </Combobox.Target>

      <Combobox.Dropdown>
        <Combobox.Options>
          {isLoading && (
            <Combobox.Empty>
              <Group gap="xs" justify="center">
                <Loader size={14} />
                <Text size="sm">Searching...</Text>
              </Group>
            </Combobox.Empty>
          )}
          {!isLoading && debouncedSearch.length < 2 && (
            <Combobox.Empty>Type at least 2 characters to search</Combobox.Empty>
          )}
          {!isLoading && debouncedSearch.length >= 2 && options.length === 0 && (
            <Combobox.Empty>No titles found</Combobox.Empty>
          )}
          {options}
        </Combobox.Options>
      </Combobox.Dropdown>
    </Combobox>
  );
}
