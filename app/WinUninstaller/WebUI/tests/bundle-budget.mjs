import { gzipSync } from 'node:zlib'
import { readdirSync, readFileSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../dist/', import.meta.url))
const files = []
const visit = directory => {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) visit(path)
    else files.push(path)
  }
}
visit(root)

const rows = files.map(path => {
  const bytes = readFileSync(path)
  return { path: relative(root, path).replaceAll('\\', '/'), bytes: bytes.length, gzip: gzipSync(bytes).length }
})
const sum = (extension, field) => rows.filter(row => row.path.endsWith(extension)).reduce((total, row) => total + row[field], 0)
const totals = {
  jsBytes: sum('.js', 'bytes'), jsGzip: sum('.js', 'gzip'),
  cssBytes: sum('.css', 'bytes'), cssGzip: sum('.css', 'gzip'),
  startupGzip: rows.filter(row => /\.(?:html|js|css)$/.test(row.path)).reduce((total, row) => total + row.gzip, 0)
}
const limits = { jsBytes: 420 * 1024, jsGzip: 130 * 1024, cssBytes: 120 * 1024, cssGzip: 25 * 1024, startupGzip: 160 * 1024 }
const failures = Object.entries(limits).filter(([name, limit]) => totals[name] > limit)
if (failures.length) {
  for (const [name, limit] of failures) console.error(`FAIL ${name}: ${totals[name]} > ${limit}`)
  process.exitCode = 1
} else {
  console.log(`PASS bundle budget: JS ${totals.jsBytes} B (${totals.jsGzip} gzip), CSS ${totals.cssBytes} B (${totals.cssGzip} gzip), startup ${totals.startupGzip} B gzip.`)
}
