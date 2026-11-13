// GET /api/releases
// Reads HumanTyper's published releases from GitHub and caches the result at the edge for 10 minutes,
// so visitors don't hit GitHub's per-IP rate limit and don't need to reach api.github.com themselves.

const REPO = 'ydnw6/HumanTyper';
const TTL = 600;

export async function onRequestGet({ request, waitUntil }) {
  const cache = caches.default;
  const key = new Request(new URL('/api/releases', request.url).toString());

  const hit = await cache.match(key);
  if (hit) return hit;

  const gh = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=10`, {
    headers: { 'User-Agent': 'humantyper-site', Accept: 'application/vnd.github+json' },
  });
  if (!gh.ok) {
    return json({ error: `GitHub responded ${gh.status}` }, 502, 'no-store');
  }

  const list = (await gh.json())
    .filter((r) => !r.draft && !r.prerelease)
    .map((r) => ({
      tag: r.tag_name,
      name: r.name,
      date: r.published_at,
      url: r.html_url,
      body: r.body || '',
      assets: (r.assets || []).map((a) => ({
        name: a.name,
        size: a.size,
        url: a.browser_download_url,
        digest: a.digest || null,
      })),
    }));

  const res = json(list, 200, `public, max-age=${TTL}`);
  waitUntil(cache.put(key, res.clone()));
  return res;
}

function json(data, status, cacheControl) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': cacheControl,
      'access-control-allow-origin': '*',
    },
  });
}
