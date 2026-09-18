import { createContext, type ReactNode, useContext } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useSystemSocket } from './useSystemSocket';

interface SystemSocketContextValue {
  status: 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
}

const SystemSocketContext = createContext<SystemSocketContextValue | null>(null);

interface SystemSocketProviderProps {
  children: ReactNode;
}

/**
 * Provides the SignalR system socket connection at the app level.
 * Invalidates dashboard stat queries on real-time signals.
 * Only connects when the user is authenticated.
 */
export function SystemSocketProvider({ children }: SystemSocketProviderProps) {
  const { isAuthenticated } = useAuth();
  const { status } = useSystemSocket(isAuthenticated);

  return (
    <SystemSocketContext.Provider value={{ status }}>
      {children}
    </SystemSocketContext.Provider>
  );
}

export function useSystemConnectionStatus() {
  const context = useContext(SystemSocketContext);
  if (!context) {
    throw new Error('useSystemConnectionStatus must be used within a SystemSocketProvider');
  }
  return context.status;
}
