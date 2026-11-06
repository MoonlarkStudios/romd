/** Cache the shared UI/runtime independently of lazy application pages. */
export function vendorChunks(id: string): string | undefined {
  // Shared CommonJS interop belongs with React so UI chunks cannot form a cycle back to it.
  if (
    id.includes('commonjsHelpers') ||
    /[/]node_modules[/](react|react-dom|react-router|scheduler)[/]/.test(id)
  ) {
    return 'react-runtime';
  }
  if (id.includes('/node_modules/@mantine/') || id.includes('/node_modules/@floating-ui/')) {
    return 'mantine';
  }
  return undefined;
}
