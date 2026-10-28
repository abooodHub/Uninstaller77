import test from 'node:test'
import assert from 'node:assert/strict'
import { localizeText, localizeState, queueProgress, inventoryCount, historyTimestamp, browserGroups, resolveExtensionBrowser, extensionIdentity, extensionPermissionImpact, virtualRange, markFirstAppearance, verifiedQueuePercent, reconcileSearch, residualScanStatusText, historyOutcome, queueFailureReason, queueFailureInfo } from '../src/presentation.ts'

test('Desktop shortcut cleanup messages keep actual counts and translate in both directions', () => {
  for (const text of ['Removed orphaned desktop shortcuts: 2.', 'Some desktop shortcuts were kept for review.']) {
    const arabic = localizeText(text, 'ar')
    assert.notEqual(arabic, text)
    assert.equal(localizeText(arabic, 'en'), text)
  }
  assert.ok(localizeText('Removed orphaned desktop shortcuts: 2.', 'ar').includes('2.'))
})

test('Verified external browser handoff translates without claiming the browser was closed', () => {
  const message = 'External browser handoff was verified; the browser can remain open.'
  const arabic = localizeText(message, 'ar')
  assert.equal(arabic, 'تم التحقق من انتهاء الإزالة؛ يمكن إبقاء المتصفح الخارجي مفتوحًا.')
  assert.equal(localizeText(arabic, 'en'), message)
})

test('Each failed attempt keeps its own localized reason after a later success', () => {
  const item = { state: 'Failed', message: 'Riot Client cannot be removed while Riot games remain installed.\nVendorDependenciesInstalled\nExit code: 0' }
  assert.equal(queueFailureReason(item, 'en'), 'Riot Client cannot be removed while Riot games remain installed.')
  assert.equal(queueFailureReason(item, 'ar'), 'لا يمكن إزالة Riot Client ما دامت ألعاب Riot مثبتة.')
  assert.equal(queueFailureReason({ ...item, state: 'Completed' }, 'en'), '')
  assert.equal(queueFailureReason({ ...item, state: 'Cancelled' }, 'en'), '')
  assert.equal(queueFailureReason({ state: 'Failed' }, 'ar'), '')
  assert.equal(queueFailureReason({ state: 'Failed', message: 'x'.repeat(2000) }, 'en').length, 600)
})

test('Already absent history remains a neutral skipped observation with no process exit or removal credit', () => {
  for (const language of ['ar', 'en']) {
    const outcome = historyOutcome({ success: false, outcome: 'NotInstalled' }, language)
    assert.equal(outcome.tone, 'cell-dim')
    assert.equal(outcome.showExit, false)
    assert.equal(outcome.label, language === 'ar' ? 'غير مثبت حاليًا — تم التخطي' : 'Already absent — skipped')
    assert.notEqual(historyOutcome({ success: true, outcome: 'NotInstalled' }, language).tone, 'text-good')
  }
})

test('Vendor refusal and pre-launch stale-registration diagnoses translate without changing codes or names', () => {
  for (const message of ['Riot Client cannot be removed while Riot games remain installed.',
    'The Riot uninstaller refused removal because Riot games are still installed. Remove the games you choose separately, then retry Riot Client. No other application was removed automatically.',
    'The installation registration is already absent.', 'The registered uninstall command changed after confirmation.',
    'The current uninstall registration could not be verified.']) {
    const translated = localizeText(message, 'ar')
    assert.notEqual(translated, message)
    assert.equal(localizeText(translated, 'en'), message)
  }
  assert.equal(localizeText('VendorDependenciesInstalled', 'ar'), 'VendorDependenciesInstalled')
})

test('Completed no-removal results explain retained registration in Arabic and English', () => {
  for (const message of ['The uninstaller finished, but removal was not observed.',
    'The installation registration is still present. No successful removal was verified. Remaining queue items were not started automatically.',
    'Not started after a completed uninstaller left its registration present.']) {
    const arabic = localizeText(message, 'ar')
    assert.notEqual(arabic, message)
    assert.equal(localizeText(arabic, 'en'), message)
  }
})

test('A finished no-op batch completes its accounting without inventing successful removal', () => {
  assert.deepEqual(queueProgress({isRunning:false,processed:1,total:3,results:[{state:'Failed'},{state:'Skipped'},{state:'Skipped'}]}),
    {attempts:'1/3',resolved:'3/3',percent:100})
  assert.equal(verifiedQueuePercent({successful:0,total:3}),0)
})

test('Unregistered file operations have separate history outcomes and no fabricated exit status', () => {
  for (const language of ['en', 'ar']) for (const outcome of ['FilesQuarantined', 'FilesRestored', 'FilesDeleted', 'FileDeletionIncomplete']) {
    const result = historyOutcome({ success: false, outcome }, language)
    assert.equal(result.showExit, false)
    assert.notEqual(result.label, language === 'ar' ? 'نجاح' : 'Success')
    assert.notEqual(result.label, language === 'ar' ? 'غير محسوم' : 'Unresolved')
  }
})

test('Unregistered refusal messages are readable in both interface languages', () => {
  for (const code of ['ProtectedScope', 'RegisteredScope', 'MixedData', 'FilesInUse', 'ChangedScope', 'InvalidReview', 'RestoreOccupied', 'InvalidBackup'])
    for (const language of ['ar', 'en']) assert.ok(!localizeText('UnregisteredApp:' + code, language).startsWith('UnregisteredApp:'))
})

test('Phase14 extension permission metadata never becomes a verified malware finding', () => {
  const impact = extensionPermissionImpact({ permissions: ['tabs', 'storage', 'history', 'history'], hostPermissions: ['<all_urls>'], contentScriptHosts: ['https://example.test/*'] })
  assert.equal(impact.broadSiteAccess, true)
  assert.deepEqual(impact.sensitivePermissions, ['tabs', 'history'])
  assert.equal(impact.verifiedSecurityFinding, false)
  assert.equal(impact.riskAssessment, 'NotAssessed')
})

test('Phase11 unsupported and incomplete residual scans never become clean in either language', () => {
  for (const language of ['en', 'ar']) {
    const unsupported = residualScanStatusText('ResidualScan:Unsupported', language)
    for (const state of ['NotScanned', 'Scanning', 'Unsupported', 'Failed', 'Cancelled']) {
      const text = residualScanStatusText('ResidualScan:' + state, language)
      assert.notEqual(text, residualScanStatusText('ResidualScan:CompletedNoFindings', language))
      assert.ok(!/Clean/i.test(text))
    }
    assert.equal(residualScanStatusText('Clean (No leftovers found)', language, 'MSIX / Store'), unsupported)
  }
  assert.equal(residualScanStatusText('ResidualScan:Unsupported', 'ar'), 'لم يتم فحص المخلفات — هذا النوع غير مدعوم حاليًا')
  assert.equal(residualScanStatusText('ResidualScan:Unsupported', 'en'), 'Residual scan not supported for this package type.')
})

test('Phase11 completed empty scan reports only checked locations', () => {
  assert.equal(residualScanStatusText('ResidualScan:CompletedNoFindings', 'en'), 'No findings in the checked locations.')
  assert.match(residualScanStatusText('ResidualScan:CompletedNoFindings', 'ar'), /المواقع المفحوصة/)
  assert.notEqual(residualScanStatusText('ResidualScan:CompletedWithFindings', 'en'), residualScanStatusText('ResidualScan:CompletedNoFindings', 'en'))
})

test('Phase11 legacy historical text is retained outside known unsupported MSIX claims', () => {
  assert.equal(residualScanStatusText('Legacy scan evidence', 'en', 'NSIS'), 'Legacy scan evidence')
})

test('Phase11 stopped review localization preserves identities and original outcomes', () => {
  const original = { language: 'ar', appsPage: { items: [], operation: { review: { queueId: 'queue_42', items: [{ operationId: 'op_99', evidence: 'User kept the unresolved operation pending for review.', lastState: 'Running', appId: 'Program_2' }] } } }, historyPage: { records: [] } }
  const translated = localizeState(original)
  assert.equal(translated.appsPage.operation.review.items[0].lastState, 'Running')
  assert.equal(translated.appsPage.operation.review.items[0].appId, 'Program_2')
  assert.equal(translated.appsPage.operation.review.items[0].operationId, 'op_99')
  assert.equal(translated.appsPage.operation.review.items[0].evidence, 'أبقى المستخدم العملية غير المحسومة للمراجعة.')
  assert.equal(original.appsPage.operation.review.items[0].evidence, 'User kept the unresolved operation pending for review.')
})

test('changing to Arabic translates an already-paused queue without changing its result', () => {
  const paused = 'Completion is uncertain. The queue is paused for review; the next application will not start.'
  assert.equal(localizeText(paused, 'ar'), 'تعذر تأكيد انتهاء أداة الإزالة. الطابور متوقف للمراجعة؛ لن يبدأ البرنامج التالي.')
  assert.equal(localizeText(localizeText(paused, 'ar'), 'en'), paused)
})

test('completed mixed queue separates attempted removals from resolved items', () => {
  const summary = queueProgress({ isRunning: false, processed: 2, total: 4, results: ['succeeded', 'failed', 'skipped', 'skipped'] })
  assert.equal(summary.attempts, '2/4')
  assert.equal(summary.resolved, '4/4')
  assert.equal(summary.percent, 100)
})
test('running and empty queues never invent completion', () => {
  assert.equal(queueProgress({ isRunning: true, processed: 2, total: 4 }).percent, 50)
  assert.equal(queueProgress({ isRunning: true, processed: 2, total: 4 }).resolved, '2/4')
  assert.equal(queueProgress({ isRunning: true, total: 0 }).percent, 0)
  assert.equal(queueProgress({ isRunning: false, processed: 1, total: 4, results: ['failed'] }).percent, 25)
})
test('Arabic failures retain the actual exit code and diagnostic path', () => {
  assert.equal(localizeText('Windows Installer does not recognize the registered product.', 'ar'), 'لا يتعرف Windows Installer على المنتج المسجّل للإزالة.')
  assert.equal(localizeText('لا يتعرف Windows Installer على المنتج المسجّل للإزالة.', 'en'), 'Windows Installer does not recognize the registered product.')
  assert.equal(localizeText('Removal could not be verified (exit code 3010).', 'ar'), 'تعذر التحقق من الإزالة (رمز الخروج 3010).')
  assert.equal(localizeText('Uninstaller executable was not found.\nDetails: C:\\Apps\\7-Zip\\Uninstall.exe', 'ar'), 'ملف أداة الإزالة المسجل غير موجود.\nالتفاصيل: C:\\Apps\\7-Zip\\Uninstall.exe')
})
test('translation is presentation only and preserves names, identifiers and engine results', () => {
  const source = { language: 'ar', appsPage: { items: [{ id: 'app-1', name: '7-Zip 26.00 (x64)', size: 'Unknown', installDate: 'Unknown', architecture: '64-bit', uninstallCommand: 'C:\\Apps\\7-Zip\\Uninstall.exe' }], operation: { processed: 2, successful: 1, failed: 1, skipped: 2, status: 'Application still appears registered in the system.', results: [] } }, historyPage: { records: [{ notes: 'Application registration was removed.', residualStatus: 'Not scanned' }] } }
  const snapshot = JSON.stringify(source)
  const shown = localizeState(source)
  assert.equal(JSON.stringify(source), snapshot)
  assert.equal(shown.appsPage.items[0].name, source.appsPage.items[0].name)
  assert.equal(shown.appsPage.items[0].uninstallCommand, source.appsPage.items[0].uninstallCommand)
  assert.equal(shown.appsPage.items[0].size, 'غير معروف')
  assert.equal(shown.appsPage.operation.successful, 1)
  assert.equal(shown.appsPage.operation.failed, 1)
  assert.equal(shown.historyPage.records[0].residualStatus, 'لم يُفحص')
})
test('English messages and technical units remain unchanged', () => {
  assert.equal(localizeText('Application still appears registered in the system.', 'en'), 'Application still appears registered in the system.')
  assert.equal(localizeText('1.0 GB', 'ar'), '1.0 GB')
})

test('failed legacy records never claim a residual scan is still running', () => {
  for (const language of ['ar', 'en']) {
    const source = { language, appsPage: {}, historyPage: { records: [{ success: false, residualStatus: 'Scanning...' }] } }
    const shown = localizeState(source)
    assert.equal(shown.historyPage.records[0].residualStatus, language === 'ar' ? 'لم يُفحص' : 'Not scanned')
    assert.equal(source.historyPage.records[0].residualStatus, 'Scanning...')
  }
})

test('quarantine history follows the current language using persisted counts', () => {
  const record = { success: true, residualStatus: 'نُقلت 1 عناصر إلى الحجر (0.3 م.ب)', cleanedItemsCount: 1, cleanedSizeBytes: 287307 }
  const english = localizeState({ language: 'en', appsPage: {}, historyPage: { records: [record] } })
  const arabic = localizeState({ language: 'ar', appsPage: {}, historyPage: { records: [record] } })
  assert.equal(english.historyPage.records[0].residualStatus, '1 item moved to quarantine (0.3 MB)')
  assert.equal(arabic.historyPage.records[0].residualStatus, 'نُقل عنصر واحد إلى الحجر (0.3 م.ب)')
  assert.equal(record.residualStatus, 'نُقلت 1 عناصر إلى الحجر (0.3 م.ب)')
})


test('unscanned and loading inventories never masquerade as an empty result', () => {
  assert.equal(inventoryCount({ items: [] }, 'Not Scanned', 'Scanning'), 'Not Scanned')
  assert.equal(inventoryCount({ loading: true, items: [] }, 'لم يُفحص', 'جارٍ الفحص'), 'جارٍ الفحص')
  assert.equal(inventoryCount({ scanned: true, items: [] }, 'Not Scanned', 'Scanning'), '0')
  assert.equal(inventoryCount({ scanned: true, items: [1, 2] }, 'Not Scanned', 'Scanning'), '2')
})

test('history timestamps keep the full date and time without timezone conversion', () => {
  assert.deepEqual(historyTimestamp('2026-10-24 13:27:39'), { date: '2026-10-24', time: '13:27:39' })
  assert.deepEqual(historyTimestamp('10/24/2026 1:27 PM'), { date: '10/24/2026', time: '1:27 PM' })
  assert.deepEqual(historyTimestamp('2026-10-24'), { date: '2026-10-24', time: '' })
})

test('browser sections are counted from exact inventory names and retain cross-browser duplicates', () => {
  const id = 'a'.repeat(32)
  const items = [
    { browser: 'Brave', profile: 'Default', id, installationLocation: 'brave/default', name: '7TV Nightly' },
    { browser: 'Microsoft Edge', profile: 'Default', id, installationLocation: 'edge/default', name: '7TV Nightly' },
    { browser: 'Google Chrome', profile: 'Profile 1', id: 'b'.repeat(32), installationLocation: 'chrome/profile1', name: 'Other' },
    { browser: 'Brave', profile: 'Profile 1', id, installationLocation: 'brave/profile1', name: '7TV Nightly' }
  ]
  const groups = browserGroups(items)
  assert.deepEqual(groups.map(x => [x.browser, x.items.length]), [['Google Chrome', 1], ['Microsoft Edge', 1], ['Brave', 2]])
  assert.equal(groups.reduce((total, group) => total + group.items.length, 0), items.length)
  assert.equal(new Set(items.map(extensionIdentity)).size, 4)
  assert.equal(items.filter(x => x.browser === 'Microsoft Edge').length, 1)
})

test('additional discovered browsers generate sections without fixed tab counts', () => {
  const items = [{ browser: 'Vivaldi', profile: 'Default', id: 'c'.repeat(32) }, { browser: 'Opera', profile: 'Default', id: 'd'.repeat(32) }]
  assert.deepEqual(browserGroups(items).map(x => x.browser), ['Opera', 'Vivaldi'])
  assert.deepEqual(browserGroups([]), [])
  assert.deepEqual(browserGroups(items, ['Google Chrome']).map(x => [x.browser, x.items.length]), [['Google Chrome', 0], ['Opera', 1], ['Vivaldi', 1]])
})

test('browser-only selection keeps an available browser and resolves stale or removed All selection to one browser', () => {
  const groups = browserGroups([], ['Brave', 'Google Chrome', 'Microsoft Edge'])
  assert.equal(resolveExtensionBrowser(groups, 'Microsoft Edge'), 'Microsoft Edge')
  assert.equal(resolveExtensionBrowser(groups, ''), 'Google Chrome')
  assert.equal(resolveExtensionBrowser(groups, 'All'), 'Google Chrome')
  assert.equal(resolveExtensionBrowser(groups, 'Removed Browser'), 'Google Chrome')
})

test('browser-only selection never invents an installed browser and keeps a detected empty browser selectable', () => {
  assert.equal(resolveExtensionBrowser([], 'Google Chrome'), '')
  const groups = browserGroups([], ['Vivaldi'])
  assert.equal(resolveExtensionBrowser(groups, 'Google Chrome'), 'Vivaldi')
  assert.equal(groups[0].items.length, 0)
})

test('virtual list keeps the rendered window bounded for 150, 500, and 1000 rows', () => {
  const rowHeight = 54
  const viewportHeight = 640
  for (const count of [150, 500, 1000]) {
    for (const top of [0, Math.floor(count * rowHeight / 2), count * rowHeight]) {
      const range = virtualRange(count, top, viewportHeight, rowHeight)
      assert.ok(range.start >= 0 && range.start <= range.end)
      assert.ok(range.end <= count)
      assert.ok(range.end - range.start <= Math.ceil(viewportHeight / rowHeight) + 8)
      assert.equal(range.totalHeight, count * rowHeight)
    }
  }
})

test('virtual list clamps invalid offsets and handles an empty inventory', () => {
  assert.deepEqual(virtualRange(0, -10, 640, 54), { start: 0, end: 0, totalHeight: 0 })
  assert.deepEqual(virtualRange(10, -10, 100, 0), { start: 0, end: 10, totalHeight: 10 })
})

test('virtual rows animate once per stable identity, including when recycled by scrolling', () => {
  const appeared = new Set<string>()
  assert.equal(markFirstAppearance('app-17', appeared), true)
  assert.equal(markFirstAppearance('app-18', appeared), true)
  assert.equal(markFirstAppearance('app-17', appeared), false)
  assert.equal(markFirstAppearance('app-17', appeared), false)
})

test('Phase 10 queue percentage counts verified removals and never converts skips or failures into success', () => {
  assert.equal(verifiedQueuePercent({ total: 4, successful: 1 }), 25)
  assert.equal(verifiedQueuePercent({ total: 4, successful: 0 }), 0)
  assert.equal(verifiedQueuePercent({ total: 0, successful: 0 }), 0)
  assert.equal(verifiedQueuePercent({ total: 4, successful: 4 }), 100)
})

test('late search acknowledgements do not replace a newer local edit', () => {
  assert.deepEqual(reconcileSearch('Microsoft', 'Microsoft', 'Micro'), { draft: 'Microsoft', pending: 'Microsoft' })
  assert.deepEqual(reconcileSearch('Microsoft', 'Microsoft', 'Microsoft'), { draft: 'Microsoft', pending: null })
})
test('host search updates apply when there is no pending user edit', () => {
  assert.deepEqual(reconcileSearch('old', null, 'new'), { draft: 'new', pending: null })
  assert.deepEqual(reconcileSearch('', '', 'old'), { draft: '', pending: '' })
})


test('Phase11 unresolved, contradictory and unstarted history never becomes successful or failed by default', () => {
  assert.equal(historyOutcome({ success: false, outcome: 'NeedsAttention' }, 'en').label, 'Unresolved')
  assert.equal(historyOutcome({ success: false, outcome: 'NotStarted' }, 'ar').label, 'لم تبدأ')
  assert.equal(historyOutcome({ success: false, outcome: 'NotStarted' }, 'en').showExit, false)
  assert.equal(historyOutcome({ success: false }, 'en').label, 'Unverified (legacy)')
  assert.equal(historyOutcome({ success: false, outcome: 'Succeeded' }, 'ar').label, 'غير محسوم')
  assert.equal(historyOutcome({ success: true, outcome: 'Failed' }, 'en').label, 'Unresolved')
  assert.equal(historyOutcome({ success: false, outcome: 'Failed' }, 'en').label, 'Failed')
})


test('Phase11 acknowledgment notices follow language changes without implying removal', () => {
  const english = 'Unresolved status acknowledged. Evidence was preserved; no operation was started.'
  const arabic = 'تم الإقرار بالحالة غير المحسومة. حُفظت الأدلة ولم تبدأ أي عملية.'
  assert.equal(localizeText(english, 'ar'), arabic)
  assert.equal(localizeText(arabic, 'en'), english)
  assert.equal(localizeText('Read-only observation recorded. This does not establish historical uninstall success.', 'ar'), 'حُفظت ملاحظة للقراءة فقط. لا تثبت نجاح الإزالة التاريخية.')
})

test('Queue row keeps its own exit code, category and code after later items finish', () => {
  const edge = { state: 'Failed', message: 'Uninstaller returned a failure exit code.\nUninstallerExitError\nExit code: 93', exitCode: 93, failureCode: 'UninstallerExitError' }
  const info = queueFailureInfo(edge, 'ar')
  assert.equal(info.exitCode, 93)
  assert.equal(info.code, 'UninstallerExitError')
  assert.equal(info.category, 'رفض المزيل أو فشله')
  assert.equal(queueFailureInfo({ ...edge, failureCode: undefined, exitCode: undefined }, 'en').exitCode, 93)
  assert.equal(queueFailureInfo({ state: 'Completed', message: 'Application registration was removed.', exitCode: 0 }, 'en').exitCode, null)
})

test('Queue categories separate UAC denial, privileges, refusal and stale registration', () => {
  const cat = (failureCode: string, state = 'Failed') => queueFailureInfo({ state, message: '', failureCode }, 'en').category
  assert.equal(cat('UacDenied', 'Cancelled'), 'UAC denied')
  assert.equal(cat('InsufficientPrivileges'), 'Insufficient privileges')
  assert.equal(cat('UninstallerExitError'), 'Uninstaller refused or failed')
  assert.equal(cat('MissingExecutable'), 'Uninstaller missing')
  assert.equal(cat('UntrackedElevation', 'NeedsUserAttention'), 'Unresolved')
  assert.equal(queueFailureInfo({ state: 'NotInstalled', message: 'x', failureCode: 'RegistrationAbsent' }, 'ar').category, 'غير مثبت حاليًا')
})


test('MSI preflight failure has a diagnosis without a fabricated process exit', () => {
  for (const language of ['ar', 'en']) {
    const info = queueFailureInfo({ state: 'Failed', message: 'Invalid MSI product code.\nInvalidMsiCommand\nThe uninstall command has no valid MSI product code.', failureCode: 'InvalidMsiCommand', exitCode: null }, language)
    assert.equal(info.exitCode, null)
    assert.equal(info.code, 'InvalidMsiCommand')
    assert.ok(info.category)
  }
})
