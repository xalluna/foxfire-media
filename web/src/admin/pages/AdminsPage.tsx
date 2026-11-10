import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api, formatDate } from '../api';

interface AdminRow {
  username: string;
  isBootstrap: boolean;
  isYou: boolean;
  createdAt: string;
  lastLoginAt?: string | null;
  lockedUntil?: string | null;
}

function AddAdmin() {
  const client = useQueryClient();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const add = useMutation({
    mutationFn: () => api<undefined>('/admins', { method: 'POST', body: { username, password } }),
    onSuccess: () => {
      setUsername('');
      setPassword('');
      void client.invalidateQueries({ queryKey: ['admins'] });
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    add.mutate();
  };

  return (
    <form className="card stack" onSubmit={submit}>
      <h2>Add an admin</h2>
      <label>
        Username
        <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" required />
      </label>
      <label>
        Initial password
        <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" minLength={12} required />
      </label>
      <p className="muted">At least 12 characters. Share it with them out of band; they can change it after signing in.</p>
      {add.isError && <p className="error">{add.error.message}</p>}
      {add.isSuccess && <p className="muted">Admin added.</p>}
      <div className="row">
        <button className="button primary" type="submit" disabled={add.isPending}>
          Add admin
        </button>
      </div>
    </form>
  );
}

function ChangePassword() {
  const [currentPassword, setCurrent] = useState('');
  const [newPassword, setNew] = useState('');
  const [confirm, setConfirm] = useState('');
  const change = useMutation({
    mutationFn: () => api<undefined>('/auth/password', { method: 'POST', body: { currentPassword, newPassword } }),
    onSuccess: () => {
      setCurrent('');
      setNew('');
      setConfirm('');
    },
  });
  const mismatch = confirm.length > 0 && confirm !== newPassword;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!mismatch) change.mutate();
  };

  return (
    <form className="card stack" onSubmit={submit}>
      <h2>Change your password</h2>
      <label>
        Current password
        <input type="password" value={currentPassword} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" required />
      </label>
      <label>
        New password
        <input type="password" value={newPassword} onChange={(e) => setNew(e.target.value)} autoComplete="new-password" minLength={12} required />
      </label>
      <label>
        Confirm new password
        <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" required />
      </label>
      {mismatch && <p className="error">The passwords do not match.</p>}
      {change.isError && <p className="error">{change.error.message}</p>}
      {change.isSuccess && <p className="muted">Password changed. Your other sessions were signed out.</p>}
      <div className="row">
        <button className="button primary" type="submit" disabled={change.isPending || mismatch}>
          Change password
        </button>
      </div>
    </form>
  );
}

export function AdminsPage() {
  const client = useQueryClient();
  const admins = useQuery({ queryKey: ['admins'], queryFn: () => api<AdminRow[]>('/admins') });
  const remove = useMutation({
    mutationFn: (username: string) => api<undefined>(`/admins/${encodeURIComponent(username)}`, { method: 'DELETE' }),
    onSuccess: () => void client.invalidateQueries({ queryKey: ['admins'] }),
  });

  return (
    <section className="stack">
      <h1 className="page-title">Admins</h1>
      <div className="card table-wrap">
        {admins.isPending && <p className="muted">Loading…</p>}
        {admins.isError && <p className="error">{admins.error.message}</p>}
        {admins.data && (
          <table className="table">
            <thead>
              <tr>
                <th>Username</th>
                <th>Last sign-in</th>
                <th>Created</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {admins.data.map((a) => (
                <tr key={a.username}>
                  <td>
                    {a.username} {a.isYou && <span className="badge">You</span>} {a.isBootstrap && <span className="badge">Bootstrap</span>}{' '}
                    {a.lockedUntil && <span className="badge bad">Locked until {formatDate(a.lockedUntil)}</span>}
                  </td>
                  <td>{a.lastLoginAt ? formatDate(a.lastLoginAt) : <span className="muted">Never</span>}</td>
                  <td>{formatDate(a.createdAt)}</td>
                  <td>
                    {!a.isYou && admins.data.length > 1 && (
                      <button
                        type="button"
                        className="button text"
                        disabled={remove.isPending}
                        onClick={() => {
                          if (window.confirm(`Delete admin ${a.username}? Their sessions end immediately.`)) remove.mutate(a.username);
                        }}
                      >
                        Delete
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {remove.isError && <p className="error">{remove.error.message}</p>}
      </div>
      <div className="two-col">
        <AddAdmin />
        <ChangePassword />
      </div>
    </section>
  );
}
