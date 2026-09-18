import { createContext, type ReactNode, useContext } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useJobNotifications } from './useJobNotifications';
import { type ConnectionStatus, useJobSocket } from './useJobSocket';

interface JobSocketContextValue {
  status: ConnectionStatus;
}

const JobSocketContext = createContext<JobSocketContextValue | null>(null);

interface JobSocketProviderProps {
  children: ReactNode;
}

/**
 * Provides the SignalR job socket connection at the app level.
 * Only connects when the user is authenticated.
 */
export function JobSocketProvider({ children }: JobSocketProviderProps) {
  const { isAuthenticated } = useAuth();
  const { status } = useJobSocket(isAuthenticated);

  // Centralized toast + cache invalidation reactor
  useJobNotifications();

  return (
    <JobSocketContext.Provider value={{ status }}>
      {children}
    </JobSocketContext.Provider>
  );
}

/**
 * Returns the current SignalR connection status for display purposes.
 */
export function useConnectionStatus(): ConnectionStatus {
  const context = useContext(JobSocketContext);
  if (!context) {
    throw new Error('useConnectionStatus must be used within a JobSocketProvider');
  }
  return context.status;
}

export type { ConnectionStatus };
