import { createServer } from 'vite'
import React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'

const server = await createServer({ server: { middlewareMode: true }, plugins: [{
  name: 'fixture-exports', enforce: 'pre', transform(code, id) {
    if (id.replaceAll('\\', '/').endsWith('/src/App.tsx')) return code + '\nexport { UninstallConfirmation, ResidualDialog, words };'
  },
}] })

try {
  const ui = await server.ssrLoadModule('/src/App.tsx')
  const source = readFileSync('src/App.tsx', 'utf8')
  const css = [...source.matchAll(/import '\.\/([^']+\.css)'/g)]
    .map(match => readFileSync('src/' + match[1], 'utf8')).join('\n')
  const output = '../../../_build/reference-previews'
  mkdirSync(output, { recursive: true })
  const apps = ['Python 3.15.2 (64-bit)', 'Python Launcher', 'Spotify'].map((name, index) => ({
    id: String(index), name, size: ['196.1 MB', '1.5 MB', '424.3 MB'][index], icon: null,
  }))
  const [python, launcher, spotify] = apps
  const items = [
    { index: 0, title: 'Spotify', path: 'HKCU\\Software\\Spotify [64-bit]', kind: 'RegistryKey', canClean: true, selected: true },
    { index: 1, title: 'Spotify', path: 'C:\\Users\\Preview\\AppData\\Local\\Spotify\\Cache\\track.bin', kind: 'File', canClean: true, selected: true },
  ]
  const residual = { open: true, items, summary: [], groups: [{ app: spotify, offset: 0, count: items.length }] }
  const queue = { isRunning: true, total: 3, processed: 1, app: launcher, operationState: 'Running', logs: [], results: [],
    queueItems: [{ app: python, state: 'Completed' }, { app: launcher, state: 'Running' }, { app: spotify, state: 'Idle' }],
  }
  const run = async () => true
  for (const language of ['en', 'ar']) {
    const lang = ui.words[language]
    const examples = {
      confirmation: [ui.UninstallConfirmation, { items: apps, close() {}, execute: run }],
      progress: [ui.ProgressDialog, { op: queue }],
      verified: [ui.ProgressDialog, { op: { ...queue, processed: 3, successful: 3, state: 'Succeeded', operationState: 'Completed', queueItems: apps.map(app => ({ app, state: 'Completed' })) } }],
      review: [ui.ResidualDialog, { residual }],
      cleaning: [ui.ResidualDialog, { residual: { ...residual, cleaning: true, deleteProcessed: 1, deleteTotal: 2, deleteCurrentPath: items[1].path } }],
      results: [ui.ProgressDialog, { op: { ...queue, isRunning: false, successful: 3, state: 'Succeeded', operationState: 'Completed', queueItems: apps.map(app => ({ app, state: 'Completed' })), residual: { summary: [], items: [], deletedCount: 4, deletedRegistryCount: 3, deletedFileBytes: 49 * 1024 ** 2 } } }],
      failure: [ui.ProgressDialog, { op: { ...queue, isRunning: false, successful: 0, failed: 1, total: 1, state: 'PartialFailure', operationState: 'Failed', queueItems: [{ app: spotify, state: 'Failed', message: 'Removal was not observed.', failureCode: 'RemovalNotObserved' }] } }],
    }
    for (const [name, [Component, props]] of Object.entries(examples)) {
      const markup = renderToStaticMarkup(React.createElement(Component, { lang, run, ...props }))
      writeFileSync(output + '/' + name + '-' + language + '.html', '<!doctype html><meta charset="utf-8"><style>' + css +
        '\nbody{margin:0;background:#15171a}.modal-backdrop{position:fixed;inset:0}*{animation:none!important}</style><body dir="' +
        (language === 'ar' ? 'rtl' : 'ltr') + '">' + markup)
    }
  }
} finally {
  await server.close()
}
