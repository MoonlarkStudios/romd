import { Navigate, useSearchParams } from 'react-router';
import { useDats } from '../../hooks/api/useDats';

/**
 * Maps the retired Registry Explorer's context-selection URLs
 * (/registry/explorer?type=system&id=X) onto the routed System hub.
 */
export function LegacyExplorerRedirect() {
  const [searchParams] = useSearchParams();
  const type = searchParams.get('type');
  const id = searchParams.get('id');

  // Old DAT selections carried no platform — resolve it from the DAT list
  const { data: dats, isLoading } = useDats();

  if (type === 'system' && id) {
    return <Navigate to={`/systems/${id}`} replace />;
  }

  if (type === 'dat' && id) {
    if (isLoading) return null;
    const systemKey = dats?.find((dat) => dat.id === id)?.systemKey;
    return (
      <Navigate
        to={systemKey ? `/systems/${systemKey}?tab=sources&dat=${id}` : '/systems'}
        replace
      />
    );
  }

  return <Navigate to="/systems" replace />;
}
