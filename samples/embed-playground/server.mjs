// A minimal "application" that embeds FoxfireMedia videos, the way your real apps will:
// the API key stays on this server, which lists its videos and mints a fresh embed URL per page view.
//
//   node samples/embed-playground/server.mjs        (then open http://localhost:5500)
//
// Configuration (environment variables, falling back to the repo's .env):
//   FOXFIRE_URL      FoxfireMedia's public origin      (default: PublicBaseUrl from .env)
//   FOXFIRE_APP      application name                  (default: DemoA)
//   FOXFIRE_API_KEY  that application's API key        (default: ApiKeys__<FOXFIRE_APP> from .env)
//   PORT             port for this page                (default: 5500; must be one of the app's AllowedOrigins)

import { existsSync, readFileSync } from 'node:fs';
import http from 'node:http';
import { join } from 'node:path';

function loadDotEnv(path) {
  if (!existsSync(path)) return {};
  return Object.fromEntries(
    readFileSync(path, 'utf8')
      .split(/\r?\n/)
      .filter((line) => line && !line.startsWith('#') && line.includes('='))
      .map((line) => [line.slice(0, line.indexOf('=')).trim(), line.slice(line.indexOf('=') + 1).trim()]),
  );
}

const dotenv = loadDotEnv(process.env.FOXFIRE_ENV_FILE ?? join(import.meta.dirname, '..', '..', '.env'));
const foxfire = (process.env.FOXFIRE_URL ?? dotenv.PublicBaseUrl ?? 'http://localhost:8090').replace(/\/$/, '');
const appName = process.env.FOXFIRE_APP ?? 'DemoA';
const apiKey = process.env.FOXFIRE_API_KEY ?? dotenv[`ApiKeys__${appName}`];
const port = Number(process.env.PORT ?? 5500);

if (!apiKey) {
  console.error(`No API key: set FOXFIRE_API_KEY or ApiKeys__${appName} in .env`);
  process.exit(1);
}

const escape = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

async function foxfireApi(path, init = {}) {
  const response = await fetch(foxfire + path, {
    ...init,
    headers: { 'X-Api-Key': apiKey, 'Content-Type': 'application/json', ...init.headers },
  });
  if (!response.ok) throw new Error(`${init.method ?? 'GET'} ${path} -> ${response.status} ${await response.text()}`);
  return response.json();
}

const page = (title, body) => `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escape(title)}</title>
<style>
  body { margin: 0; font: 14px/1.4 Roboto, Arial, sans-serif; color: #0f0f0f; background: #fff; }
  header { padding: 16px 24px; border-bottom: 1px solid #e5e5e5; }
  main { max-width: 1100px; margin: 0 auto; padding: 24px; }
  a { color: #065fd4; text-decoration: none; }
  ul { padding: 0; list-style: none; display: grid; gap: 8px; }
  li a { display: block; padding: 12px 16px; border-radius: 12px; background: #f2f2f2; color: inherit; }
  li a:hover { background: #e5e5e5; }
  .frame { position: relative; aspect-ratio: 16 / 9; border-radius: 12px; overflow: hidden; background: #000; }
  .frame iframe { position: absolute; inset: 0; width: 100%; height: 100%; border: 0; }
  code, pre { font: 12px ui-monospace, Consolas, monospace; }
  pre { white-space: pre-wrap; word-break: break-all; background: #f9f9f9; padding: 12px; border-radius: 8px; }
  .muted { color: #606060; }
</style></head>
<body><header><strong>Embed playground</strong> <span class="muted">· "${escape(appName)}" on ${escape(foxfire)}</span></header>
<main>${body}</main></body></html>`;

async function index() {
  const { items } = await foxfireApi('/api/v1/videos?limit=100');
  const list = items.length
    ? `<ul>${items
        .map((v) => `<li><a href="/watch?id=${encodeURIComponent(v.id)}">${escape(v.title)} <span class="muted">· ${escape(v.status)}</span></a></li>`)
        .join('')}</ul>`
    : `<p class="muted">No videos for ${escape(appName)} yet. Upload one in the admin console (${escape(foxfire)}/admin/upload), choosing ${escape(appName)}.</p>`;
  return page('Embed playground', `<h1>Videos</h1>${list}`);
}

async function watch(id, query) {
  // Server side: mint a short-lived embed URL with the API key. Only the URL goes to the browser.
  const embed = await foxfireApi(`/api/v1/videos/${encodeURIComponent(id)}/embed`, { method: 'POST', body: JSON.stringify({ ttlSeconds: 3600 }) });
  const options = new URLSearchParams();
  for (const name of ['autoplay', 'muted', 't']) if (query.get(name)) options.set(name, query.get(name));
  const src = embed.embedUrl + (options.size ? `?${options}` : '');
  return page(
    'Watch',
    `<p><a href="/">&larr; All videos</a></p>
     <div class="frame"><iframe src="${escape(src)}" allow="autoplay; fullscreen; picture-in-picture" allowfullscreen></iframe></div>
     <p class="muted">Try <a href="?id=${encodeURIComponent(id)}&autoplay=true&muted=true">autoplay</a> or
       <a href="?id=${encodeURIComponent(id)}&t=10">start at 0:10</a>. This embed URL expires ${escape(embed.expiresAt)}; reload for a fresh one.</p>
     ${embed.posterUrl ? `<p class="muted">Poster (usable as a thumbnail on your pages):</p><img src="${escape(embed.posterUrl)}" alt="" width="320">` : ''}
     <p class="muted">Embed URL minted by this page's server:</p><pre>${escape(src)}</pre>`,
  );
}

http
  .createServer(async (req, res) => {
    const url = new URL(req.url ?? '/', `http://localhost:${port}`);
    try {
      const html = url.pathname === '/watch' && url.searchParams.get('id') ? await watch(url.searchParams.get('id'), url.searchParams) : await index();
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
      res.end(html);
    } catch (error) {
      res.writeHead(502, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(page('Error', `<h1>Request to FoxfireMedia failed</h1><pre>${escape(error.message)}</pre>`));
    }
  })
  .listen(port, () => console.log(`Embed playground for ${appName}: http://localhost:${port}  (FoxfireMedia: ${foxfire})`));
