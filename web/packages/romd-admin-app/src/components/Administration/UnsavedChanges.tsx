import { DraftGuardProvider, useDraft } from './DraftGuard';

export function UnsavedChanges({
  dirty,
  description = 'Your saved settings will stay as they are.',
}: {
  dirty: boolean;
  description?: string;
}) {
  return (
    <DraftGuardProvider>
      <Draft
        dirty={dirty}
        description={description}
      />
    </DraftGuardProvider>
  );
}
function Draft({ dirty, description }: { dirty: boolean; description: string }) {
  useDraft(dirty, description);
  return null;
}
