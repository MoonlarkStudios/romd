import { StatusState } from './StatusState';

export function LibraryUnavailable() {
  return (
    <StatusState
      kind="empty"
      title="No library available yet"
      message="Ask your administrator to check your library access."
    />
  );
}
