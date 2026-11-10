import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, use, type ReactNode } from 'react';
import { api, ApiError, refreshXsrf } from './api';

export interface AdminMe {
  username: string;
  isBootstrap: boolean;
}

interface AuthState {
  me: AdminMe | null;
  loading: boolean;
  signIn: (username: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const client = useQueryClient();
  const me = useQuery({
    queryKey: ['me'],
    queryFn: async () => {
      try {
        return await api<AdminMe>('/auth/me');
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null;
        throw error;
      }
    },
    staleTime: 60_000,
  });

  const signIn = useMutation({
    mutationFn: async ({ username, password }: { username: string; password: string }) => {
      await refreshXsrf();
      const result = await api<AdminMe>('/auth/login', { method: 'POST', body: { username, password } });
      await refreshXsrf(); // antiforgery tokens are bound to the signed-in admin
      return result;
    },
    onSuccess: (result) => client.setQueryData(['me'], result),
  });

  const signOut = useMutation({
    mutationFn: () => api<undefined>('/auth/logout', { method: 'POST' }),
    onSettled: () => {
      client.clear();
      client.setQueryData(['me'], null);
    },
  });

  const value: AuthState = {
    me: me.data ?? null,
    loading: me.isPending,
    signIn: async (username, password) => {
      await signIn.mutateAsync({ username, password });
    },
    signOut: async () => {
      await signOut.mutateAsync();
    },
  };

  return <AuthContext value={value}>{children}</AuthContext>;
}

export function useAuth(): AuthState {
  const value = use(AuthContext);
  if (!value) throw new Error('useAuth outside AuthProvider');
  return value;
}
