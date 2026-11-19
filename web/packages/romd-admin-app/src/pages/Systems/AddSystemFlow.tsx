import { AddSourceFlow, type OpenSetupSystem } from './AddSourceFlow';

export function AddSystemFlow({
  startWithUpload = false,
  onClose,
  onSystemOpen,
}: {
  startWithUpload?: boolean;
  onClose: () => void;
  onSystemOpen?: OpenSetupSystem;
}) {
  return (
    <AddSourceFlow
      startWithUpload={startWithUpload}
      onClose={onClose}
      onSystemOpen={onSystemOpen}
    />
  );
}
