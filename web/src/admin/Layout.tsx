import { useState, type FormEvent } from 'react';
import { NavLink, Outlet, useNavigate, useSearchParams } from 'react-router';
import { useAuth } from './auth';

const NAV: { to: string; label: string }[] = [
  { to: '/', label: 'Library' },
  { to: '/upload', label: 'Upload' },
  { to: '/processing', label: 'Processing' },
  { to: '/applications', label: 'Applications' },
  { to: '/storage', label: 'Storage' },
  { to: '/admins', label: 'Admins' },
];

/** YouTube-like chrome: fixed top bar with a centered search box, left navigation rail. */
export function Layout() {
  const { me, signOut } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [query, setQuery] = useState(params.get('q') ?? '');
  const [menuOpen, setMenuOpen] = useState(false);

  const search = (event: FormEvent) => {
    event.preventDefault();
    navigate(query.trim() ? `/?q=${encodeURIComponent(query.trim())}` : '/');
  };

  return (
    <div className="shell">
      <header className="topbar">
        <div className="topbar-start">
          <button type="button" className="menu-button" aria-label="Toggle navigation" onClick={() => setMenuOpen((open) => !open)}>
            <span className="menu-bars" />
          </button>
          <span className="wordmark">FoxfireMedia</span>
        </div>
        <form className="search" role="search" onSubmit={search}>
          <input
            type="search"
            placeholder="Search videos"
            aria-label="Search videos"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          <button type="submit">Search</button>
        </form>
        <div className="topbar-end">
          <span className="who">{me?.username}</span>
          <button type="button" className="button text" onClick={() => void signOut().then(() => navigate('/login'))}>
            Sign out
          </button>
        </div>
      </header>
      <nav className={menuOpen ? 'sidebar open' : 'sidebar'} aria-label="Main">
        {NAV.map((item) => (
          <NavLink key={item.to} to={item.to} end={item.to === '/'} className="nav-item" onClick={() => setMenuOpen(false)}>
            {item.label}
          </NavLink>
        ))}
      </nav>
      <main className="content">
        <Outlet />
      </main>
    </div>
  );
}
