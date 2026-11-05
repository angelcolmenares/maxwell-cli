#!/usr/bin/env node
// Search a SearXNG instance and print compact, numbered results.
//
//   node searxng-search.mjs "search terms" [--limit 8] [--time day|month|year]
//                           [--lang en] [--category general] [--page 1]
//
// The instance address comes from the SEARXNG_URL environment variable, e.g. http://localhost:8888
// Needs Node.js 18+ (built-in fetch). No other dependencies.
//
// Exit codes:  0 results printed | 1 bad usage | 2 no results
//              3 SEARXNG_URL not set | 4 SearXNG unreachable or refused the request

const TIMEOUT_MS = 20_000;
const SNIPPET_CHARS = 240;

function fail(code, message) {
  console.error(message);
  process.exit(code);
}

function parseArgs(argv) {
  const options = { limit: 8, time: undefined, lang: undefined, category: undefined, page: 1 };
  const words = [];
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const takeValue = () => {
      if (i + 1 >= argv.length) fail(1, `Missing value for ${arg}.`);
      return argv[++i];
    };
    switch (arg) {
      case '--limit': {
        const n = Number(takeValue());
        if (!Number.isInteger(n) || n < 1 || n > 50) fail(1, '--limit must be a whole number from 1 to 50.');
        options.limit = n;
        break;
      }
      case '--page': {
        const n = Number(takeValue());
        if (!Number.isInteger(n) || n < 1) fail(1, '--page must be a whole number of 1 or more.');
        options.page = n;
        break;
      }
      case '--time': {
        const value = takeValue();
        if (!['day', 'month', 'year'].includes(value)) fail(1, '--time must be day, month or year.');
        options.time = value;
        break;
      }
      case '--lang':
        options.lang = takeValue();
        break;
      case '--category':
        options.category = takeValue();
        break;
      default:
        if (arg.startsWith('--')) fail(1, `Unknown option ${arg}.`);
        words.push(arg);
    }
  }
  return { query: words.join(' ').trim(), options };
}

const collapse = (text) => String(text ?? '').replace(/\s+/g, ' ').trim();
const shorten = (text, max) => (text.length <= max ? text : `${text.slice(0, max - 1)}…`);

const { query, options } = parseArgs(process.argv.slice(2));
if (!query) fail(1, 'Usage: searxng-search.mjs "search terms" [--limit N] [--time day|month|year] [--lang xx] [--category c] [--page N]');

const base = (process.env.SEARXNG_URL ?? '').trim().replace(/\/+$/, '');
if (!base) fail(3, 'SEARXNG_URL is not set, so there is no SearXNG instance to use.');
if (!/^https?:\/\//i.test(base)) fail(3, `SEARXNG_URL must start with http:// or https:// (it is "${base}").`);

const params = new URLSearchParams({ q: query, format: 'json', pageno: String(options.page) });
if (options.time) params.set('time_range', options.time);
if (options.lang) params.set('language', options.lang);
if (options.category) params.set('categories', options.category);

let response;
try {
  response = await fetch(`${base}/search?${params}`, {
    headers: { Accept: 'application/json', 'User-Agent': 'Mozilla/5.0 (compatible; maxwell-cli)' },
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
} catch (error) {
  const reason = error?.cause?.code ?? error?.cause?.message ?? error?.message ?? 'unknown error';
  fail(4, `Could not reach SearXNG at ${base} (${reason}). Is it running, and is SEARXNG_URL correct?`);
}

const JSON_HINT =
  "JSON output is probably not enabled: add 'json' to search.formats in the instance's settings.yml " +
  "(and restart it). If it is enabled, the instance's bot limiter may be refusing API requests.";

if (response.status === 403) fail(4, `SearXNG refused the request (403 Forbidden). ${JSON_HINT}`);
if (!response.ok) fail(4, `SearXNG answered ${response.status} ${response.statusText}.`);

let data;
try {
  data = JSON.parse(await response.text());
} catch {
  fail(4, `SearXNG did not return JSON. ${JSON_HINT}`);
}

const results = Array.isArray(data.results) ? data.results : [];
const unresponsive = (Array.isArray(data.unresponsive_engines) ? data.unresponsive_engines : [])
  .map((entry) => (Array.isArray(entry) ? `${entry[0]} (${entry[1]})` : String(entry)));
const engineNote = unresponsive.length > 0 ? `Engines that did not respond: ${unresponsive.join(', ')}.` : '';

if (results.length === 0) {
  console.log(`No results for "${query}".${engineNote ? ` ${engineNote}` : ''}`);
  process.exit(2);
}

// Instant answers some engines provide (strings, or objects with an "answer" field depending on version).
for (const answer of Array.isArray(data.answers) ? data.answers : []) {
  const text = collapse(typeof answer === 'string' ? answer : answer?.answer);
  if (text) console.log(`Answer: ${text}\n`);
}

results.slice(0, options.limit).forEach((result, index) => {
  const date = result.publishedDate ? ` (${String(result.publishedDate).slice(0, 10)})` : '';
  console.log(`${index + 1}. ${collapse(result.title) || '(no title)'}${date}`);
  console.log(`   ${result.url}`);
  const snippet = shorten(collapse(result.content), SNIPPET_CHARS);
  if (snippet) console.log(`   ${snippet}`);
});

const shown = Math.min(options.limit, results.length);
console.log(`\nShowing ${shown} of ${results.length} results.${engineNote ? ` ${engineNote}` : ''}`);
