// Tests for the web-search skill's scripts. Run from the repo root:
//   node --test tests/skill-scripts/*.test.mjs
// Needs Node.js 18+. Spins up small local HTTP servers; no network access or packages needed.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const scripts = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '../../src/Maxwell.Agents/BuiltInSkills/Maxwell/web-search/scripts');
const SEARCH = path.join(scripts, 'searxng-search.mjs');
const PAGE_TEXT = path.join(scripts, 'page-text.mjs');

// Runs a script and resolves with its exit code and output (never rejects on non-zero exit).
function run(script, args, env = {}) {
  const cleanEnv = { ...process.env, ...env };
  if (env.SEARXNG_URL === undefined) delete cleanEnv.SEARXNG_URL;
  return new Promise((resolve) => {
    execFile(process.execPath, [script, ...args], { env: cleanEnv, timeout: 30_000 }, (error, stdout, stderr) => {
      resolve({ code: error ? (typeof error.code === 'number' ? error.code : 99) : 0, stdout, stderr });
    });
  });
}

function listen(handler) {
  return new Promise((resolve) => {
    const server = http.createServer(handler);
    server.listen(0, '127.0.0.1', () => resolve({ server, url: `http://127.0.0.1:${server.address().port}` }));
  });
}

// ---------------------------------------------------------------- fake SearXNG

const searxRequests = [];
let searx;
let searxMode = 'ok';

const manyResults = Array.from({ length: 12 }, (_, i) => ({
  title: `Result ${i + 1}  with   spaces`,
  url: `https://example.com/page-${i + 1}`,
  content: i === 0 ? 'A '.repeat(300) : `Snippet ${i + 1}\nacross lines`,
  publishedDate: i === 1 ? '2026-10-15T08:00:00' : undefined,
}));

before(async () => {
  searx = await listen((req, res) => {
    const url = new URL(req.url, 'http://x');
    searxRequests.push({ path: url.pathname, query: Object.fromEntries(url.searchParams) });
    const send = (status, body, type = 'application/json') => {
      res.writeHead(status, { 'content-type': type });
      res.end(typeof body === 'string' ? body : JSON.stringify(body));
    };
    switch (searxMode) {
      case 'forbidden': return send(403, 'Forbidden', 'text/html');
      case 'html': return send(200, '<html><body>Search</body></html>', 'text/html');
      case 'empty':
        return send(200, { results: [], unresponsive_engines: [['google', 'CAPTCHA'], ['duckduckgo', 'timeout']] });
      case 'answers':
        return send(200, { answers: ['42', { answer: 'Forty-two' }], results: manyResults.slice(0, 1) });
      case 'error': return send(500, 'boom', 'text/plain');
      default: return send(200, { results: manyResults, unresponsive_engines: [['bing', 'rate limited']] });
    }
  });
});
after(() => searx.server.close());

const search = (args, url = searx.url) => run(SEARCH, args, { SEARXNG_URL: url });

test('search: prints numbered results with url and a trimmed one-line snippet', async () => {
  searxMode = 'ok';
  const { code, stdout } = await search(['hello world']);
  assert.equal(code, 0);
  assert.match(stdout, /^1\. Result 1 with spaces$/m);
  assert.match(stdout, /^ {3}https:\/\/example\.com\/page-1$/m);
  assert.match(stdout, /^ {3}Snippet 3 across lines$/m); // newlines collapsed
  const firstSnippet = stdout.split('\n').find((line) => line.startsWith('   A A A'));
  assert.ok(firstSnippet.length <= 3 + 240, `snippet too long: ${firstSnippet.length}`);
  assert.ok(firstSnippet.endsWith('…'));
});

test('search: shows at most --limit results (default 8) and says how many there were', async () => {
  searxMode = 'ok';
  const def = await search(['q']);
  assert.match(def.stdout, /^8\. /m);
  assert.doesNotMatch(def.stdout, /^9\. /m);
  assert.match(def.stdout, /Showing 8 of 12 results\./);

  const three = await search(['q', '--limit', '3']);
  assert.match(three.stdout, /^3\. /m);
  assert.doesNotMatch(three.stdout, /^4\. /m);
});

test('search: includes the publication date when the result has one', async () => {
  searxMode = 'ok';
  const { stdout } = await search(['q']);
  assert.match(stdout, /^2\. Result 2 with spaces \(2026-10-15\)$/m);
});

test('search: forwards query, format, page, time range, language and category', async () => {
  searxMode = 'ok';
  searxRequests.length = 0;
  await search(['rust', 'async', '--time', 'month', '--lang', 'es', '--category', 'it', '--page', '2']);
  const sent = searxRequests.at(-1);
  assert.equal(sent.path, '/search');
  assert.deepEqual(sent.query, { q: 'rust async', format: 'json', pageno: '2', time_range: 'month', language: 'es', categories: 'it' });
});

test('search: encodes accents, spaces and symbols in the query', async () => {
  searxMode = 'ok';
  searxRequests.length = 0;
  await search(['¿qué es & "C#"?']);
  assert.equal(searxRequests.at(-1).query.q, '¿qué es & "C#"?');
});

test('search: tolerates a trailing slash and a path prefix in SEARXNG_URL', async () => {
  searxMode = 'ok';
  searxRequests.length = 0;
  const { code } = await search(['q'], `${searx.url}/`);
  assert.equal(code, 0);
  assert.equal(searxRequests.at(-1).path, '/search');
});

test('search: exit 2 and a note about engines that did not respond when there are no results', async () => {
  searxMode = 'empty';
  const { code, stdout } = await search(['nothing']);
  assert.equal(code, 2);
  assert.match(stdout, /No results for "nothing"\./);
  assert.match(stdout, /google \(CAPTCHA\), duckduckgo \(timeout\)/);
});

test('search: reports engines that did not respond even when there are results', async () => {
  searxMode = 'ok';
  const { stdout } = await search(['q']);
  assert.match(stdout, /Engines that did not respond: bing \(rate limited\)\./);
});

test('search: prints instant answers (strings or {answer})', async () => {
  searxMode = 'answers';
  const { stdout } = await search(['meaning of life']);
  assert.match(stdout, /^Answer: 42$/m);
  assert.match(stdout, /^Answer: Forty-two$/m);
});

test('search: exit 3 when SEARXNG_URL is not set', async () => {
  const { code, stderr } = await run(SEARCH, ['q']);
  assert.equal(code, 3);
  assert.match(stderr, /SEARXNG_URL is not set/);
});

test('search: exit 3 when SEARXNG_URL is not an http(s) address', async () => {
  const { code, stderr } = await search(['q'], 'localhost:8888');
  assert.equal(code, 3);
  assert.match(stderr, /must start with http/);
});

test('search: exit 4 with the settings.yml hint on 403 (JSON not enabled)', async () => {
  searxMode = 'forbidden';
  const { code, stderr } = await search(['q']);
  assert.equal(code, 4);
  assert.match(stderr, /403/);
  assert.match(stderr, /search\.formats/);
});

test('search: exit 4 with the same hint when the answer is HTML, not JSON', async () => {
  searxMode = 'html';
  const { code, stderr } = await search(['q']);
  assert.equal(code, 4);
  assert.match(stderr, /did not return JSON/);
});

test('search: exit 4 on a server error', async () => {
  searxMode = 'error';
  const { code, stderr } = await search(['q']);
  assert.equal(code, 4);
  assert.match(stderr, /500/);
});

test('search: exit 4 and a clear message when nothing is listening', async () => {
  const closed = await listen(() => {});
  const dead = closed.url;
  await new Promise((resolve) => closed.server.close(resolve));
  const { code, stderr } = await search(['q'], dead);
  assert.equal(code, 4);
  assert.match(stderr, /Could not reach SearXNG/);
});

test('search: exit 1 on bad usage', async () => {
  assert.equal((await search([])).code, 1);
  assert.equal((await search(['q', '--limit', '0'])).code, 1);
  assert.equal((await search(['q', '--time', 'week'])).code, 1);
  assert.equal((await search(['q', '--bogus'])).code, 1);
  assert.equal((await search(['q', '--lang'])).code, 1);
});

// ---------------------------------------------------------------- page-text

const pages = {
  '/article': {
    type: 'text/html; charset=utf-8',
    body: `<!doctype html><html><head><title>Great &amp; Useful Article</title>
      <style>.x{color:red}</style><script>var secret = "SCRIPT-TEXT";</script></head>
      <body><nav>NAV-TEXT Home About</nav>
      <header><h1>Main   headline</h1></header>
      <main><p>First paragraph with <b>bold</b> and a link <a href="/x">here</a>.</p>
      <p>Caf&eacute; &Eacute;l &ntilde;u &iquest;s&iacute;&quest; &#233; &#x41; &lt;tag&gt; &nbsp;done &unknown;</p>
      <ul><li>one</li><li>two</li></ul>
      <!-- COMMENT-TEXT --><noscript>NOSCRIPT-TEXT</noscript></main>
      <aside>ASIDE-TEXT</aside><footer>FOOTER-TEXT</footer>${'<p>' + 'filler sentence. '.repeat(30) + '</p>'}</body></html>`,
  },
  '/long': { type: 'text/html', body: `<html><body>${Array.from({ length: 400 }, (_, i) => `<p>Paragraph number ${i}</p>`).join('')}</body></html>` },
  '/thin': { type: 'text/html', body: '<html><head><title>App</title></head><body><div id="root"></div><script>render()</script></body></html>' },
  '/plain': { type: 'text/plain', body: 'Just plain text.\nSecond line. '.repeat(20) },
  '/pdf': { type: 'application/pdf', body: '%PDF-1.4 binary' },
  '/latin1': { type: 'text/html; charset=iso-8859-1', body: Buffer.from('<html><body><p>Ni\xF1o a\xF1o caf\xE9 acci\xF3n</p></body></html>' + '<p>' + 'relleno '.repeat(60) + '</p>', 'latin1') },
};

let site;
before(async () => {
  site = await listen((req, res) => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname === '/redirect') { res.writeHead(302, { location: '/article' }); return res.end(); }
    if (url.pathname === '/forbidden') { res.writeHead(403, { 'content-type': 'text/html' }); return res.end('no'); }
    if (url.pathname === '/missing') { res.writeHead(404); return res.end('nope'); }
    const page = pages[url.pathname];
    if (!page) { res.writeHead(404); return res.end(); }
    res.writeHead(200, { 'content-type': page.type });
    res.end(page.body);
  });
});
after(() => site.server.close());

const read = (pathAndArgs, ...more) => run(PAGE_TEXT, [`${site.url}${pathAndArgs}`, ...more]);

test('page-text: prints url, decoded title and the readable text', async () => {
  const { code, stdout } = await read('/article');
  assert.equal(code, 0);
  assert.match(stdout, /^URL: http:\/\/127\.0\.0\.1:\d+\/article$/m);
  assert.match(stdout, /^Title: Great & Useful Article$/m);
  assert.match(stdout, /^Main headline$/m);
  assert.match(stdout, /First paragraph with bold and a link here\./);
  assert.match(stdout, /^one$/m);
  assert.match(stdout, /^two$/m);
});

test('page-text: drops scripts, styles, navigation, comments, asides and footers', async () => {
  const { stdout } = await read('/article');
  for (const unwanted of ['SCRIPT-TEXT', 'color:red', 'NAV-TEXT', 'COMMENT-TEXT', 'NOSCRIPT-TEXT', 'ASIDE-TEXT', 'FOOTER-TEXT', '<p>', '<b>']) {
    assert.ok(!stdout.includes(unwanted), `output should not contain ${unwanted}`);
  }
});

test('page-text: decodes named (incl. Latin-1 accents, case-sensitive), decimal and hex entities; leaves unknown ones alone', async () => {
  const { stdout } = await read('/article');
  assert.match(stdout, /Café Él ñu ¿sí&quest; é A <tag> done &unknown;/);
});

test('page-text: truncates to maxChars and says how much was cut', async () => {
  const { code, stdout } = await read('/long', '500');
  assert.equal(code, 0);
  assert.match(stdout, /\[Truncated: showing 500 of \d+ characters\./);
  assert.ok(!stdout.includes('Paragraph number 399'));
});

test('page-text: does not truncate a page that fits', async () => {
  const { stdout } = await read('/long', '50000');
  assert.doesNotMatch(stdout, /Truncated/);
  assert.match(stdout, /Paragraph number 399/);
});

test('page-text: warns when a page has almost no text (JavaScript app)', async () => {
  const { code, stdout } = await read('/thin');
  assert.equal(code, 0);
  assert.match(stdout, /Very little text found/);
  assert.match(stdout, /web-browser/);
});

test('page-text: reads plain text pages', async () => {
  const { code, stdout } = await read('/plain');
  assert.equal(code, 0);
  assert.match(stdout, /Just plain text\./);
});

test('page-text: honours the charset in the Content-Type header', async () => {
  const { stdout } = await read('/latin1');
  assert.match(stdout, /Niño año café acción/);
});

test('page-text: follows redirects and reports the final url', async () => {
  const { code, stdout } = await read('/redirect');
  assert.equal(code, 0);
  assert.match(stdout, /^URL: http:\/\/127\.0\.0\.1:\d+\/article$/m);
});

test('page-text: exit 5 for a non-text page such as a PDF', async () => {
  const { code, stderr } = await read('/pdf');
  assert.equal(code, 5);
  assert.match(stderr, /not a text page/);
});

test('page-text: exit 4 on 404, and suggests the browser skill on a likely block (403)', async () => {
  const missing = await read('/missing');
  assert.equal(missing.code, 4);
  assert.match(missing.stderr, /404/);

  const blocked = await read('/forbidden');
  assert.equal(blocked.code, 4);
  assert.match(blocked.stderr, /web-browser skill/);
});

test('page-text: exit 4 when the site is unreachable', async () => {
  const closed = await listen(() => {});
  const dead = closed.url;
  await new Promise((resolve) => closed.server.close(resolve));
  const { code, stderr } = await run(PAGE_TEXT, [dead]);
  assert.equal(code, 4);
  assert.match(stderr, /Could not fetch/);
});

test('page-text: exit 1 for bad usage and for non-http(s) URLs', async () => {
  assert.equal((await run(PAGE_TEXT, [])).code, 1);
  assert.equal((await run(PAGE_TEXT, ['not a url'])).code, 1);
  assert.equal((await run(PAGE_TEXT, ['file:///etc/passwd'])).code, 1);
  assert.equal((await run(PAGE_TEXT, [`${site.url}/article`, '10'])).code, 1); // maxChars too small
});
