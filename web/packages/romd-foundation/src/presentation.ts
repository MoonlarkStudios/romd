import { ratingBoards } from './generated/referenceCatalog';

export { ratingBoards, systemKeys } from './generated/referenceCatalog';

export function formatRatingLabel(board: string, code: string): string {
  const label = ratingBoards.find(item => item.key === board)?.label ?? board;
  return code.toUpperCase().startsWith(label.toUpperCase()) ? code : `${label} ${code}`;
}
