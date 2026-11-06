import { readFile, readdir } from 'node:fs/promises'
import { extname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const protocolPath = fileURLToPath(new URL('../../Services/WebUiProtocol.cs', import.meta.url))
const operationsPath = fileURLToPath(new URL('../src/host-operations.ts', import.meta.url))
const sourceRoot = fileURLToPath(new URL('../src', import.meta.url))

const quotedValues = text => [...text.matchAll(/["']([A-Za-z][A-Za-z0-9]*)["']/g)].map(match => match[1])
const unique = values => [...new Set(values)].sort()
const difference = (left, right) => left.filter(value => !right.includes(value))

const protocol = await readFile(protocolPath, 'utf8')
const protocolBlock = protocol.match(/Operations\s*=[\s\S]*?\{([\s\S]*?)\};/)?.[1]
if (!protocolBlock) throw new Error('Could not locate the C# WebUiProtocol operation allowlist.')

const generated = await readFile(operationsPath, 'utf8')
const generatedBlock = generated.match(/hostOperations\s*=\s*\[([\s\S]*?)\]\s*as const/)?.[1]
if (!generatedBlock) throw new Error('Could not locate the TypeScript host operation contract.')

const csharpOperations = quotedValues(protocolBlock)
const typeScriptOperations = quotedValues(generatedBlock)
if (unique(csharpOperations).length !== csharpOperations.length) throw new Error('C# host operation allowlist contains duplicates.')
if (unique(typeScriptOperations).length !== typeScriptOperations.length) throw new Error('TypeScript host operation contract contains duplicates.')

const missingInTypeScript = difference(unique(csharpOperations), unique(typeScriptOperations))
const missingInCSharp = difference(unique(typeScriptOperations), unique(csharpOperations))
if (missingInTypeScript.length || missingInCSharp.length) {
  throw new Error(`Host operation contract drift. Missing in TypeScript: ${missingInTypeScript.join(', ') || 'none'}; missing in C#: ${missingInCSharp.join(', ') || 'none'}.`)
}

async function sourceFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true })
  const files = []
  for (const entry of entries) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) files.push(...await sourceFiles(path))
    else if (['.ts', '.tsx'].includes(extname(entry.name)) && entry.name !== 'host-operations.ts') files.push(path)
  }
  return files
}

const unauthorizedCalls = []
for (const path of await sourceFiles(sourceRoot)) {
  const source = await readFile(path, 'utf8')
  for (const match of source.matchAll(/\b(?:run|send|act|action)\(\s*["']([A-Za-z][A-Za-z0-9]*)["']/g)) {
    if (!typeScriptOperations.includes(match[1])) unauthorizedCalls.push(`${path}: ${match[1]}`)
  }
}
if (unauthorizedCalls.length) throw new Error(`UI calls operations outside the host allowlist:\n${unauthorizedCalls.join('\n')}`)

console.log(`PASS host contract: ${typeScriptOperations.length} unique operations match C# and all static UI calls are authorized.`)
