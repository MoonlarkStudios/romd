import { DraftGuardProvider, useDraft } from './DraftGuard';

export function UnsavedChanges({
  dirty,
  saving = false,
  description = 'Your saved settings will stay as they are.',
}: {
  dirty: boolean;
  saving?: boolean;
  description?: string;
}) {
  return (
    <DraftGuardProvider>
      <Draft
        dirty={dirty}
        saving={saving}
        description={description}
      />
    </DraftGuardProvider>
  );
}
function Draft({
  dirty,
  description,
  saving,
}: {
  dirty: boolean;
  description: string;
  saving: boolean;
}) {
  useDraft(dirty, description, saving);
  return null;
}
