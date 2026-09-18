import { Accordion, Anchor, Badge, Button, Group, NativeSelect, Stack, Text } from '@mantine/core';
import { useLocalStorage } from '@mantine/hooks';
import type { TitleRelease } from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { useTitleSourceReferences } from '../../hooks/api/useTitleDetail';
import { SourceStatusBadge } from '../Systems/SourceStatusBadge';
import { FileRequirements } from './FileRequirements';
import { SourceReferencesPanel } from './SourceReferencesPanel';

export function releaseRegions(region: string | null | undefined): string[] {
  if (!region?.trim()) return ['Unspecified'];
  return region.split(/[,;/]/).map((part) => part.trim()).filter(Boolean)
    .map((part) => /^(US|USA|United States)$/i.test(part) ? 'USA' : part);
}

export function releaseOwnership(release: TitleRelease): 'Owned' | 'Partial' | 'Missing' {
  if (release.isComplete) return 'Owned';
  return release.files?.some((file) => file.isOwned) ? 'Partial' : 'Missing';
}

export function ReleasesPanel({ releases, titleId }: { releases: TitleRelease[]; titleId: string }) {
  const [params, setParams] = useSearchParams();
  const target = params.get('release');
  const selected = releases.find((release) => release.id === target || release.sources?.some((source) => source.datGameId === target));
  const [expanded, setExpanded] = useState<string[]>([]);
  useEffect(() => { if (selected) setExpanded([selected.id]); }, [selected?.id]);
  const [region, setRegion] = useLocalStorage<string>({ key: 'romd-admin-release-region', defaultValue: '', getInitialValueInEffect: false });
  const [ownership, setOwnership] = useState('');
  const [sourceId, setSourceId] = useState('');
  const { data: references = [] } = useTitleSourceReferences(titleId);
  const regions = Array.from(new Set([...releases.flatMap((release) => releaseRegions(release.region)), ...(region ? [region] : [])])).sort();
  const sourceOptions = new Map<string, string>();
  for (const reference of references) if (reference.datId) sourceOptions.set(reference.datId, reference.name ?? 'Catalog source');
  for (const release of releases) {
    if (!sourceOptions.has(release.datId)) sourceOptions.set(release.datId, 'Catalog source');
    for (const source of release.sources ?? []) sourceOptions.set(source.datId, source.datName);
  }
  const focused = releases.filter((release) => (!region || releaseRegions(release.region).includes(region))
    && (!sourceId || release.datId === sourceId || release.sources?.some((source) => source.datId === sourceId)));
  const visible = selected ? [selected] : focused.filter((release) => !ownership || releaseOwnership(release) === ownership);
  const owned = focused.filter((release) => release.isComplete).length;
  return (
    <Stack gap="md">
      {target && <Group><Text size="sm">{selected ? 'Linked release' : 'The linked release is no longer available.'}</Text><Button variant="subtle" onClick={() => { const next = new URLSearchParams(params); next.delete('release'); setParams(next); }}>Show filtered releases</Button></Group>}
      <Group align="flex-end" gap="md">
        <NativeSelect label="Region" aria-label="Release region" value={region} onChange={(event) => setRegion(event.currentTarget.value)} data={[{ value: '', label: 'All regions' }, ...regions.map((value) => ({ value, label: value }))]} />
        <NativeSelect label="Ownership" value={ownership} onChange={(event) => setOwnership(event.currentTarget.value)} data={[{ value: '', label: 'Any ownership' }, 'Owned', 'Missing', 'Partial']} />
        <NativeSelect label="Source" value={sourceId} onChange={(event) => setSourceId(event.currentTarget.value)} data={[{ value: '', label: 'All sources' }, ...Array.from(sourceOptions, ([value, label]) => ({ value, label }))]} style={{ maxWidth: '100%', minWidth: 0 }} />
      </Group>
      <Group justify="space-between">
        <Text size="sm" fw={500}>{region || 'All regions'}: {owned} owned · {focused.length} known</Text>
        <Text size="xs" c="dimmed">Showing {visible.length} of {releases.length} catalog releases</Text>
      </Group>
      {visible.length > 0 ? (
        <Accordion variant="default" multiple value={expanded} onChange={setExpanded}>
          {visible.map((release) => {
            const status = releaseOwnership(release);
            const sources = release.sources?.length ? release.sources : [{ datId: release.datId, datGameId: release.id, datName: sourceOptions.get(release.datId) ?? 'Catalog source', gameName: release.name }];
            return (
              <Accordion.Item key={release.id} value={release.id}>
                <Accordion.Control>
                  <Group justify="space-between" wrap="nowrap" gap="sm">
                    <Stack gap={4} style={{ minWidth: 0 }}>
                      <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>{release.name}</Text>
                      <Text size="xs" c="dimmed">{[release.region || 'Unspecified region', release.revision, release.language, release.year].filter(Boolean).join(' · ')}</Text>
                      <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{Array.from(new Set(sources.map((source) => source.datName))).join(' · ')}</Text>
                    </Stack>
                    <Badge style={{ flexShrink: 0 }} variant="light" color={status === 'Owned' ? 'green' : status === 'Partial' ? 'yellow' : 'gray'}>{status}</Badge>
                  </Group>
                </Accordion.Control>
                <Accordion.Panel>
                  <FileRequirements files={release.files ?? []} />
                  <Stack gap="xs" mt="md">
                    <Text size="xs" c="dimmed" fw={600}>Source declarations</Text>
                    {sources.map((source) => {
                      const reference = references.find((ref) => ref.datId === source.datId);
                      return <Stack key={source.datGameId} gap={4}>
                        <Group gap="xs">
                          {reference?.systemKey ? <Anchor component={Link} size="sm" to={`/systems/${reference.systemKey}?dat=${source.datId}&sourceTitle=${titleId}`}>{source.datName}</Anchor> : <Text size="sm">{source.datName}</Text>}
                          {reference && <SourceStatusBadge status={reference.status} />}
                        </Group>
                        <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{source.gameName}</Text>
                      </Stack>;
                    })}
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>
            );
          })}
        </Accordion>
      ) : (
        <Stack gap="xs" align="flex-start" py="md">
          <Text size="sm" c="dimmed">{releases.length === 0 ? 'No releases found for this title.' : 'No releases match this view.'}</Text>
          {releases.length > 0 && <Button variant="subtle" size="xs" onClick={() => { setRegion(''); setOwnership(''); setSourceId(''); }}>Show all releases</Button>}
        </Stack>
      )}
      <Accordion variant="default">
        <Accordion.Item value="sources">
          <Accordion.Control>Catalog sources{references.length > 0 ? ` (${references.length})` : ''}</Accordion.Control>
          <Accordion.Panel><SourceReferencesPanel titleId={titleId} /></Accordion.Panel>
        </Accordion.Item>
      </Accordion>
    </Stack>
  );
}
