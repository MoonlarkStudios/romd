import { Group, Pagination, Select, Text } from '@mantine/core';
import { useDataTableContext } from './DataTableContext';

export function DataTablePagination() {
  const { table, pageSizeOptions } = useDataTableContext();
  const { pageIndex, pageSize } = table.getState().pagination;
  const pageCount = table.getPageCount();
  const totalRows = table.getFilteredRowModel().rows.length;

  const startRow = totalRows === 0 ? 0 : pageIndex * pageSize + 1;
  const endRow = Math.min((pageIndex + 1) * pageSize, totalRows);

  return (
    <Group justify="space-between" p="md">
      <Group gap="xs">
        <Text size="sm" c="dimmed">
          Rows per page:
        </Text>
        <Select
          size="xs"
          value={String(pageSize)}
          onChange={(value) => {
            if (value) table.setPageSize(Number(value));
          }}
          data={pageSizeOptions.map((size) => ({
            value: String(size),
            label: String(size),
          }))}
          style={{ width: 70 }}
          withCheckIcon={false}
        />
      </Group>

      <Text size="sm" c="dimmed">
        {startRow}-{endRow} of {totalRows}
      </Text>

      {pageCount > 1 && (
        <Pagination
          size="sm"
          total={pageCount}
          value={pageIndex + 1}
          onChange={(page) => table.setPageIndex(page - 1)}
          withEdges
        />
      )}
    </Group>
  );
}
