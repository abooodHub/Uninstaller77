// Component/accessibility contracts only; SSR cannot prove native window layout.
import { createServer } from 'vite'
import React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import assert from 'node:assert/strict'
const server = await createServer({ server: { middlewareMode: true }, plugins: [{
  name: 'audit-components', enforce: 'pre', transform(code, id) {
    if (id.replaceAll('\\', '/').endsWith('/src/App.tsx'))
      return code + '\nexport { words, defaultState, SettingsDialog, ControlPage, ExtensionsPage, HistoryPage, ResidualDialog };'
  }
}] })
let passed = 0
const run = async () => true
const render = (Component, props) => renderToStaticMarkup(React.createElement(Component, props))
const check = (name, test) => { test(); passed++; console.log('PASS ' + name) }
try {
  const ui = await server.ssrLoadModule('/src/App.tsx')
  check('Arabic and English expose the same translation keys', () => {
    assert.deepEqual(Object.keys(ui.words.ar).sort(), Object.keys(ui.words.en).sort())
    assert.deepEqual(Object.keys(ui.words.ar.control).sort(), Object.keys(ui.words.en.control).sort())
    assert.deepEqual(Object.keys(ui.words.ar.unregistered).sort(), Object.keys(ui.words.en.unregistered).sort())
  })
  for (const language of ['ar', 'en']) {
    const lang = ui.words[language]
    check(language + ': a peer already removed by the previous item is skipped, without a false failed row or successful removal', () => {
      const html=render(ui.ProgressDialog,{lang,run,op:{isRunning:false,state:'CompletedWithSkips',operationState:'Completed',app:{id:'peer',name:'Owned already-absent peer'},total:2,processed:1,failed:0,successful:1,skipped:1,results:[{},{}],queueItems:[{app:{id:'parent',name:'Owned parent'},state:'Completed'},{app:{id:'peer',name:'Owned already-absent peer'},state:'NotInstalled',message:'The installation registration is already absent.'}]}})
      assert.ok(html.includes(lang.stages.NotInstalled))
      assert.match(html,/result-notinstalled/)
      assert.doesNotMatch(html,/result-failed/)
      assert.match(html,/phase10-progress status-orange/)
      assert.match(html,/aria-valuenow="100"/)
      assert.match(html,/1\/2 · 50%/)
      assert.doesNotMatch(html,/stop-queue/)
    })
    check(language + ': a previous failed application retains its visible cause after the selected next app succeeds', () => {
      const html=render(ui.ProgressDialog,{lang,run,op:{isRunning:false,state:'PartialFailure',operationState:'Failed',app:{id:'next',name:'Owned next'},failureCode:'None',total:2,processed:2,failed:1,successful:1,skipped:0,results:[{},{}],queueItems:[{app:{id:'riot',name:'Owned refusal'},state:'Failed',message:'Riot Client cannot be removed while Riot games remain installed.\nVendorDependenciesInstalled\nExit code: 0'},{app:{id:'next',name:'Owned next'},state:'Completed'}]}})
      assert.match(html,/queue-result-reason/)
      assert.ok(html.includes(language==='ar' ? 'لا يمكن إزالة Riot Client ما دامت ألعاب Riot مثبتة.' : 'Riot Client cannot be removed while Riot games remain installed.'))
      assert.match(html,/aria-valuenow="100"/)
      assert.match(html,/1\/2 · 50%/)
      assert.match(html,/queue-current-stage[^]*?state-completed|state-completed[^]*?queue-current-stage/)
    })
    check(language + ': completed queue header follows its current failed row, not the skipped remainder', () => {
      const html = render(ui.ProgressDialog, {lang,run,op:{isRunning:false,state:'PartialFailure',operationState:'Cancelled',app:{id:'owned',name:'Owned failure'},total:3,processed:1,successful:0,failed:1,skipped:2,queueItems:[{app:{id:'owned',name:'Owned failure'},state:'Failed'},{app:{id:'skip',name:'Owned skipped'},state:'Cancelled'}]}})
      assert.match(html,new RegExp('queue-current-stage[^]*?' + lang.stages.Failed))
      assert.match(html,/class="app-icon[^"]*variant-progress[^"]*state-failed/)
    })
    check(language + ': vendor dependency refusal is visible before opening diagnostics', () => {
      const html=render(ui.ProgressDialog,{lang,run,op:{isRunning:false,state:'PartialFailure',failureCode:'VendorDependenciesInstalled',total:2,processed:1,failed:1,skipped:1,successful:0,queueItems:[],results:[{state:'Failed',app:{id:'owned',name:'Owned refusal'}},{state:'Skipped',app:{id:'skip',name:'Owned not started'}}]}})
      assert.ok(html.includes(lang.vendorDependenciesInstalled))
      assert.match(html,/aria-valuenow="100"/)
      assert.match(html,/0\/2 · 0%/)
      assert.doesNotMatch(html,/stop-queue/)
    })
    check(language + ': running residual maintenance freezes selection and duplicate cleanup actions', () => {
      const html=render(ui.ResidualDialog,{lang,labels:{},run,residual:{cleaning:true,items:[{index:0,path:'C:\\Owned\\Fixture',canClean:true,selected:true}]}})
      assert.match(html,/type="checkbox"[^>]*disabled=""/)
      assert.match(html,/class="button primary" disabled=""/)
      assert.match(html,/class="button quiet" disabled=""/)
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
    check(language + ': simplified settings preserve language choice', () => {
      const html = render(ui.SettingsDialog, { state: { language }, lang, run })
      assert.doesNotMatch(html, /type="checkbox"|Reduce motion|تقليل الحركة|ضبط تفضيلات العرض/)
      assert.match(html, /English/); assert.match(html, /العربية/)
    })
    check(language + ': support section is removed and working management tools remain', () => {
      const html = render(ui.ControlPage, { lang, run, page: { runtime: { appVersion: '1.1.10' }, sources: [{ source: 'HKCU 64-bit', accepted: 2, errors: 0, sourceErrors: 0, elapsedMs: 1, status: 'Completed' }] }, inventory: {}, scope: { title: '', search: '', count: 0, ready: false } })
      assert.equal((html.match(/class="control-tool"/g) || []).length, 6)
      assert.doesNotMatch(html, /control-support|Support information|معلومات الدعم|Discovery diagnostics|تشخيص اكتشاف البرامج|Runtime information|بيئة التشغيل|HKCU 64-bit/)
      assert.doesNotMatch(html, /Force Uninstall|File Shredder|System Restore|Unavailable advanced tools|إزالة البرامج المستعصية|تمزيق الملفات|استعادة النظام|أدوات متقدمة غير متاحة|control-advanced/)
    })
    const id = 'a'.repeat(32)
    const extension = browser => ({ browser, id, profile: 'Default', name: 'Owned shared fixture', version: '1.0', installationLocation: 'C:/owned-fixture/'+browser, enabledState: 'Installed' })
    const items = [extension('Google Chrome'), extension('Microsoft Edge'), extension('Brave')]
    check(language + ': sidebar and page select the same first real browser without an aggregate entry', () => {
      const original = { ...ui.defaultState }
      try {
        Object.assign(ui.defaultState, { language, direction: language === 'ar' ? 'rtl' : 'ltr', page: 'extensions', activeSidebarItem: 'BrowserExtensions', extensionsPage: { scanned: true, browsers: ['Google Chrome','Microsoft Edge','Brave'], items } })
        const html = render(ui.default, {})
        const sidebar = html.slice(0, html.indexOf('<main'))
        assert.doesNotMatch(html, /All Extensions|كافة ملحقات المستعرض/)
        assert.equal((html.match(/class="app-row ext-row"/g) || []).length, 1)
        assert.match(sidebar, /class="nav-sub-item active"[^>]*><span class="extension-nav-label">[^]*?Google Chrome/)
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
    for (const [state, isRunning, tone] of [['Running',true,'blue'],['Succeeded',false,'green'],['PartialFailure',false,'orange'],['CriticalFailure',false,'red']]) {
      check(language + ': actual queue state tone ' + state, () => {
        const html = render(ui.ProgressDialog, { lang, run, op: { isRunning, state, total: 3, processed: 2, successful: 1, failed: 1, skipped: 0, logs: [], results: [], queueItems: [] } })
        assert.match(html, new RegExp('phase10-progress status-'+tone))
      })
    }
    check(language + ': mixed queue completes its progress without inventing successful removals', () => {
      const results = ['Succeeded','Failed','Skipped','Skipped'].map((state,index)=>({state,app:{id:'owned-'+index,name:'Owned fixture '+index,icon:null}}))
      const html = render(ui.ProgressDialog, { lang, run, op: { isRunning: false, state: 'PartialFailure', total: 4, processed: 2, successful: 1, failed: 1, skipped: 2, results, queueItems: [], logs: [] } })
      assert.equal((html.match(/role="progressbar"/g) || []).length, 1)
      assert.match(html, /aria-valuenow="100"/)
      assert.match(html, /<bdi dir="ltr">4\/4<\/bdi>/)
      assert.match(html, /<bdi dir="ltr">1\/4 · 25%<\/bdi>/)
      assert.ok(html.includes(lang.close))
      assert.ok(!html.includes(lang.stopQueue))
      assert.ok(html.includes(lang.queuePartial))
    })
    check(language + ': completed success follows final engine state even if a stop request was recorded', () => {
      const html = render(ui.ProgressDialog, {lang,run,op:{isRunning:false,state:'Succeeded',stopRequested:true,total:1,processed:1,successful:1,failed:0,skipped:0,results:[{app:{id:'owned',name:'Owned fixture'},state:'Succeeded'}]}})
      assert.match(html,/phase10-progress status-green/)
      assert.doesNotMatch(html,/stop-queue/)
      assert.match(html,/aria-valuenow="100"/)
    })
    check(language + ': finished no-removal queue has a visible reason and closes normally', () => {
      const html = render(ui.ProgressDialog, {lang,run,op:{isRunning:false,needsReview:false,state:'PartialFailure',failureCode:'RemovalNotObserved',total:3,processed:1,successful:0,failed:1,skipped:2,results:[{app:{id:'owned',name:'Owned fixture'},state:'Failed'},{app:{id:'skip1',name:'Skipped one'},state:'Skipped'},{app:{id:'skip2',name:'Skipped two'},state:'Skipped'}]}})
      assert.ok(html.includes(lang.removalNotObserved))
      assert.match(html,/aria-valuenow="100"/)
      assert.match(html,/0\/3 · 0%/)
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
      const html = render(ui.ProgressDialog,{lang,run,op:{isRunning:false,state:'PartialFailure',total:2,processed:2,successful:1,failed:1,skipped:0,results:[],queueItems:[],errorDetails:'Owned diagnostic',failureCode:'None',exitCode:1605}})
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
      assert.match(html,/InvalidMsiCommand/)
      assert.ok(!html.includes(lang.exitCodeLabel))
      assert.doesNotMatch(html,/>-1</)
    })
    check(language + ': history empty state renders', () => {
      assert.match(render(ui.HistoryPage, { page: { records: [] }, lang, run, onClear: () => {} }), /empty-state/)
    })
  }
  console.log(`RESULT ${passed} passed / 0 failed (SSR only; no screenshot/native resize claim)`)
} finally { await server.close() }
