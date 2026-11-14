// Component/accessibility contracts only; SSR cannot prove native window layout.
import { createServer } from 'vite'
import React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
const server = await createServer({ server: { middlewareMode: true }, plugins: [{
  name: 'audit-components', enforce: 'pre', transform(code, id) {
    if (id.replaceAll('\\', '/').endsWith('/src/App.tsx'))
      return code + '\nexport { words, defaultState, AppRow, WindowsAppsPage, SettingsDialog, ExtensionsPage, HistoryPage, ResidualDialog };'
  }
}] })
let passed = 0
const run = async () => true
const render = (Component, props) => renderToStaticMarkup(React.createElement(Component, props))
const check = (name, test) => { test(); passed++; console.log('PASS ' + name) }
try {
  const ui = await server.ssrLoadModule('/src/App.tsx')
  const { InstallationPage } = await server.ssrLoadModule('/src/components/InstallationPage.tsx')
  const { installationWords } = await server.ssrLoadModule('/src/installation-strings.ts')
  const boundaryModule = await server.ssrLoadModule('/src/ErrorBoundary.tsx')
  check('Render failure boundary exposes a keyboard action and bilingual alert surface', () => {
    const Boundary = boundaryModule.default
    const boundary = new Boundary({ children: React.createElement('span', null, 'normal') })
    boundary.state = Boundary.getDerivedStateFromError(new Error('owned render fixture'))
    const html = render(() => boundary.render(), {})
    assert.match(html, /role="alert"/)
    assert.match(html, /type="button"/)
    assert.ok(html.includes('Reload interface'))
  })
  check('Arabic and English expose the same translation keys', () => {
    assert.deepEqual(Object.keys(ui.words.ar).sort(), Object.keys(ui.words.en).sort())
    assert.deepEqual(Object.keys(ui.words.ar.unregistered).sort(), Object.keys(ui.words.en.unregistered).sort())
    assert.deepEqual(Object.keys(ui.words.ar.startup).sort(), Object.keys(ui.words.en.startup).sort())
  })
  for (const language of ['ar', 'en']) {
    const lang = ui.words[language]
    const protection = JSON.parse(readFileSync(new URL(`../../Resources/Strings.${language}.json`, import.meta.url), 'utf8').replace(/^\uFEFF/, ''))
    check(language + ': queue protection settings show the opt-in and recovery limitation', () => {
      const html = render(ui.SettingsDialog, { lang, run, state: { language, queueProtectionSettings: {
        enabled: true, label: protection.protectionSetting, hint: protection.protectionHint } } })
      assert.match(html, /type="checkbox"[^>]*checked=""/)
      assert.ok(html.includes(protection.protectionHint))
      assert.match(html, /role="dialog"/)
      assert.match(html, /aria-modal="true"/)
      assert.match(html, /tabindex="-1"/)
    })
    check(language + ': protection preparation and warning are visible in the queue', () => {
      const op = { total: 1, successful: 1, state: 'Succeeded', protectionWarning: protection.protectionWarning,
        protectionStage: protection.protectionPreparing, canOpenSystemRestore: true, openSystemRestore: protection.protectionOpenSystemRestore }
      const running = render(ui.ProgressDialog, { lang, run, op: { ...op, isRunning: true, preparingProtection: true } })
      assert.ok(running.includes(protection.protectionPreparing))
      assert.ok(!running.includes(protection.protectionOpenSystemRestore))
      const done = render(ui.ProgressDialog, { lang, run, op: { ...op, isRunning: false } })
      assert.ok(done.includes(protection.protectionWarning))
      assert.match(done, /simple-result-summary\s*" role="status"/)
    })
    check(language + ': History separates the restore-point result from registry backup details', () => {
      const record = { id: 1, outcome: 'QueueProtection', name: protection.outcomeQueueProtection, status: protection.protectionFailed,
        date: '2026-01-01 01:02:03', residualStatus: protection.protectionExportCounts, notes: 'Owned fixture 0001.reg' }
      const html = render(ui.HistoryPage, { lang, run, onClear: () => {}, page: { records: [record], details: record,
        restorePointLabel: protection.protectionRestorePoint, registryBackupLabel: protection.protectionRegistryBackup } })
      assert.ok(html.includes(record.status) && html.includes(record.notes) && html.includes(protection.protectionRegistryBackup))
      assert.match(html, /data-outcome="QueueProtection"/)
    })
    check(language + ': a peer already removed by the previous item is skipped, without a false failed row or successful removal', () => {
      const html=render(ui.ProgressDialog,{lang,run,op:{isRunning:false,state:'CompletedWithSkips',operationState:'Completed',app:{id:'peer',name:'Owned already-absent peer'},total:2,processed:1,failed:0,successful:1,skipped:1,results:[{},{}],queueItems:[{app:{id:'parent',name:'Owned parent'},state:'Completed'},{app:{id:'peer',name:'Owned already-absent peer'},state:'NotInstalled',message:'The installation registration is already absent.'}]}})
      assert.ok(html.includes(lang.stages.NotInstalled))
      assert.match(html,/result-notinstalled/)
      assert.doesNotMatch(html,/result-failed/)
      assert.match(html,/simple-result-summary has-failures/)
      assert.ok(html.includes(language === 'ar' ? 'تمت إزالة 1 من 2' : 'Removed 1 of 2'))
      assert.doesNotMatch(html,/stop-queue/)
    })
    check(language + ': a previous failed application retains its visible cause after the selected next app succeeds', () => {
      const html=render(ui.ProgressDialog,{lang,run,op:{isRunning:false,state:'PartialFailure',operationState:'Failed',app:{id:'next',name:'Owned next'},failureCode:'None',total:2,processed:2,failed:1,successful:1,skipped:0,results:[{},{}],queueItems:[{app:{id:'riot',name:'Owned refusal'},state:'Failed',message:'Riot Client cannot be removed while Riot games remain installed.\nVendorDependenciesInstalled\nExit code: 0'},{app:{id:'next',name:'Owned next'},state:'Completed'}]}})
      assert.match(html,/simple-result-reason/)
      assert.ok(html.includes(language==='ar' ? 'لا يمكن إزالة Riot Client ما دامت ألعاب Riot مثبتة.' : 'Riot Client cannot be removed while Riot games remain installed.'))
      assert.match(html,/simple-result-row result-completed/)
    })
    check(language + ': completed queue distinguishes a failed program from skipped programs', () => {
      const html = render(ui.ProgressDialog, {lang,run,op:{isRunning:false,state:'PartialFailure',operationState:'Cancelled',app:{id:'owned',name:'Owned failure'},total:3,processed:1,successful:0,failed:1,skipped:2,queueItems:[{app:{id:'owned',name:'Owned failure'},state:'Failed'},{app:{id:'skip',name:'Owned skipped'},state:'Cancelled'}]}})
      assert.match(html,/simple-result-row result-failed/)
      assert.match(html,/simple-result-row result-cancelled/)
      assert.ok(html.includes(lang.stages.Failed))
    })
    check(language + ': vendor dependency refusal is visible before opening diagnostics', () => {
      const html=render(ui.ProgressDialog,{lang,run,op:{isRunning:false,state:'PartialFailure',failureCode:'VendorDependenciesInstalled',total:2,processed:1,failed:1,skipped:1,successful:0,queueItems:[],results:[{state:'Failed',app:{id:'owned',name:'Owned refusal'},message:'Riot Client cannot be removed while Riot games remain installed.'},{state:'Skipped',app:{id:'skip',name:'Owned not started'}}]}})
      assert.ok(html.includes(language === 'ar' ? 'لا يمكن إزالة Riot Client ما دامت ألعاب Riot مثبتة.' : 'Riot Client cannot be removed while Riot games remain installed.'))
      assert.match(html,/simple-result-summary has-failures/)
      assert.doesNotMatch(html,/stop-queue/)
    })
    check(language + ': protected registrations stay visible but cannot be selected or uninstalled', () => {
      const app = { id: 'owned-protected', name: 'Owned protected fixture', canUninstall: false }
      const html = render(ui.AppRow, { app, lang, run })
      assert.match(html, /type="checkbox"[^>]*disabled=""/)
      assert.doesNotMatch(html, /class="icon-action-button trash"/)
      assert.ok(html.includes(`${lang.windowsAppsSettings}: ${app.name}`))
      assert.ok(html.includes(lang.removalBlocked) && html.includes(lang.removalBlockedReason))
      assert.doesNotMatch(render(ui.AppRow, { app: { ...app, canUninstall: true }, lang, run }), /disabled=""/)
    })
    check(language + ': Windows apps includes the All tab and excludes protected packages from its selection', () => {
      const blocked = { id: 'protected', name: 'Protected app', source: 'MSIX', isBuiltIn: true, canUninstall: false, selected: true }
      const removable = { id: 'removable', name: 'Removable app', source: 'MSIX', canUninstall: true, selected: true }
      const props = { lang, run, category: 'Store', hasInventory: true, search: '' }
      const html = render(ui.WindowsAppsPage, { ...props, items: [blocked, removable] })
      assert.ok(html.includes(`aria-current="page">${lang.allWindowsApps}</button>`))
      assert.match(html, /aria-label="[^"]*Protected app"[^>]*disabled=""/)
      assert.ok(html.includes(lang.removalBlockedReason))
      assert.match(html, /class="workspace-selection".*>\(1\)<\/bdi>/)
      assert.doesNotMatch(render(ui.WindowsAppsPage, { ...props, items: [blocked] }), /class="workspace-selection"|class="windows-select-visible"/)
    })
    check(language + ': Windows Installer retry status and observed exit appear in the compact workflow', () => {
      const app = { id: 'owned-msi', name: 'Owned MSI fixture' }
      const op = { isRunning: true, total: 1, app, operationState: 'WaitingForMsiRetry',
        queueItems: [{ app, state: 'WaitingForMsiRetry' }] }
      assert.ok(render(ui.ProgressDialog, { lang, run, op }).includes(lang.stages.WaitingForMsiRetry))
      const finished = render(ui.ProgressDialog, { lang, run, op: { ...op, isRunning: false,
        operationState: 'Failed', failed: 1, queueItems: [], results: [{ app, state: 'Failed',
          message: 'Windows Installer failed to remove this program.', failureCode: 'MsiInstallFailure', exitCode: 1603 }] } })
      assert.match(finished, /<bdi dir="ltr">1603<\/bdi>/)
      assert.ok(finished.includes(language === 'ar' ? 'تعذّر على Windows Installer إزالة هذا البرنامج.' : 'Windows Installer failed to remove this program.'))
    })
    check(language + ': running residual maintenance freezes selection and duplicate cleanup actions', () => {
      const html=render(ui.ResidualDialog,{lang,labels:{},run,op:{successful:1},residual:{cleaning:true,deleteStopRequested:true,deleteCurrentPath:'C:\\Owned\\Fixture.bin',
        groups:[{app:{id:'owned',name:'Owned fixture'},offset:0,count:1,status:lang.results}],
        items:[{index:0,title:'Fixture.bin',path:'C:\\Owned\\Fixture.bin',canClean:true,selected:true}]}})
      assert.match(html,/type="checkbox"[^>]*disabled=""/)
      assert.match(html,/class="button residual-danger" disabled=""/)
      assert.match(html,/class="button quiet" disabled=""/)
      assert.match(html, /<bdi dir="ltr"[^>]*>C:\\Owned\\Fixture.bin<\/bdi>/)
      assert.match(html, new RegExp(`dir="${language === 'ar' ? 'rtl' : 'ltr'}"`))
      assert.ok(!html.includes(lang.residualDeleteConfirm))
    })
    check(language + ': empty residual review explains why there is nothing to select', () => {
      const html = render(ui.ResidualDialog, { lang, run, residual: {
        groups: [{ app: { id: 'cross-device', name: 'Cross Device' }, offset: 0, count: 0,
          status: lang.noLeftovers }], items: [],
      } })
      assert.ok(html.includes('Cross Device') && html.includes(lang.noLeftovers))
      assert.ok(html.includes(lang.residualNothingToDelete) && html.includes(lang.close))
      assert.doesNotMatch(html, /type="checkbox"/)
      assert.match(html, /class="button residual-danger" disabled=""/)
    })
    check(language + ': leftover selection counts only eligible items and leaves review-only rows visible', () => {
      const file = { index: 0, title: 'owned.bin', path: 'C:\\Owned\\owned.bin', kind: 'File', canClean: true, selected: false }
      const informational = { index: 1, title: 'Shared service', path: 'HKLM\\Shared', kind: 'Service', canClean: false, selected: true }
      const group = { app: { id: 'owned', name: 'Owned fixture' }, offset: 0, count: 2, status: '' }
      const mixed = render(ui.ResidualDialog, { lang, run, residual: { items: [file, informational], groups: [group] } })
      assert.ok(mixed.includes(lang.residualSelectAll) && mixed.includes(lang.residualReviewOnly))
      assert.match(mixed, /<bdi dir="ltr">0\/1<\/bdi>/)
      assert.equal((mixed.match(/type="checkbox"/g) || []).length, 4)
      assert.match(mixed, /class="button residual-danger" disabled=""/)

      const selected = render(ui.ResidualDialog, { lang, run, residual: { items: [{ ...file, selected: true }, informational], groups: [group] } })
      assert.match(selected, /<bdi dir="ltr">1\/1<\/bdi>/)
      assert.doesNotMatch(selected, /class="button residual-danger" disabled=""/)

      const reviewOnly = render(ui.ResidualDialog, { lang, run, residual: { items: [informational], groups: [{ ...group, count: 1 }] } })
      assert.ok(reviewOnly.includes('HKLM\\Shared') && reviewOnly.includes(lang.residualNothingToDelete))
      assert.doesNotMatch(reviewOnly, /type="checkbox"/)
      assert.match(reviewOnly, /class="button residual-danger" disabled=""/)
    })
    check(language + ': active queue draws measured progress and scanning hides the ring', () => {
      const app = { id: 'owned', name: 'Owned fixture' }
      const op = { isRunning:true,state:'Running',total:4,processed:1,app,results:[],queueItems:[{app,state:'Running'}] }
      const running = render(ui.ProgressDialog, {lang,run,op})
      assert.match(running, /simple-progress is-running/)
      assert.match(running, /aria-valuenow="25"/)
      assert.equal((running.match(/role="progressbar"/g) || []).length, 1)
      assert.doesNotMatch(running, /batch-counters/)
      const scanning = render(ui.ProgressDialog, {lang,run,op:{...op,operationState:'ScanningResiduals',residual:{scannedLocations:7}}})
      assert.ok(scanning.includes(lang.scanLeftovers))
      assert.doesNotMatch(scanning, /role="progressbar"/)
      assert.match(scanning, /stop-queue"[^>]*disabled=""/)
    })
    check(language + ': empty cleanup stays in queue results without a delete action', () => {
      const folder = protection.residualFolderNotRecorded
      const html = render(ui.ProgressDialog, {lang, run, op:{isRunning:false,state:'Succeeded',total:1,processed:1,
        successful:1,results:[],queueItems:[],residual:{foundSelectable:false,scannedLocations:7,
          summary:[{id:'scope',name:'Owned scope fixture',status:lang.noLeftovers,folder}]}}})
      assert.match(html, /simple-result-summary\s*" role="status"/)
      assert.doesNotMatch(html, /residual-danger|reference-result-metrics/)
    })
    check(language + ': unregistered review requires explicit consent and isolates its exact LTR scope', () => {
      const html = render(ui.UnregisteredDialog, {lang,run,review:{state:'Review',planId:'owned-review',root:'C:\\Owned\\App',target:'C:\\Owned\\App\\Owned.exe',count:1,bytes:1024,files:[{path:'Owned.exe',bytes:1024}]}})
      assert.match(html,/data-testid="unregistered-confirm"[^>]*disabled=""/)
      assert.match(html,/<bdi dir="ltr">C:\\Owned\\App<\/bdi>/)
      assert.ok(html.includes(lang.unregistered.explanation))
      assert.doesNotMatch(html,/data-testid="unregistered-undo"/)
    })
    check(language + ': completed permanent deletion has no restore or further delete action', () => {
      const html = render(ui.UnregisteredDialog, {lang,run,review:{state:'Deleted',planId:'owned-review',root:'C:\\Owned\\App',count:1,deletedFiles:1,files:[]}})
      assert.doesNotMatch(html,/data-testid="unregistered-undo"/)
      assert.doesNotMatch(html,/data-testid="unregistered-confirm"/)
      assert.ok(html.includes(lang.unregistered.deleted))
      assert.ok(html.includes(lang.unregistered.historyNote))
    })
    check(language + ': incomplete deletion retains accurate counts and requires consent for retry', () => {
      const html=render(ui.UnregisteredDialog,{lang,run,review:{state:'DeleteIncomplete',planId:'owned-review',count:4,deletedFiles:1,remainingFiles:3,canRetryDelete:true,files:[]}})
      assert.match(html,/data-testid="unregistered-retry-delete"[^>]*disabled=""/)
      assert.match(html,/<bdi dir="ltr">1\/4<\/bdi>/)
      assert.ok(html.includes(lang.unregistered.incomplete))
      assert.doesNotMatch(html,/data-testid="unregistered-undo"/)
    })
    check(language + ': unsupported unregistered layouts cannot trigger a file operation', () => {
      const html = render(ui.UnregisteredDialog, {lang,run,review:{state:'Unsupported',error:'UnregisteredApp:MixedData'}})
      assert.doesNotMatch(html,/data-testid="unregistered-confirm"|data-testid="unregistered-undo"/)
      assert.match(html,/role="alert"/)
    })
    check(language + ': prior stored application copy is explicitly identified before permanent deletion', () => {
      const html=render(ui.UnregisteredDialog,{lang,run,review:{state:'Review',fromBackup:true,planId:'owned-review',count:4,files:[]}})
      assert.ok(html.includes(lang.unregistered.storedNotice))
      assert.ok(html.includes(lang.unregistered.consent))
      assert.doesNotMatch(html,/data-testid="unregistered-undo"/)
    })
    check(language + ': settings expose language and the saved motion preference', () => {
      const html = render(ui.SettingsDialog, { state: { language, reduceMotion: true }, lang, run })
      assert.match(html, /type="checkbox"[^>]*checked=""/)
      assert.ok(html.includes(protection.reduceMotion))
      assert.match(html, /English/); assert.match(html, /العربية/)
    })
    check(language + ': settings offer the translated product website action', () => {
      const html = render(ui.SettingsDialog, { state: { language }, lang, run })
      assert.ok(html.includes(lang.productWebsite))
      assert.match(html, /<button[^>]*class="button quiet"/)
      assert.doesNotMatch(html, /https?:\/\/example|target="_self"/)
    })
    check(language + ': settings offer restoration without the Tools page', () => {
      const html = render(ui.SettingsDialog, { state: { language }, lang, run })
      const restoreButton = (html.match(/<button[^>]*>[\s\S]*?<\/button>/g) || [])
        .find(button => button.includes(lang.restoreLeftoverBackup))
      assert.match(restoreButton || '', /^<button type="button" class="button quiet"/)
    })
    check(language + ': sidebar contains Startup apps and the existing sections', () => {
      const original = { ...ui.defaultState }
      try {
        Object.assign(ui.defaultState, { language, direction: language === 'ar' ? 'rtl' : 'ltr' })
        const html = render(ui.default, {})
        const sidebar = html.slice(0, html.indexOf('<main'))
        assert.equal((sidebar.match(/class="workspace-nav-button/g) || []).length, 7)
        for (const label of [lang.programs, lang.windowsApps, lang.browserExtensions, lang.startup.title, lang.uninstallHistory, lang.settings, installationWords[language].title])
          assert.ok(sidebar.includes(label))
      } finally { Object.assign(ui.defaultState, original) }
    })
    check(language + ': installation monitoring separates observations from linked evidence and exposes the finish state', () => {
      const monitor = installationWords[language]
      const active = { id: 'owned', root: 'C:\\Owned', state: 'Monitoring', issues: [], count: 2, baselineFiles: 1, baselineKeys: 0, changes: [
        { kind: 'FileEvent', change: 'Created', path: 'C:\\Owned\\background.txt', phase: 'During', association: 'Observed' },
        { kind: 'File', change: 'Created', path: 'C:\\Owned\\Program.exe', phase: 'After', association: 'RegistrationLinked' },
      ] }
      const html = render(InstallationPage, { page: { busy: true, active, reports: [] }, language, run })
      assert.ok(html.includes(monitor.states.Monitoring) && html.includes(monitor.observed) && html.includes(monitor.linked))
      assert.ok(html.includes(monitor.cancel) && !html.includes(monitor.export))
      assert.ok(html.includes('dir="ltr">C:\\Owned\\background.txt'))
      const finishing = render(InstallationPage, { page: { busy: true, finishing: true, active, reports: [] }, language, run })
      assert.ok(finishing.includes(monitor.states.Finishing))
      assert.match(finishing, /class="button quiet" disabled=""/)
      const completed = render(InstallationPage, { page: { busy: false, finishing: false, active: { ...active, state: 'Completed' }, reports: [] }, language, run })
      assert.ok(completed.includes(monitor.export) && !completed.includes(monitor.cancel))
    })
    check(language + ': Startup apps shows real and fallback icons, scope controls, and collapsed locations', () => {
      const original = { ...ui.defaultState }
      try {
        Object.assign(ui.defaultState, { language, direction: language === 'ar' ? 'rtl' : 'ltr', page: 'startup',
          activeSidebarItem: 'StartupApps', startupPage: { scanned: true, loading: false, failedSources: 1, snapshotId: 'fixture', items: [
            { id: 'run', name: 'Example app', source: 'Run', scope: 'Machine', location: 'HKLM\\Example', icon: 'data:image/png;base64,fixture', status: 'Enabled' },
            { id: 'folder', name: 'Example shortcut', source: 'StartupFolder', scope: 'User', location: 'C:\\Startup\\Example.lnk', status: 'Disabled', changeToken: 'owned-fixture' },
            { id: 'user', name: 'Example user app', source: 'Run', scope: 'User', location: 'HKCU\\Example', status: 'Enabled', changeToken: 'owned-fixture' },
          ] } })
        const html = render(ui.default, {})
        const sidebar = html.slice(0, html.indexOf('<main'))
        assert.match(sidebar, /class="workspace-nav-button active"[^>]*aria-current="page"/)
        assert.doesNotMatch(html, /HKLM\\Example|startup-details/)
        assert.match(html, /data-icon-source="application"/)
        assert.match(html, /data-icon-source="unavailable"/)
        assert.equal((html.match(/class="startup-disclosure"[^>]*aria-expanded="false"/g) || []).length, 3)
        assert.equal((html.match(/<th scope="col"/g) || []).length, 5)
        assert.ok(html.includes(`aria-label="${lang.startup.enable}: Example shortcut"`))
        assert.ok(!html.includes(`aria-label="${lang.startup.disable}: Example app"`))
        assert.match(html, /class="catalog-tab active" aria-pressed="true"/)
        for (const label of [lang.startup.settings, lang.startup.partial, lang.startup.runKey, lang.startup.folder, lang.startup.machine, lang.startup.user])
          assert.ok(html.includes(label))
        assert.match(html, /type="checkbox"[^>]*disabled=""[^>]*aria-label="[^"]*Example app/)
        assert.ok(html.includes(lang.startup.selectShown))
        assert.ok(html.includes(lang.startup.disableAllShown + ' <bdi>(1)</bdi>'))
        assert.doesNotMatch(html, /type="checkbox"[^>]*checked=""/)
      } finally { Object.assign(ui.defaultState, original) }
    })
    const id = 'a'.repeat(32)
    const extension = browser => ({ browser, id, profile: 'Default', name: 'Owned shared fixture', version: '1.0', installationLocation: 'C:/owned-fixture/'+browser, enabledState: 'Installed' })
    const items = [extension('Google Chrome'), extension('Microsoft Edge'), extension('Brave')]
    check(language + ': sidebar opens extensions and the page selects the first detected browser', () => {
      const original = { ...ui.defaultState }
      try {
        Object.assign(ui.defaultState, { language, direction: language === 'ar' ? 'rtl' : 'ltr', page: 'extensions', activeSidebarItem: 'BrowserExtensions', extensionsPage: { scanned: true, browsers: ['Google Chrome','Microsoft Edge','Brave'], items } })
        const html = render(ui.default, {})
        const sidebar = html.slice(0, html.indexOf('<main'))
        assert.doesNotMatch(html, /All Extensions|كافة ملحقات المستعرض/)
        assert.equal((html.match(/class="app-row ext-row"/g) || []).length, 1)
        assert.match(sidebar, /class="workspace-nav-button active"[^>]*aria-current="page"/)
        assert.doesNotMatch(sidebar, /Google Chrome|Microsoft Edge|Brave/)
        assert.match(html, /role="tab" aria-selected="true"[^]*?Google Chrome/)
      } finally { Object.assign(ui.defaultState, original) }
    })
    for (const browser of ['','Google Chrome','Microsoft Edge','Brave','All']) {
      check(language + ': isolated browser tab ' + browser, () => {
        const html = render(ui.ExtensionsPage, { page: { loading: false, scanned: true, items }, lang, run, selectedBrowser: browser, onSelectBrowser: () => {} })
        assert.equal((html.match(/class="app-row ext-row"/g) || []).length, 1)
        assert.equal((html.match(/role="tab"/g) || []).length, 3)
        assert.equal((html.match(/role="tab" aria-selected="true"/g) || []).length, 1)
        assert.match(html, /class="title-count">\(1\)/)
        assert.doesNotMatch(html, /All Extensions|كافة ملحقات المستعرض|Group by browser|تجميع حسب المتصفح/)
        assert.doesNotMatch(html, /إدارة داخل المتصفح|Manage in browser/)
        assert.equal((html.match(/class="browser-icon/g) || []).length >= 3, true)
      })
    }
    check(language + ': all supported browsers have logos and extension selection is available', () => {
      const browsers = ['Google Chrome', 'Microsoft Edge', 'Brave', 'Opera', 'Vivaldi']
      const html = render(ui.ExtensionsPage, {
        page: { loading: false, scanned: true, items: browsers.map(browser => ({ ...extension(browser), managementCapability: 'Browser' })) }, lang, run,
        selectedBrowser: 'Vivaldi', onSelectBrowser: () => {},
      })
      assert.equal((html.match(/<img class="browser-icon"/g) || []).length, browsers.length + 1)
      assert.doesNotMatch(html, /browser-icon-fallback/)
      assert.equal((html.match(/type="checkbox"/g) || []).length, 2)
      assert.doesNotMatch(html, /type="checkbox"[^>]*disabled=""/)
      assert.ok(html.includes(lang.selectAllExtensions))
    })
    check(language + ': managed extensions cannot be selected for browser actions', () => {
      const html = render(ui.ExtensionsPage, { page: { scanned: true, items: [{ ...extension('Microsoft Edge'), managementCapability: 'Browser', isPolicyManaged: true }] }, lang, run, selectedBrowser: 'Microsoft Edge', onSelectBrowser: () => {} })
      assert.equal((html.match(/type="checkbox"[^>]*disabled=""/g) || []).length, 2)
    })
    check(language + ': incomplete inventory retains valid rows and warning', () => {
      const html = render(ui.ExtensionsPage, { page: { scanned: true, incomplete: true, items }, lang, run, selectedBrowser: 'All', onSelectBrowser: () => {} })
      assert.match(html, /class="extension-scan-warning" role="status"/)
      assert.equal((html.match(/class="app-row ext-row"/g) || []).length, 1)
    })
    check(language + ': detected empty browser keeps its tab and an honest empty state', () => {
      const html = render(ui.ExtensionsPage, { page: { scanned: true, browsers: ['Google Chrome'], items: [] }, lang, run, selectedBrowser: '', onSelectBrowser: () => {} })
      assert.equal((html.match(/role="tab"/g) || []).length, 1)
      assert.match(html, /Google Chrome/)
      assert.doesNotMatch(html, /Microsoft Edge|Brave/)
      assert.ok(html.includes(lang.noExtensionsView))
    })
    check(language + ': repeated extension ID in separate profiles stays as two instances', () => {
      const first = extension('Brave')
      const second = { ...first, profile: 'Profile 1', installationLocation: 'C:/owned-fixture/Brave/Profile 1' }
      const html = render(ui.ExtensionsPage, { page: { scanned: true, browsers: ['Brave'], items: [first, second] }, lang, run, selectedBrowser: 'Brave', onSelectBrowser: () => {} })
      assert.equal((html.match(/class="app-row ext-row"/g) || []).length, 2)
      assert.match(html, /Profile 1/)
      assert.match(html, /Default/)
    })
    check(language + ': no detected browsers creates no fake tabs', () => {
      const html = render(ui.ExtensionsPage, { page: { scanned: true, browsers: [], items: [] }, lang, run, selectedBrowser: 'Google Chrome', onSelectBrowser: () => {} })
      assert.doesNotMatch(html, /role="tab"|role="tablist"/)
      assert.ok(html.includes(lang.extensionNoBrowsersFound))
    })
    for (const loading of [false, true]) {
      check(language + ': unscanned browser counts never display zero; loading=' + loading, () => {
        const html = render(ui.ExtensionsPage, { page: { scanned: false, loading, browsers: ['Microsoft Edge'], items: [] }, lang, run, selectedBrowser: '', onSelectBrowser: () => {} })
        assert.doesNotMatch(html, /class="sub-tab-badge">0<|class="title-count">\(0\)/)
        assert.ok(html.includes(loading ? lang.scanning : lang.notScanned))
      })
    }
    check(language + ': empty failed scan shows an error, not a successful empty result', () => {
      const html = render(ui.ExtensionsPage, { page: { scanned: false, error: 'Fixture scan unavailable', items: [] }, lang, run, selectedBrowser: 'All', onSelectBrowser: () => {} })
      assert.match(html, /class="empty-state extension-error"/)
      assert.match(html, /Fixture scan unavailable/)
    })
    check(language + ': mixed queue completes its progress without inventing successful removals', () => {
      const results = ['Succeeded','Failed','Skipped','Skipped'].map((state,index)=>({state,app:{id:'owned-'+index,name:'Owned fixture '+index,icon:null}}))
      const html = render(ui.ProgressDialog, { lang, run, op: { isRunning: false, state: 'PartialFailure', total: 4, processed: 2, successful: 1, failed: 1, skipped: 2, results, queueItems: [], logs: [] } })
      assert.match(html, /simple-result-summary has-failures/)
      assert.ok(html.includes(language === 'ar' ? 'تمت إزالة 1 من 4' : 'Removed 1 of 4'))
      assert.equal((html.match(/simple-result-row result-cancelled/g) || []).length, 2)
      assert.ok(html.includes(lang.close))
      assert.ok(!html.includes(lang.stopQueue))
    })
    check(language + ': completed success follows final engine state even if a stop request was recorded', () => {
      const html = render(ui.ProgressDialog, {lang,run,op:{isRunning:false,state:'Succeeded',stopRequested:true,total:1,processed:1,successful:1,failed:0,skipped:0,results:[{app:{id:'owned',name:'Owned fixture'},state:'Succeeded'}]}})
      assert.match(html,/simple-result-summary\s*" role="status"/)
      assert.doesNotMatch(html,/stop-queue|has-failures/)
    })
    check(language + ': finished no-removal queue has a visible reason and closes normally', () => {
      const reason = 'Removal was not observed. The program is still registered.'
      const html = render(ui.ProgressDialog, {lang,run,op:{isRunning:false,needsReview:false,state:'PartialFailure',failureCode:'RemovalNotObserved',total:3,processed:1,successful:0,failed:1,skipped:2,results:[{app:{id:'owned',name:'Owned fixture'},state:'Failed',message:reason},{app:{id:'skip1',name:'Skipped one'},state:'Skipped'},{app:{id:'skip2',name:'Skipped two'},state:'Skipped'}]}})
      assert.match(html,/simple-result-reason/)
      assert.ok(html.includes(language === 'ar' ? 'تمت إزالة 0 من 3' : 'Removed 0 of 3'))
      assert.ok(html.includes(lang.close))
      assert.doesNotMatch(html,/data-testid="queue-review-stop"|data-testid="queue-acknowledge"/)
    })
    check(language + ': paused/recovered queue keeps evidence collapsed and consent required', () => {
      const html = render(ui.ProgressDialog, { lang, run, op: { isRunning: false, needsReview: true, total: 4, state: 'NeedsAttention', app: {id:'owned',name:'Owned fixture'}, queueItems: [], results: [], logs: [], status:'Owned review warning', errorDetails:'Owned evidence', exitCode:0, review:{recovered:true,canAcknowledge:true,items:[]} } })
      assert.match(html, /<details class="queue-diagnostics">/)
      assert.match(html, /<details class="queue-recovery-details">/)
      assert.doesNotMatch(html, /<details[^>]*open/)
      assert.match(html, /data-testid="queue-acknowledge" disabled=""/)
      assert.match(html, /data-testid="queue-review-stop"/)
      assert.match(html, /0\/4 · 0%/)
      assert.doesNotMatch(html, /icon-state-badge success/)
    })
    check(language + ': actual app icon is preserved and unavailable app gets a generic fallback', () => {
      const missing = render(ui.AppIcon, {app:{name:'No logo fixture',icon:null},variant:'progress',fallbackLabel:lang.iconUnavailable})
      assert.match(missing, /data-icon-source="unavailable"/)
      assert.ok(missing.includes(lang.iconUnavailable))
      const real = render(ui.AppIcon, {app:{name:'Actual logo fixture',icon:'data:image/png;base64,owned'},variant:'progress',fallbackLabel:lang.iconUnavailable})
      assert.match(real, /data-icon-source="application"/)
      assert.match(real, /loading="eager"/)
      assert.doesNotMatch(real, /app-icon-fallback/)
    })
    check(language + ': exit 1605 diagnostic hides None and retains the actual localized exit field', () => {
      const html = render(ui.ProgressDialog,{lang,run,op:{isRunning:false,needsReview:true,state:'NeedsUserAttention',total:2,processed:2,successful:1,failed:1,skipped:0,results:[],queueItems:[],errorDetails:'Owned diagnostic',failureCode:'None',exitCode:1605}})
      assert.doesNotMatch(html,/>None</)
      assert.match(html,/<bdi dir="ltr">1605<\/bdi>/)
      assert.ok(html.includes(lang.exitCodeLabel))
    })
    check(language + ': absent current registration stays separate from unresolved attempt history', () => {
      const html=render(ui.ProgressDialog,{lang,run,op:{isRunning:true,needsReview:true,state:'NeedsUserAttention',total:2,processed:1,successful:0,failed:1,app:{id:'owned',name:'Owned review fixture'},queueItems:[],results:[],review:{registrationState:'Absent'}}})
      assert.ok(html.includes(lang.registrationAbsent))
      assert.ok(html.includes(lang.queueHistoryHint))
      assert.ok(html.includes(lang.stopAndReview.replaceAll('&', '&amp;')))
      assert.match(html,/0\/2 · 0%/)
      assert.doesNotMatch(html,/icon-state-badge success/)
    })
    check(language + ': prelaunch MSI validation failure shows diagnosis without sentinel exit', () => {
      const html = render(ui.ProgressDialog, {lang,run,op:{isRunning:false,state:'PartialFailure',operationState:'Failed',app:{id:'owned',name:'Owned invalid command'},failureCode:'InvalidMsiCommand',exitCode:null,errorDetails:'The uninstall command has no valid MSI product code.',total:1,processed:1,failed:1,successful:0,skipped:0,queueItems:[{app:{id:'owned',name:'Owned invalid command'},state:'Failed',message:'Invalid MSI product code.\nInvalidMsiCommand\nThe uninstall command has no valid MSI product code.',failureCode:'InvalidMsiCommand',exitCode:null}]}})
      assert.match(html,/simple-result-reason[^]*?MSI/)
      assert.ok(!html.includes(lang.exitCodeLabel))
      assert.doesNotMatch(html,/>-1</)
    })
    check(language + ': history empty state renders', () => {
      assert.match(render(ui.HistoryPage, { page: { records: [] }, lang, run, onClear: () => {} }), /empty-state/)
    })
    check(language + ': history shows the current range and disables the unavailable direction', () => {
      const page = { records: [{ id: 100, name: 'Owned fixture', date: '2026-01-01 01:02:03' }],
        pageNumber: 1, range: '1–100 / 350', hasOlderPage: true, hasNewerPage: false }
      const html = render(ui.HistoryPage, { page, lang, run, onClear: () => {} })
      assert.ok(html.includes(`aria-label="${lang.historyPagination}"`))
      assert.match(html, /<bdi dir="ltr">1–100 \/ 350<\/bdi>/)
      assert.match(html, new RegExp(`<button[^>]*disabled=""[^>]*>${lang.historyNewerPage}</button>`))
      assert.match(html, new RegExp(`<button class="button quiet">${lang.historyOlderPage}</button>`))
    })
  }
  console.log(`RESULT ${passed} passed / 0 failed (SSR only; no screenshot/native resize claim)`)
} finally { await server.close() }
