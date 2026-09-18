import { createContext, type ReactNode, useContext } from 'react';

export interface ReferenceIcon {
  url: string;
  sha256: string;
  contentType: string;
  monochrome: boolean;
}
/** Display-only projection. Host apps supply the generated API contract. */
export interface ReferenceCatalogData {
  revision: string;
  systems: readonly { key: string; name: string; compactLabel: string; description?: string | null; icon?: ReferenceIcon | null }[];
  ratingBoards: readonly { key: string; name: string; description?: string | null }[];
  ratings: readonly { board: string; code: string; name: string; description?: string | null; icon?: ReferenceIcon | null }[];
}
const Context = createContext<ReferenceCatalogData | undefined>(undefined);
export function ReferenceCatalogProvider({ catalog, children }: { catalog?: ReferenceCatalogData; children: ReactNode }) {
  return <Context.Provider value={catalog}>{children}</Context.Provider>;
}
export function useReferenceCatalog() { return useContext(Context); }
