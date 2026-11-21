import {
  Box,
  Combobox,
  Group,
  Loader,
  ScrollArea,
  Stack,
  Switch,
  Text,
  TextInput,
  useCombobox,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { useCatalogSearch } from '../hooks/api/useCatalogSearch';
import { usePlatforms } from '../hooks/api/usePlatforms';
import { gamePosterUrl } from './artworkPresentation';
import { catalogGameLabel } from './catalogGameLabel';
import { GameCover } from './GameCover';

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
  /** Keep the field ready to add another game after accepting a result. */
  clearAfterSelect?: boolean;
  localFirst?: boolean;
  withinPortal?: boolean;
}

/**
 * Async select for searching and selecting titles.
 * Uses the catalog search API with debounced type-ahead.
 */
export function TitleSearchSelect({
  onChange,
  placeholder = 'Search titles...',
  label,
  excludeIds = [],
  error,
  disabled,
  clearAfterSelect = false,
  localFirst = false,
  withinPortal,
}: TitleSearchSelectProps) {
  const combobox = useCombobox({
    onDropdownClose: () => combobox.resetSelectedOption(),
  });

  const [search, setSearch] = useState('');
  const [debouncedSearch] = useDebouncedValue(search, 300);
  const [selectedTitle, setSelectedTitle] = useState<CatalogTitle | null>(null);
  const [localFilesOnly, setLocalFilesOnly] = useState(localFirst);
  const systems = usePlatforms();
  const query = debouncedSearch.trim();
  const canSearch = (text: string) => text.length >= 2 || (localFilesOnly && text.length === 0);
  const searching = canSearch(query);
  const catalog = useCatalogSearch(
    {
      query: query || undefined,
      sortBy: query ? 'relevance' : undefined,
      localFilesOnly,
    },
    {
      enabled: searching && combobox.dropdownOpened,
    },
  );

  const waiting =
    canSearch(search.trim()) &&
    (search.trim() !== query || catalog.isLoading || catalog.isPlaceholderData);
  const showResults = searching && search.trim() === query && !waiting && !catalog.isError;
  const titles = showResults
    ? (catalog.data?.pages.flatMap((p) => p.items).filter((t) => !excludeIds.includes(t.id)) ?? [])
    : [];

  const handleSelect = (title: CatalogTitle) => {
    setSelectedTitle(clearAfterSelect ? null : title);
    setSearch(clearAfterSelect ? '' : title.name);
    onChange(title.id, title);
    combobox.closeDropdown();
  };

  const options = titles.map((title) => {
    const { system, availability } = catalogGameLabel(title, systems.data);
    return (
      <Combobox.Option
        key={title.id}
        value={title.id}
        aria-label={`${title.name}, ${system}, ${availability}`}
      >
        <Group
          gap="sm"
          wrap="nowrap"
        >
          <GameCover url={gamePosterUrl(title.artwork)} />
          <Box miw={0}>
            <Text
              size="sm"
              fw={600}
              lineClamp={2}
            >
              {title.name}
            </Text>
            <Text
              size="xs"
              c="dimmed"
            >
              {system} · {availability}
            </Text>
          </Box>
        </Group>
      </Combobox.Option>
    );
  });

  return (
    <Stack gap="xs">
      <Combobox
        store={combobox}
        withinPortal={withinPortal}
        onOptionSubmit={(val) => {
          if (val === 'more-results') {
            if (!catalog.isFetchingNextPage) void catalog.fetchNextPage();
            return;
          }
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
              combobox.resetSelectedOption();
            }}
            onClick={() => combobox.openDropdown()}
            onFocus={() => combobox.openDropdown()}
            onBlur={() => {
              combobox.closeDropdown();
            }}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && combobox.dropdownOpened) event.preventDefault();
            }}
            leftSection={<IconSearch size={16} />}
            rightSection={waiting ? <Loader size={16} /> : null}
            error={error}
            disabled={disabled}
          />
        </Combobox.Target>

        <Combobox.Dropdown>
          <Combobox.Options aria-label={label ? `${label} results` : 'Game search results'}>
            {waiting && (
              <Combobox.Empty>
                <Group
                  gap="xs"
                  justify="center"
                >
                  <Loader size={14} />
                  <Text size="sm">Searching games…</Text>
                </Group>
              </Combobox.Empty>
            )}
            {!canSearch(search.trim()) && (
              <Combobox.Empty>Enter at least two characters to find a game.</Combobox.Empty>
            )}
            {!waiting && canSearch(search.trim()) && catalog.isError && (
              <Combobox.Empty>Search is unavailable. Edit your search to try again.</Combobox.Empty>
            )}
            {showResults && !catalog.hasNextPage && options.length === 0 && (
              <Combobox.Empty>
                {localFilesOnly
                  ? 'No local games match. Turn off “Local files only” to search the catalog.'
                  : 'No matching games.'}
              </Combobox.Empty>
            )}
            {showResults && (options.length > 0 || catalog.hasNextPage) && (
              <ScrollArea.Autosize
                mah={280}
                type="auto"
              >
                {options}
                {catalog.hasNextPage && (
                  <Combobox.Option
                    value="more-results"
                    disabled={catalog.isFetchingNextPage}
                  >
                    {catalog.isFetchingNextPage ? 'Loading more games…' : 'More games'}
                  </Combobox.Option>
                )}
              </ScrollArea.Autosize>
            )}
          </Combobox.Options>
        </Combobox.Dropdown>
      </Combobox>
      {localFirst && (
        <Switch
          label="Local files only"
          size="xs"
          disabled={disabled}
          checked={localFilesOnly}
          onChange={(event) => setLocalFilesOnly(event.currentTarget.checked)}
        />
      )}
    </Stack>
  );
}
