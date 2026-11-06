import type { ReactNode } from 'react';

/** Shared library-card crop and shading; navigation belongs to the host. */
export function GamePosterSurface({ children }: { children: ReactNode }) {
  return (
    <div className="romd-game-poster-surface">
      {children}
      <div className="romd-game-poster-shade" />
    </div>
  );
}
