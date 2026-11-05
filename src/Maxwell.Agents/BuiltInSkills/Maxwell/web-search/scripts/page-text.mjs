#!/usr/bin/env node
// Fetch a web page and print its readable text, so it can be read without a browser or
// dumping raw HTML into context.
//
//   node page-text.mjs <url> [maxChars]        (maxChars: default 6000, at most 50000)
//
// Works on pages whose content is in the HTML. Pages that build their content with JavaScript
// come back nearly empty: use the web-browser skill for those. Needs Node.js 18+.
//
// Exit codes:  0 text printed | 1 bad usage | 4 could not fetch the page (network or HTTP error)
//              5 the URL is not a text/HTML page

const TIMEOUT_MS = 20_000;
const MAX_BYTES = 2_000_000;
const DEFAULT_CHARS = 6000;
const HARD_CAP_CHARS = 50_000;
const THIN_PAGE_CHARS = 200;

function fail(code, message) {
  console.error(message);
  process.exit(code);
}

const [rawUrl, rawMax] = process.argv.slice(2);
if (!rawUrl) fail(1, 'Usage: page-text.mjs <url> [maxChars]');

let url;
try {
  url = new URL(rawUrl);
} catch {
  fail(1, `Not a valid URL: ${rawUrl}`);
}
if (url.protocol !== 'http:' && url.protocol !== 'https:') fail(1, `Only http and https URLs are supported, not ${url.protocol}`);

let maxChars = DEFAULT_CHARS;
if (rawMax !== undefined) {
  maxChars = Number(rawMax);
  if (!Number.isInteger(maxChars) || maxChars < 200) fail(1, 'maxChars must be a whole number of at least 200.');
  maxChars = Math.min(maxChars, HARD_CAP_CHARS);
}

let response;
try {
  response = await fetch(url, {
    headers: {
      Accept: 'text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5',
      'User-Agent': 'Mozilla/5.0 (compatible; maxwell-cli)',
    },
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
} catch (error) {
  const reason = error?.cause?.code ?? error?.cause?.message ?? error?.message ?? 'unknown error';
  fail(4, `Could not fetch ${url} (${reason}).`);
}

if (!response.ok) {
  const blocked = [401, 403, 429, 503].includes(response.status);
  fail(4, `${url} answered ${response.status} ${response.statusText}.` +
    (blocked ? ' The site may be blocking automated requests; try the web-browser skill instead.' : ''));
}

const contentType = (response.headers.get('content-type') ?? '').toLowerCase();
const isHtml = contentType.includes('html') || contentType === '';
const isText = contentType.startsWith('text/');
if (!isHtml && !isText) fail(5, `${url} is not a text page (content-type: ${contentType}).`);

// Read at most MAX_BYTES so a huge file can't flood memory.
const chunks = [];
let received = 0;
const reader = response.body.getReader();
while (received < MAX_BYTES) {
  const { done, value } = await reader.read();
  if (done) break;
  chunks.push(value);
  received += value.length;
}
await reader.cancel().catch(() => {});
const bytes = Buffer.concat(chunks).subarray(0, MAX_BYTES);

function decode(buffer) {
  const header = /charset=["']?([\w-]+)/i.exec(contentType)?.[1];
  const sniffed = /<meta[^>]+charset=["']?([\w-]+)/i.exec(buffer.subarray(0, 2048).toString('latin1'))?.[1];
  for (const label of [header, sniffed, 'utf-8']) {
    if (!label) continue;
    try {
      return new TextDecoder(label).decode(buffer);
    } catch {
      // unknown label: try the next one
    }
  }
  return buffer.toString('utf8');
}

const NAMED_ENTITIES = {
  amp: '&', lt: '<', gt: '>', quot: '"', apos: "'",
  ndash: '–', mdash: '—', hellip: '…', lsquo: '‘', rsquo: '’', ldquo: '“', rdquo: '”', bull: '•', euro: '€', trade: '™',
};

// The HTML Latin-1 entity set (U+00A0..U+00FF, in order): accented letters, ñ, ¿, ¡, «, », ... Entity names are
// case-sensitive (&Eacute; is É, &eacute; is é), and older Spanish/French pages rely on these constantly.
const LATIN1 =
  'nbsp iexcl cent pound curren yen brvbar sect uml copy ordf laquo not shy reg macr deg plusmn sup2 sup3 acute micro ' +
  'para middot cedil sup1 ordm raquo frac14 frac12 frac34 iquest Agrave Aacute Acirc Atilde Auml Aring AElig Ccedil ' +
  'Egrave Eacute Ecirc Euml Igrave Iacute Icirc Iuml ETH Ntilde Ograve Oacute Ocirc Otilde Ouml times Oslash Ugrave ' +
  'Uacute Ucirc Uuml Yacute THORN szlig agrave aacute acirc atilde auml aring aelig ccedil egrave eacute ecirc euml ' +
  'igrave iacute icirc iuml eth ntilde ograve oacute ocirc otilde ouml divide oslash ugrave uacute ucirc uuml yacute ' +
  'thorn yuml';
LATIN1.split(' ').forEach((name, i) => {
  NAMED_ENTITIES[name] = String.fromCharCode(0xa0 + i);
});
NAMED_ENTITIES.nbsp = ' ';

function decodeEntities(text) {
  return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z][a-z0-9]*);/gi, (whole, body) => {
    if (body[0] === '#') {
      const code = body[1].toLowerCase() === 'x' ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
      try {
        return String.fromCodePoint(code);
      } catch {
        return whole;
      }
    }
    return NAMED_ENTITIES[body] ?? NAMED_ENTITIES[body.toLowerCase()] ?? whole;
  });
}

function extractText(html) {
  const title = decodeEntities((/<title[^>]*>([\s\S]*?)<\/title>/i.exec(html)?.[1] ?? '').replace(/\s+/g, ' ').trim());

  let body = html.replace(/<!--[\s\S]*?-->/g, ' ');
  // Whole blocks that are never article text.
  for (const tag of ['script', 'style', 'noscript', 'svg', 'template', 'iframe', 'nav', 'footer', 'aside', 'head']) {
    body = body.replace(new RegExp(`<${tag}\\b[\\s\\S]*?<\\/${tag}\\s*>`, 'gi'), ' ');
  }
  // Block-level tags become line breaks; every other tag disappears.
  body = body.replace(/<\/?(?:p|div|br|li|ul|ol|h[1-6]|tr|table|section|article|main|header|blockquote|pre|figure|figcaption|dd|dt|dl)\b[^>]*>/gi, '\n');
  body = body.replace(/<[^>]+>/g, '');

  const text = decodeEntities(body)
    .split('\n')
    .map((line) => line.replace(/\s+/g, ' ').trim())
    .filter((line) => line.length > 0)
    .join('\n');

  return { title, text };
}

const { title, text } = extractText(decode(bytes));

console.log(`URL: ${response.url || url}`);
if (title) console.log(`Title: ${title}`);
console.log('');

if (text.length <= maxChars) {
  console.log(text);
} else {
  console.log(text.slice(0, maxChars));
  console.log(`\n[Truncated: showing ${maxChars} of ${text.length} characters. Ask for more with a larger maxChars, up to ${HARD_CAP_CHARS}.]`);
}

if (text.length < THIN_PAGE_CHARS) {
  console.log('\n[Very little text found. The page probably builds its content with JavaScript: use the web-browser skill for it.]');
}
