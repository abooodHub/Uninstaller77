import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const siteRoot = new URL('../', import.meta.url);
const [script, document] = await Promise.all([
  readFile(new URL('app.js', siteRoot), 'utf8'),
  readFile(new URL('index.html', siteRoot), 'utf8'),
]);

test('translations use text-only DOM updates', () => {
  const unsafeSinks = [
    /\.innerHTML\b/,
    /\.outerHTML\b/,
    /\.insertAdjacentHTML\b/,
    /\bdocument\.write\s*\(/,
    /\beval\s*\(/,
    /\bnew\s+Function\s*\(/,
  ];

  for (const sink of unsafeSinks) {
    assert.doesNotMatch(script, sink);
  }
  assert.doesNotMatch(document, /data-i18n-html\b/);
});

test('every translated DOM key has an Arabic value', () => {
  const domKeys = new Set(
    [...document.matchAll(/data-i18n(?:-alt)?="([^"]+)"/g)].map((match) => match[1]),
  );
  const arabicKeys = new Set(
    [...script.matchAll(/^\s*"([^"]+)"\s*:/gm)].map((match) => match[1]),
  );
  const missing = [...domKeys].filter((key) => !arabicKeys.has(key));

  assert.deepEqual(missing, []);
});
