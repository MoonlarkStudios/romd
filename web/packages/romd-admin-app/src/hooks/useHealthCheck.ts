import { healthCheck } from '@romd/admin-api-client';
import { useCallback, useEffect, useState } from 'react';

interface HealthCheckResult {
  status: string;
  timestamp: string;
}

interface UseHealthCheckReturn {
  status: 'loading' | 'healthy' | 'error';
  data: HealthCheckResult | null;
  error: string | null;
  refresh: () => void;
}

export function useHealthCheck(): UseHealthCheckReturn {
  const [status, setStatus] = useState<'loading' | 'healthy' | 'error'>('loading');
  const [data, setData] = useState<HealthCheckResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const fetchHealth = useCallback(async () => {
    setStatus('loading');
    setError(null);

    try {
      const response = await healthCheck();
      if (response.error) {
        throw new Error(`Health check failed`);
      }
      const result = response.data as HealthCheckResult;
      setData(result);
      setStatus('healthy');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
      setStatus('error');
      setData(null);
    }
  }, []);

  useEffect(() => {
    fetchHealth();
  }, [fetchHealth]);

  return {
    status,
    data,
    error,
    refresh: fetchHealth,
  };
}
