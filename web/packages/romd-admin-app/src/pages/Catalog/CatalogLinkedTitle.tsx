import { Alert, Button, Group, Skeleton, Text } from '@mantine/core';
import { Link } from 'react-router';
import { titleSectionPath } from '../../components/Title/titleSections';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { TitleNotFoundError, useTitleDetail } from '../../hooks/api/useTitleDetail';
import { rememberCatalogPosition } from './catalogNavigation';

/** Narrow layouts keep a copied preview link usable even if the title is outside the loaded page. */
export function CatalogLinkedTitle({
  titleId,
  returnPath,
  knownName,
  href,
}: {
  titleId: string;
  returnPath: string;
  knownName?: string;
  href?: string;
}) {
  const query = useTitleDetail(knownName ? undefined : titleId, false);
  const name = knownName ?? query.data?.name;
  if (query.error instanceof TitleNotFoundError) return null;
  if (!name && query.isPending)
    return (
      <Skeleton
        h={40}
        aria-label="Loading linked title"
      />
    );
  if (!name && query.isError)
    return (
      <Alert
        color="yellow"
        title="Could not load the linked title"
      >
        <Button
          size="compact-sm"
          variant="subtle"
          onClick={() => void query.refetch()}
        >
          Retry
        </Button>
      </Alert>
    );
  return (
    <Group
      gap="xs"
      justify="space-between"
      wrap="nowrap"
    >
      <Text
        size="sm"
        lineClamp={2}
      >
        {name}
      </Text>
      <Button
        {...workspaceActionProps}
        variant="light"
        size="compact-sm"
        component={Link}
        to={href ?? titleSectionPath(titleId)}
        state={{
          catalogReturnTo: returnPath,
        }}
        onClick={(event) => rememberCatalogPosition(returnPath, event)}
      >
        Open linked title
      </Button>
    </Group>
  );
}
