import { describe, expect, it, vi } from 'vitest';
import { render, screen, userEvent, within } from '../../test/utils';
import { DataTable, type DataTableColumn } from './index';

interface TestItem {
  id: string;
  name: string;
  value: number;
  status: string;
}

const testData: TestItem[] = [
  { id: '1', name: 'Item A', value: 100, status: 'active' },
  { id: '2', name: 'Item B', value: 200, status: 'inactive' },
  { id: '3', name: 'Item C', value: 50, status: 'active' },
  { id: '4', name: 'Item D', value: 300, status: 'pending' },
  { id: '5', name: 'Item E', value: 150, status: 'active' },
];

const columns: DataTableColumn<TestItem>[] = [
  { id: 'name', header: 'Name', accessorKey: 'name' },
  { id: 'value', header: 'Value', accessorKey: 'value', align: 'right' },
  { id: 'status', header: 'Status', accessorKey: 'status' },
];

describe('DataTable', () => {
  it('renders data in table rows', () => {
    render(<DataTable data={testData} columns={columns} />);

    expect(screen.getByText('Item A')).toBeInTheDocument();
    expect(screen.getByText('Item B')).toBeInTheDocument();
    expect(screen.getByText('100')).toBeInTheDocument();
    // Multiple 'active' values exist, check they're all rendered
    expect(screen.getAllByText('active')).toHaveLength(3);
  });

  it('renders column headers', () => {
    render(<DataTable data={testData} columns={columns} />);

    expect(screen.getByText('Name')).toBeInTheDocument();
    expect(screen.getByText('Value')).toBeInTheDocument();
    expect(screen.getByText('Status')).toBeInTheDocument();
  });

  it('shows empty state when data is empty', () => {
    render(<DataTable data={[]} columns={columns} />);

    expect(screen.getByText('No data')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('shows custom empty state', () => {
    render(<DataTable data={[]} columns={columns} emptyState={<div>Custom empty message</div>} />);

    expect(screen.getByText('Custom empty message')).toBeInTheDocument();
  });

  it('shows loading state with skeletons', () => {
    render(<DataTable data={testData} columns={columns} isLoading />);

    // Should not show actual data while loading
    expect(screen.queryByText('Item A')).not.toBeInTheDocument();
  });

  it('sorts by column when header clicked', async () => {
    const user = userEvent.setup();
    render(<DataTable data={testData} columns={columns} enablePagination={false} />);

    // Find the name header button and click it
    const nameHeader = screen.getByRole('button', { name: /name/i });
    await user.click(nameHeader);

    // After first click, should be sorted ascending
    const rows = screen.getAllByRole('row');
    // First row is header, second is first data row
    expect(within(rows[1]).getByText('Item A')).toBeInTheDocument();

    // Click again for descending
    await user.click(nameHeader);
    const rowsAfterSecondClick = screen.getAllByRole('row');
    expect(within(rowsAfterSecondClick[1]).getByText('Item E')).toBeInTheDocument();
  });

  it('filters data with search', async () => {
    const user = userEvent.setup();
    render(<DataTable data={testData} columns={columns} enablePagination={false} />);

    const searchInput = screen.getByPlaceholderText('Search...');
    await user.type(searchInput, 'Item A');

    // Should only show Item A
    expect(screen.getByText('Item A')).toBeInTheDocument();
    expect(screen.queryByText('Item B')).not.toBeInTheDocument();
    expect(screen.queryByText('Item C')).not.toBeInTheDocument();
  });

  it('calls onRowClick when row is clicked', async () => {
    const user = userEvent.setup();
    const onRowClick = vi.fn();

    render(<DataTable data={testData} columns={columns} onRowClick={onRowClick} enablePagination={false} />);

    // Click on the first data row (after header)
    const rows = screen.getAllByRole('row');
    await user.click(rows[1]); // First data row

    expect(onRowClick).toHaveBeenCalledWith(testData[0]);
  });

  it('enables pagination automatically for large datasets', () => {
    // DataTable auto-enables pagination when data.length > 20
    const smallData = Array.from({ length: 15 }, (_, i) => ({
      id: String(i),
      name: `Item ${i}`,
      value: i * 10,
      status: 'active',
    }));

    const { rerender } = render(
      <DataTable data={smallData} columns={columns} showToolbar={false} />
    );

    // Small dataset - no pagination text
    expect(screen.queryByText(/of 15/)).not.toBeInTheDocument();

    const largeData = Array.from({ length: 30 }, (_, i) => ({
      id: String(i),
      name: `Item ${i}`,
      value: i * 10,
      status: 'active',
    }));

    rerender(<DataTable data={largeData} columns={columns} showToolbar={false} />);

    // Large dataset - pagination is enabled
    expect(screen.getByText(/of 30/)).toBeInTheDocument();
  });

  it('respects enablePagination=false', () => {
    const largeData = Array.from({ length: 50 }, (_, i) => ({
      id: String(i),
      name: `Item ${i}`,
      value: i * 10,
      status: 'active',
    }));

    render(<DataTable data={largeData} columns={columns} enablePagination={false} showToolbar={false} />);

    // All items should be visible
    expect(screen.getByText('Item 0')).toBeInTheDocument();
    expect(screen.getByText('Item 49')).toBeInTheDocument();
    // No pagination info
    expect(screen.queryByText(/of 50/)).not.toBeInTheDocument();
  });

  it('hides toolbar when showToolbar is false', () => {
    render(<DataTable data={testData} columns={columns} showToolbar={false} />);

    expect(screen.queryByPlaceholderText('Search...')).not.toBeInTheDocument();
  });

  it('renders custom toolbar actions', () => {
    render(
      <DataTable data={testData} columns={columns} toolbarActions={<button type="button">Custom Action</button>} />
    );

    expect(screen.getByRole('button', { name: 'Custom Action' })).toBeInTheDocument();
  });

  it('shows column toggle button when enableColumnVisibility is true', () => {
    render(<DataTable data={testData} columns={columns} enableColumnVisibility />);

    // Column toggle button should be present
    expect(screen.getByRole('button', { name: /toggle columns/i })).toBeInTheDocument();
  });

  it('hides column toggle button when enableColumnVisibility is false', () => {
    render(<DataTable data={testData} columns={columns} enableColumnVisibility={false} />);

    // Column toggle button should not be present
    expect(screen.queryByRole('button', { name: /toggle columns/i })).not.toBeInTheDocument();
  });

  it('disables sorting when enableSorting is false', () => {
    render(<DataTable data={testData} columns={columns} enableSorting={false} />);

    // Headers should not be clickable buttons
    expect(screen.queryByRole('button', { name: /name/i })).not.toBeInTheDocument();
    // But text should still be there
    expect(screen.getByText('Name')).toBeInTheDocument();
  });

  it('uses getRowId for row keys', () => {
    const onRowClick = vi.fn();
    render(
      <DataTable data={testData} columns={columns} getRowId={(row) => row.id} onRowClick={onRowClick} enablePagination={false} />
    );

    // Component should render without errors when using custom getRowId
    expect(screen.getByText('Item A')).toBeInTheDocument();
  });
});
