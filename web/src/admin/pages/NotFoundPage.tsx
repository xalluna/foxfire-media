import { Link } from 'react-router';

export function NotFoundPage() {
  return (
    <section>
      <h1 className="page-title">Page not found</h1>
      <p className="muted">
        This page does not exist. <Link to="/">Back to the library</Link>
      </p>
    </section>
  );
}