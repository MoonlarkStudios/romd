import { useLocation, useNavigate } from 'react-router';

/**
 * Back navigation that preserves browsing context: returns to the previous
 * entry (keeping library filters, scroll, and the rail the user came from),
 * falling back to a sensible parent when the page was opened as a deep link
 * (the initial history entry has the key 'default').
 */
export function useHistoryBack(fallbackTo: string): () => void {
  const navigate = useNavigate();
  const location = useLocation();

  return () => {
    if (location.key === 'default') {
      navigate(fallbackTo);
      return;
    }

    navigate(-1);
  };
}
