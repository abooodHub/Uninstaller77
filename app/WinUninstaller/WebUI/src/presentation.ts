
const unregisteredErrors: Record<string, [string, string]> = {
    AmbiguousBackup: ['More than one stored copy matches this target. Review the backups manually.', 'تطابق المسار أكثر من نسخة محفوظة. راجع النسخ يدويًا.'],
    PermanentDeletionStarted: ['Permanent deletion has started; this is not a complete restorable backup.', 'بدأ الحذف النهائي؛ هذه ليست نسخة كاملة قابلة للاستعادة.'],
    DeletionUnconfirmed: ['File deletion could not be confirmed. Remaining files require review.', 'تعذر تأكيد حذف الملف. تحتاج الملفات المتبقية إلى مراجعة.'],
    SourceReappeared: ['The original folder was recreated by another process. Review it again; new files were not deleted.', 'أعادت عملية أخرى إنشاء المجلد الأصلي. راجعه مجددًا؛ لم تُحذف الملفات الجديدة.'],
    ProtectedScope: ['This folder is outside the supported scope or is shared/protected.', 'المجلد خارج النطاق المدعوم أو مشترك أو محمي.'],
    RegisteredScope: ['A registered application overlaps this folder. Use its registered uninstall entry.', 'يتداخل برنامج مسجّل مع هذا المجلد. استخدم إدخال الإزالة المسجّل الخاص به.'],
    UnknownLayout: ['The folder is not a recognized deployment bundle or an isolated executable. Its other contents will be preserved.', 'المجلد ليس حزمة تنفيذ معروفة أو ملفًا تنفيذيًا معزولًا. ستبقى محتوياته دون تغيير.'],
    MixedData: ['Application files and data share this folder; automatic file deletion is unavailable.', 'توجد بيانات مع ملفات التطبيق في المجلد نفسه؛ الحذف الآلي غير متاح.'],
    ChangedScope: ['The folder changed after review. Close this review and open the shortcut action again.', 'تغيّر المجلد بعد المراجعة. أغلق المراجعة وافتح الإجراء من الاختصار مجددًا.'],
    ApplicationRunning: ['Close the application and its background processes, then try again.', 'أغلق التطبيق وعملياته الخلفية، ثم حاول مجددًا.'],
    FilesInUse: ['Some files are locked. Close the application before retrying.', 'بعض الملفات مقفولة. أغلق التطبيق قبل المحاولة مجددًا.'],
    LinkedPath: ['Linked folders and reparse points are not supported.', 'المجلدات المرتبطة ونقاط إعادة التوجيه غير مدعومة.'],
    ScopeTooLarge: ['The folder exceeds the supported scan limit.', 'يتجاوز المجلد الحد المدعوم للفحص.'],
    DifferentVolume: ['This operation currently requires the same disk volume.', 'تتطلب هذه العملية حاليًا وحدة التخزين نفسها.'],
    UnsupportedExecutable: ['The shortcut does not point to a supported Windows executable.', 'لا يشير الاختصار إلى ملف Windows تنفيذي مدعوم.'],
    InvalidReview: ['The confirmation is no longer valid. Open a new review.', 'لم يعد التأكيد صالحًا. افتح مراجعة جديدة.'],
    OperationBusy: ['Finish the current operation or unresolved queue review first.', 'أكمل العملية الحالية أو مراجعة الطابور غير المحسوم أولًا.'],
    InventoryUnavailable: ['Registration discovery was incomplete. No files were moved.', 'لم يكتمل اكتشاف التسجيلات. لم تُنقل ملفات.'],
    RestoreOccupied: ['The original destination is occupied. No existing files were overwritten.', 'المسار الأصلي مشغول. لم تُستبدل أي ملفات موجودة.'],
    RestoreParentMissing: ['The original parent folder is unavailable.', 'المجلد الأب الأصلي غير متاح.'],
    BackupChanged: ['The backup contents changed. Restore was refused.', 'تغيّر محتوى النسخة المحجورة؛ رُفضت الاستعادة.'],
    InvalidBackup: ['The restore receipt is invalid or has changed.', 'سجل الاستعادة غير صالح أو جرى تغييره.'],
    HistoryUnavailable: ['The file operation finished, but its history record could not be saved. The restore receipt is retained.', 'اكتملت عملية الملفات، لكن تعذر حفظ سجلها. سجل الاستعادة محفوظ.']
    , MoveNeedsReview: ['The folder changed during the move. The preserved backup needs manual review; no successful removal was recorded.', 'تغيّر المجلد أثناء النقل. تحتاج النسخة المحفوظة إلى مراجعة يدوية؛ لم تُسجّل إزالة ناجحة.']
  }

export function unregisteredErrorText(value: string, language: string): string {
  return unregisteredErrors[value.replace("UnregisteredApp:", "")]?.[language === "ar" ? 1 : 0] || ""
}

// Presentation only: never change stored records, engine messages or bridge requests.
export type BrowserExtension = { browser: string; profile: string; id: string; installationLocation?: string; hostPermissions?: string[]; contentScriptHosts?: string[]; permissions?: string[]; isPolicyManaged?: boolean }

/** Permission patterns are metadata only; this helper never labels an extension malicious. */
export function extensionPermissionImpact(item: Pick<BrowserExtension, 'hostPermissions' | 'contentScriptHosts' | 'permissions'>) {
  const hosts = [...new Set([...(item.hostPermissions || []), ...(item.contentScriptHosts || [])])]
  const broadSiteAccess = hosts.some(host => ['<all_urls>', '*://*/*', 'http://*/*', 'https://*/*'].includes(host))
  const sensitive = new Set(['tabs', 'history', 'cookies', 'clipboardRead', 'clipboardWrite', 'webRequest', 'debugger', 'management', 'nativeMessaging', 'downloads', 'geolocation', 'browsingData', 'proxy', 'contentSettings'])
  const sensitivePermissions = [...new Set(item.permissions || [])].filter(permission => sensitive.has(permission))
  return { broadSiteAccess, sensitivePermissions, verifiedSecurityFinding: false, riskAssessment: 'NotAssessed' as const }
}

export function historyOutcome(item: { success: boolean; outcome?: string; test?: boolean }, language: string) {
  let state = item.outcome || (item.success ? 'Succeeded' : 'LegacyUnverified')
  if (state === 'Succeeded' && !item.success || item.success && state !== 'Succeeded') state = 'NeedsAttention'
  const labels: Record<string, [string, string]> = {
    Succeeded: ['Success', 'نجاح'], Failed: ['Failed', 'فشل'], Cancelled: ['Cancelled', 'أُلغيت'],
    NeedsAttention: ['Unresolved', 'غير محسوم'], NotStarted: ['Not started', 'لم تبدأ'], NotInstalled: ['Already absent — skipped', 'غير مثبت حاليًا — تم التخطي'],
    FilesQuarantined: ['Files quarantined', 'عُزلت ملفات التطبيق'], FilesRestored: ['Files restored', 'استُعيدت ملفات التطبيق'],
    FilesDeleted: ['Application files deleted', 'حُذفت ملفات التطبيق'], FileDeletionIncomplete: ['File deletion incomplete', 'لم يكتمل حذف الملفات'],
    LegacyUnverified: ['Unverified (legacy)', 'غير موثق (سجل قديم)']
  }
  return { label: item.test ? language === 'ar' ? 'سجل اختبار' : 'Test record' : (labels[state] || labels.NeedsAttention)[language === 'ar' ? 1 : 0],
    tone: state === 'Succeeded' || state === 'FilesDeleted' || state === 'FilesRestored' ? 'text-good' : state === 'Failed' ? 'text-danger' : state === 'Cancelled' || state === 'NotStarted' || state === 'NotInstalled' ? 'cell-dim' : 'text-unresolved',
    showExit: !['NotStarted', 'NotInstalled', 'Succeeded', 'FilesQuarantined', 'FilesRestored', 'FilesDeleted', 'FileDeletionIncomplete'].includes(state) }
}

const residualStates: Record<string, [string, string]> = {
  NotScanned: ['Residuals were not scanned.', 'لم يتم فحص المخلفات.'],
  Scanning: ['No completed scan result; last recorded state: scanning.', 'لا توجد نتيجة فحص مكتملة؛ آخر حالة مسجلة: جارٍ الفحص.'],
  CompletedNoFindings: ['No findings in the checked locations.', 'لم يُعثر على مخلفات في المواقع المفحوصة.'],
  CompletedWithFindings: ['Scan completed with findings for review.', 'اكتمل الفحص مع نتائج للمراجعة.'],
  Unsupported: ['Residual scan not supported for this package type.', 'لم يتم فحص المخلفات — هذا النوع غير مدعوم حاليًا'],
  Failed: ['Residual scan failed; absence of leftovers is not established.', 'فشل فحص المخلفات؛ لم يتم تأكيد عدم وجودها.'],
  Cancelled: ['Residual scan cancelled; results are incomplete.', 'أُلغي فحص المخلفات؛ النتائج غير مكتملة.']
}

export function residualScanStatusText(value: string, language: string, installerType = '') {
  const state = value.startsWith('ResidualScan:') ? value.slice(13) : ''
  if (state && residualStates[state]) return residualStates[state][language === 'ar' ? 1 : 0]
  // Preserve legacy database values; do not repeat a known unsupported MSIX clean claim.
  if (/MSIX|Store|متجر/i.test(installerType) && /^Clean\s*\(/i.test(value))
    return residualStates.Unsupported[language === 'ar' ? 1 : 0]
  return localizeText(value, language)
}

export function reconcileSearch(draft: string, pending: string | null, server: string) {
  if (pending !== null) return { draft, pending: server === pending ? null : pending }
  return { draft: server, pending: null }
}

export function virtualRange(itemCount: number, scrollTop: number, viewportHeight: number, rowHeight: number, overscan = 4) {
  const count = Math.max(0, Math.floor(itemCount))
  const height = Math.max(0, viewportHeight)
  const row = Math.max(1, rowHeight)
  const top = Math.max(0, scrollTop)
  const start = Math.max(0, Math.floor(top / row) - Math.max(0, overscan))
  const end = Math.min(count, Math.ceil((top + height) / row) + Math.max(0, overscan))
  return { start, end, totalHeight: count * row }
}

/** Animate an item only the first time its stable identity enters this list instance. */
export function markFirstAppearance(id: string, appeared: Set<string>): boolean {
  if (appeared.has(id)) return false
  appeared.add(id)
  return true
}

export function browserGroups<T extends BrowserExtension>(items: T[], detectedBrowsers: string[] = []): { browser: string; items: T[] }[] {
  const groups = new Map<string, T[]>()
  for (const browser of detectedBrowsers) groups.set(browser, [])
  for (const item of items) {
    const current = groups.get(item.browser) ?? []
    current.push(item)
    groups.set(item.browser, current)
  }
  const preferred = ['Google Chrome', 'Microsoft Edge', 'Brave', 'Opera', 'Vivaldi']
  return [...groups].sort(([a], [b]) => {
    const ai = preferred.indexOf(a), bi = preferred.indexOf(b)
    return (ai < 0 ? preferred.length : ai) - (bi < 0 ? preferred.length : bi) || a.localeCompare(b)
  }).map(([browser, entries]) => ({ browser, items: entries }))
}

export function extensionIdentity(item: BrowserExtension): string {
  return JSON.stringify([item.browser, item.profile, item.id, item.installationLocation ?? ''])
}

/** Resolve stale/empty selections without ever falling back to mixed-browser rows. */
export function resolveExtensionBrowser(groups: { browser: string }[], selected: string): string {
  return groups.some(group => group.browser === selected) ? selected : groups[0]?.browser ?? ''
}

const arabic: Record<string, string> = {
  'No uninstaller was launched for this item. It was skipped because its exact registration is already absent.': 'لم تُشغّل أداة إزالة لهذا العنصر. تم تخطيه لأن تسجيل تثبيته المحدد لم يعد موجودًا.',
  'The Riot uninstaller refused removal because Riot games are still installed. Remove the games you choose separately, then retry Riot Client. Only applications explicitly confirmed in this queue can run; no Riot games are added automatically.': 'رفض مزيل Riot الإزالة لأن ألعاب Riot ما زالت مثبتة. أزل الألعاب التي تختارها بشكل منفصل ثم أعد محاولة إزالة Riot Client. لا تُشغّل إلا البرامج التي أكّدتها في هذا الطابور؛ لا تُضاف ألعاب Riot تلقائيًا.',
  'The installation registration is already absent.': 'تسجيل تثبيت البرنامج غير موجود حاليًا.',
  'No uninstaller was launched. Refresh the application list before trying again.': 'لم تُشغّل أي أداة إزالة. حدّث قائمة البرامج قبل المحاولة مجددًا.',
  'The current uninstall registration could not be verified.': 'تعذر التحقق من تسجيل الإزالة الحالي.',
  'The registered uninstall command changed after confirmation.': 'تغيّر أمر الإزالة المسجّل بعد التأكيد.',
  'No uninstaller was launched. Refresh the application list and review the current command.': 'لم تُشغّل أي أداة إزالة. حدّث قائمة البرامج وراجع الأمر الحالي.',
  'Riot Client cannot be removed while Riot games remain installed.': 'لا يمكن إزالة Riot Client ما دامت ألعاب Riot مثبتة.',
  'The Riot uninstaller refused removal because Riot games are still installed. Remove the games you choose separately, then retry Riot Client. No other application was removed automatically.': 'رفض مزيل Riot الإزالة لأن ألعاب Riot ما زالت مثبتة. أزل الألعاب التي تختارها بشكل منفصل ثم أعد محاولة إزالة Riot Client. لم تُزل أي برامج أخرى تلقائيًا.',
  'The uninstaller finished, but removal was not observed.': 'انتهت أداة الإزالة، لكن لم تُثبت إزالة البرنامج.',
  'The uninstaller requested a restart; the installation registration is still present.': 'طلبت أداة الإزالة إعادة تشغيل الجهاز؛ تسجيل البرنامج ما زال موجودًا.',
  'The installation registration is still present. No successful removal was verified. Remaining queue items were not started automatically.': 'تسجيل البرنامج ما زال موجودًا، ولم تُثبت إزالته. لم تُشغّل البرامج المتبقية في الطابور تلقائيًا.',
  'Not started after a completed uninstaller left its registration present.': 'لم تبدأ؛ انتهت الأداة السابقة وبقي تسجيل البرنامج موجودًا.',
  'Windows Installer does not recognize the registered product.': 'لا يتعرف Windows Installer على المنتج المسجّل للإزالة.',
  'The registered MSI product code was not found in this installation context. The entry may be stale or refer to another product or user context. No successful removal was verified.': 'لم يُعثر على رمز منتج MSI المسجّل ضمن سياق التثبيت الحالي. قد يكون التسجيل قديمًا أو يشير إلى منتج أو حساب مستخدم آخر. لم يتم تأكيد إزالة ناجحة.',
  'Uninstaller returned a failure exit code.': 'أعادت أداة الإزالة رمز فشل.',
  'The uninstaller reported access denied and ran without administrator rights.': 'أبلغت أداة الإزالة عن رفض الوصول وقد عملت دون صلاحيات المسؤول.',
  'The uninstaller reported access denied although it ran elevated.': 'أبلغت أداة الإزالة عن رفض الوصول رغم أنها عملت بصلاحيات مرتفعة.',
  'Administrative privileges were denied at the UAC prompt. No uninstaller was started.': 'رُفضت صلاحيات المسؤول في مطالبة UAC. لم تبدأ أداة الإزالة.',
  'Unresolved status acknowledged. Evidence was preserved; no operation was started.': 'تم الإقرار بالحالة غير المحسومة. حُفظت الأدلة ولم تبدأ أي عملية.',
  'Read-only observation recorded. This does not establish historical uninstall success.': 'حُفظت ملاحظة للقراءة فقط. لا تثبت نجاح الإزالة التاريخية.',
  'Completion was not established. Acknowledgment preserves the unknown outcome and never restarts a queued uninstall.': 'لم يتم تأكيد انتهاء العملية. الإقرار يحفظ النتيجة غير المحسومة ولا يعيد تشغيل أي إزالة في الطابور.',
  'Usage and bundle classification are unavailable: no reliable evidence is recorded.': 'تصنيف الاستخدام والباقات غير متاح: لا توجد أدلة موثوقة مسجلة.',
  'Wait for a confirmed inventory before exporting.': 'انتظر اكتمال جرد موثوق قبل التصدير.',
  'A previous queue was interrupted. Review the applications manually; no removal will resume automatically.': 'انقطع طابور سابق. راجع حالة البرامج يدويًا؛ لن تُستأنف أي إزالة تلقائيًا.',
  'Recorded fixture evidence: completion was not established.': 'دليل اختبار مسجل: لم يتم تأكيد انتهاء العملية.',
  'Read-only Registry observation; historical completion remains unresolved.': 'ملاحظة سجل للقراءة فقط؛ يبقى انتهاء العملية التاريخية غير محسوم.',
  'Interruption detected on application startup; no operation resumed.': 'اكتُشف الانقطاع عند تشغيل التطبيق؛ لم تُستأنف أي عملية.',
  'User kept the unresolved operation pending for review.': 'أبقى المستخدم العملية غير المحسومة للمراجعة.',
  'Completion is uncertain. The queue is paused for review; the next application will not start.': 'تعذر تأكيد انتهاء أداة الإزالة. الطابور متوقف للمراجعة؛ لن يبدأ البرنامج التالي.',
  'Explicit UI test fixture; no uninstaller was launched.': 'اختبار واجهة اصطناعي صريح؛ لم تُشغّل أي أداة إزالة.',
  'MSIX verification query failed.': 'تعذر فحص حالة حزمة Windows.',
  'MSIX completion receipt was missing or invalid.': 'لم تُرجع إزالة حزمة Windows نتيجة موثوقة.',
  'MSIX deployment and verification did not agree.': 'تعارضت نتيجة إزالة حزمة Windows مع فحص حالتها.',
  'This Windows package does not support removal.': 'يمنع Windows إزالة هذه الحزمة.',
  'The package was already absent; no removal was performed.': 'الحزمة غير مثبتة بالفعل؛ لم تُنفّذ إزالة.',
  'MSIX package removal failed.': 'فشلت إزالة حزمة Windows.',
  'MSIX process could not be launched.': 'تعذر تشغيل عملية إزالة حزمة Windows.',
  'The read-only package query timed out.': 'انتهت مهلة فحص حالة الحزمة دون تعديلها.',

  'The uninstall launcher exited; waiting for related processes to finish...': 'انتهى مشغّل الإزالة؛ بانتظار انتهاء عملياته الفرعية...',
  'External browser handoff was verified; the browser can remain open.': 'تم التحقق من انتهاء الإزالة؛ يمكن إبقاء المتصفح الخارجي مفتوحًا.',
  'Removed orphaned desktop shortcuts:': 'تم حذف اختصارات سطح المكتب التي اختفت ملفاتها:',
  'Some desktop shortcuts were kept for review.': 'أُبقيت بعض اختصارات سطح المكتب للمراجعة.',
  'Uninstaller completion could not be established confidently.': 'تعذر تأكيد انتهاء أداة الإزالة.',
  'Previous uninstaller completion is still uncertain.': 'لم يُحسم انتهاء أداة الإزالة السابقة.',
  'Retry verification or stop for manual review before starting another operation.': 'أعد التحقق أو أوقف الطابور للمراجعة قبل بدء عملية أخرى.',
  'No reliable live process evidence is available.': 'لا تتوفر أدلة موثوقة عن العملية الحالية.',
  'Review the interrupted operation manually.': 'راجع حالة العملية المنقطعة يدويًا.',
  'Uninstaller completion is still uncertain.': 'لم يُحسم انتهاء أداة الإزالة بعد.',
  "Reliable tracking of this elevated uninstaller's full process tree is unavailable. Current registration is checked separately; no further uninstall will start automatically.": 'لا يتوفر تتبّع موثوق لكل العمليات التابعة لأداة الإزالة المرتفعة. يُفحص تسجيل التثبيت الحالي بصورة مستقلة؛ لن تبدأ إزالة أخرى تلقائيًا.',
  'A related process is still running. No additional application will be started.': 'ما زالت عملية مرتبطة قيد التشغيل. لن يبدأ برنامج آخر.',
  'No additional application will be started.': 'لن يبدأ أي برنامج إضافي.',
  'Removal is still awaiting verification.': 'لا تزال الإزالة بانتظار التحقق.',
  'The queue remains paused.': 'يبقى الطابور متوقفًا للمراجعة.',
  'MSIX removal is still awaiting completion.': 'لا تزال إزالة حزمة MSIX قيد الانتظار.',
  'The queue must pause.': 'يجب إيقاف الطابور للمراجعة.',
  'Not started after cancellation.': 'لم يبدأ بعد إلغاء العملية.',
  'Not started; the previous uninstall needs verification.': 'لم يبدأ؛ الإزالة السابقة تحتاج تحققًا.',
  'A related process may still be active or elevated process tracking is incomplete. The batch is paused for review.': 'قد تظل عملية فرعية نشطة أو قد يكون تتبع العملية المرتفعة غير مكتمل. أُوقف الطابور للمراجعة.',

  'Unknown': 'غير معروف', 'Ready': 'جاهز', 'Not provided': 'غير متوفر', 'Command details hidden for privacy': 'تفاصيل الأمر مخفية للخصوصية', 'Registry': 'سجل Windows', '64-bit': '64 بت', '32-bit': '32 بت', 'Neutral': 'محايد',
  'Standard EXE': 'ملف EXE قياسي', 'MSIX / Store': 'MSIX / المتجر', 'High': 'مرتفع', 'Medium': 'متوسط', 'Low': 'منخفض',
  '📁 Folder': 'مجلد', '📄 File': 'ملف', '🔑 Registry Key': 'مفتاح سجل', '🏷 Registry Value': 'قيمة سجل',
  'All Programs': 'كل البرامج', 'Bundleware': 'أدوات الباقات',
  'Recently Installed': 'مثبت مؤخرًا', 'Large Programs': 'البرامج الكبيرة', 'Infrequently Used': 'استخدام غير متكرر',
  'Software Health': 'صحة البرمجيات', 'Browser Extensions': 'ملحقات المستعرض',
  'Windows Apps': 'تطبيقات ويندوز', 'Built-in Windows Apps': 'تطبيقات ويندوز المدمجة', 'Third-Party Apps': 'تطبيقات الجهات الأخرى',
  'Control Center': 'مركز التحكم', 'Force Uninstall': 'إزالة البرامج المستعصية',
  'File Shredder': 'تمزيق الملفات', 'System Restore': 'استعادة النظام', 'Uninstall History': 'سجل الإزالة', 'Export List': 'تصدير القائمة',
  'Program Leftovers': 'بقايا إزالة التثبيت', 'Outdated Software': 'برامج قديمة', 'Redundant Files': 'ملفات البرامج الزائدة عن الحاجة',
  'Software Hibernation': 'إسبات البرامج', 'Software Permissions': 'أذونات البرامج', 'Disturbing Notifications': 'الإخطارات المزعجة',
  'Uninstall Issues': 'البرامج ذات المشاكل المتعلقة بإلغاء التثبيت', 'Installation Files': 'ملفات التثبيت', 'Malicious Software & Extensions': 'البرامج والإضافات الضارة',
  'Cancelled before launch.': 'أُلغيت العملية قبل التشغيل.', 'No uninstaller was started.': 'لم يتم تشغيل أداة إزالة.',
  'Concurrent uninstall blocked.': 'توجد عملية إزالة أخرى قيد التشغيل.',
  'Another uninstallation process is already in progress. Please wait for it to complete.': 'توجد عملية إزالة أخرى قيد التشغيل. انتظر حتى تنتهي.',
  'No uninstall command found.': 'لم يُعثر على أمر إزالة مسجل.',
  'Unsupported uninstall command.': 'أمر الإزالة غير مدعوم.', 'Only direct executable uninstallers are launched.': 'يُسمح بتشغيل ملفات أدوات الإزالة التنفيذية المباشرة فقط.',
  'Invalid MSI product code.': 'رمز منتج MSI غير صالح.', 'The uninstall command has no valid MSI product code.': 'لا يتضمن أمر الإزالة رمز منتج MSI صالحًا.',
  'Uninstaller executable was not found.': 'ملف أداة الإزالة المسجل غير موجود.', 'Failed to launch process.': 'تعذر تشغيل العملية.',
  'UAC Prompt was cancelled by user.': 'أُلغيت مطالبة صلاحيات المسؤول.', 'Administrative privileges were denied at the UAC prompt.': 'لم تُمنح صلاحيات المسؤول المطلوبة.',
  'Cancelled by user.': 'ألغى المستخدم العملية.', 'Uninstallation cancelled.': 'أُلغيت الإزالة.', 'Process launch error.': 'تعذر تشغيل أداة الإزالة.',
  'Application registration was removed.': 'تم التحقق من إزالة تسجيل التطبيق.',
  'Application was uninstalled, but leftover files/registry keys were detected.': 'أُزيل التطبيق وعُثر على ملفات أو مفاتيح سجل متبقية.',
  'Application still appears registered in the system.': 'لا يزال التطبيق مسجلًا في النظام.',
  'Uninstaller requested a reboot; removal was verified, but restart may still be required.': 'تم التحقق من الإزالة؛ طلبت أداة الإزالة إعادة تشغيل الجهاز.',
  'Restart required; removal cannot yet be verified.': 'يلزم إعادة التشغيل؛ لم يتسن التحقق من الإزالة بعد.',
  'Invalid MSIX identity.': 'معرّف حزمة MSIX غير صالح.', 'Package identity contained unsupported characters.': 'يحتوي معرّف الحزمة على أحرف غير مدعومة.',
  'PowerShell launch failed.': 'تعذر تشغيل PowerShell.', 'MSIX package removal was verified.': 'تم التحقق من إزالة حزمة MSIX.',
  'MSIX package removal encountered errors.': 'حدثت أخطاء أثناء إزالة حزمة MSIX.', 'MSIX removal exception.': 'حدث خطأ أثناء إزالة حزمة MSIX.',
  'Not scanned': 'لم يُفحص', 'Not Scanned': 'لم يُفحص', 'Scanning...': 'جارٍ الفحص…', 'Not scanned (batch queue)': 'لم يُفحص (طابور إزالة)',
  'Clean (No leftovers found)': 'لم يُعثر على مخلفات', 'Skipped by user': 'تخطاه المستخدم',
  'Manifest is outside the quarantine folder.': 'ملف الاستعادة خارج مجلد الحجر.',
  'Manifest or restore target is unsafe or already occupied.': 'ملف الاستعادة أو مسارها غير آمن، أو المسار مستخدم بالفعل.',
  'Restore parent is unavailable or is a reparse point.': 'مجلد الاستعادة غير متاح أو يشير إلى مسار آخر.',
  'Former install location; review contents and shared use before moving to quarantine.': 'مجلد التثبيت السابق؛ راجع محتوياته واستخدامه المشترك قبل نقله إلى الحجر.',
  'Name match only; ownership unverified. Informational, not eligible for cleanup.': 'تطابق في الاسم فقط؛ لم يتم التحقق من الملكية. للمراجعة ولا يمكن تنظيفه.',
  'Name match only; shared Registry data possible. Informational, not eligible for cleanup.': 'تطابق في الاسم فقط؛ قد تكون بيانات السجل مشتركة. للمراجعة ولا يمكن تنظيفها.',
  'Preparing scan': 'جارٍ تجهيز الفحص', 'Starting Deep Residual Scan...': 'جارٍ بدء فحص المخلفات…',
  'The desktop host did not respond.': 'لم يستجب التطبيق. حاول مرة أخرى.', 'Request failed': 'تعذر إكمال الطلب',
  'This interface must run inside WinUninstaller.': 'يجب فتح هذه الواجهة من تطبيق WinUninstaller.',
  'An uninstall or cleanup operation is still running. Wait for its result before closing.': 'توجد عملية إزالة أو تنظيف قيد التشغيل. انتظر نتيجتها قبل إغلاق التطبيق.',
}
const phrases = Object.entries(arabic).sort((a, b) => b[0].length - a[0].length)
const englishPhrases = Object.entries(arabic)
  .map(([english, arabicText]) => [arabicText, english] as const)
  .sort((a, b) => b[0].length - a[0].length)

export function queueProgress(op: { isRunning?: boolean; processed?: number; total?: number; results?: unknown[] }) {
  const total = op.total || 0, processed = op.processed || 0, results = op.results?.length || 0
  const resolved = op.isRunning ? processed : results
  return { attempts: `${processed}/${total}`, resolved: `${Math.min(total, resolved)}/${total}`, percent: total ? Math.min(100, Math.round(resolved / total * 100)) : 0 }
}

export function verifiedQueuePercent(op: { total?: number; successful?: number }) {
  const total = Math.max(0, op.total ?? 0)
  return total ? Math.round(Math.min(total, Math.max(0, op.successful ?? 0)) / total * 100) : 0
}

/** The reason belongs to its own attempt, even after another application finishes. */
export function queueFailureReason(item: { state: string; message?: string }, language: string): string {
  if (!['Failed', 'NeedsUserAttention', 'NeedsAttention'].includes(item.state)) return ''
  const summary = (item.message || '').split(/\r?\n/, 1)[0].split(' — ', 1)[0].trim()
  return localizeText(summary, language).slice(0, 600)
}

const failureCategories: Record<string, [string, string]> = {
  InvalidMsiCommand: ['Invalid Windows Installer command', 'أمر Windows Installer غير صالح'],
  UacDenied: ['UAC denied', 'رفض UAC'],
  InsufficientPrivileges: ['Insufficient privileges', 'نقص الصلاحيات'],
  ElevationRequired: ['Insufficient privileges', 'نقص الصلاحيات'],
  LaunchAccessDenied: ['Launch access denied', 'رُفض الوصول عند التشغيل'],
  AccessDenied: ['Access denied while elevated', 'رُفض الوصول رغم الصلاحيات المرتفعة'],
  UninstallerExitError: ['Uninstaller refused or failed', 'رفض المزيل أو فشله'],
  MsiInstallFailure: ['Uninstaller refused or failed', 'رفض المزيل أو فشله'],
  MsiBusy: ['Windows Installer busy', 'Windows Installer مشغول'],
  RegistrationAbsent: ['Already absent', 'غير مثبت حاليًا'],
  MissingExecutable: ['Uninstaller missing', 'ملف أداة الإزالة مفقود'],
  MsiUnknownProduct: ['MSI product not found in this context', 'منتج MSI غير موجود ضمن السياق الحالي'],
  LaunchFailed: ['Launch failed', 'تعذر التشغيل'],
  NeedsUserAttention: ['Unresolved', 'غير محسوم'],
  UntrackedElevation: ['Unresolved', 'غير محسوم'],
  RemovalNotObserved: ['Unresolved', 'غير محسوم'],
  RestartPending: ['Unresolved', 'غير محسوم'],
}

/** Per-attempt diagnosis: category, failure code and exit code stay on their own row. */
export function queueFailureInfo(item: { state: string; message?: string; failureCode?: string; exitCode?: number | null }, language: string) {
  const reason = queueFailureReason(item, language)
  const lines = (item.message || '').split(/\r?\n/).map(line => line.trim())
  const fromMessage = /^[A-Za-z]{4,}$/.test(lines[1] || '') ? lines[1] : ''
  const code = item.failureCode && item.failureCode !== 'None' ? item.failureCode : fromMessage
  const parsedExit = lines.map(line => /^Exit code:\s*(-?\d+)$/.exec(line)?.[1]).find(value => value !== undefined)
  const exitCode = item.exitCode ?? (parsedExit === undefined ? null : Number(parsedExit))
  const category = code ? failureCategories[code]?.[language === 'ar' ? 1 : 0] || '' : ''
  const failed = ['Failed', 'NeedsUserAttention', 'NeedsAttention', 'Cancelled', 'NotInstalled'].includes(item.state)
  return { reason, code: failed ? code : '', category: failed ? category : '', exitCode: failed ? exitCode : null }
}

export function localizeText(value: unknown, language: string): string {
  const text = String(value ?? '')
  if (!text) return text
  if (text.startsWith('UnregisteredApp:')) return unregisteredErrorText(text, language) || text
  if (language === 'en') {
    let result = text
    for (const [source, translated] of englishPhrases) {
      if (result === source) return translated
      if (source.length > 12) result = result.replaceAll(source, translated)
    }
    return result
  }
  if (language !== 'ar') return text
  let result = text
  for (const [source, translated] of phrases) {
    if (result === source) return translated
    if (source.length > 20) result = result.replaceAll(source, translated)
  }
  return result
    .replace(/\[arguments hidden for privacy\]/g, '[المعاملات مخفية للخصوصية]')
    .replace(/Removal could not be verified \(exit code (-?\d+)\)\./g, 'تعذر التحقق من الإزالة (رمز الخروج $1).')
    .replace(/Exit code:\s*(-?\d+)/g, 'رمز الخروج: $1')
    .replace(/Details:\s*/g, 'التفاصيل: ')
    .replace(/No uninstall string found for '(.+)' in the Windows Registry\./g, 'لا يوجد أمر إزالة مسجل للتطبيق «$1» في سجل Windows.')
    .replace(/Preparing uninstallation for:? (.+?)(?:\.\.\.|$)/g, 'جارٍ تجهيز إزالة $1…')
    .replace(/Launching uninstaller for (.+?)\.\.\./g, 'جارٍ تشغيل أداة إزالة $1…')
    .replace(/Uninstaller running \(PID: (\d+)\)\. Waiting for completion\.\.\./g, 'أداة الإزالة تعمل (PID: $1). جارٍ انتظار الانتهاء…')
    .replace(/Uninstaller exited with code (\d+)\./g, 'انتهت أداة الإزالة برمز $1.')
    .replace(/Mode: Quiet \/ Silent/g, 'الوضع: إزالة صامتة')
    .replace(/Mode: Standard Interactive/g, 'الوضع: إزالة تفاعلية')
    .replace(/Successfully uninstalled (.+)/g, 'تمت إزالة $1 بنجاح')
    .replace(/Restored to (.+)/g, 'تمت الاستعادة إلى $1')
    .replace(/Moved to quarantine: (.+)/g, 'نُقل إلى الحجر: $1')
    .replace(/Refused unverified or shared item: (.+)/g, 'رُفض عنصر مشترك أو غير موثّق: $1')
    .replace(/Could not move (.+):/g, 'تعذر نقل $1:')
    .replace(/Quarantined (\d+) items/g, 'نُقل $1 عنصرًا إلى الحجر')
    .replace(/Access (?:is )?denied\.?/gi, 'تم رفض الوصول.')
    .replace(/The system cannot find the (?:file|path) specified\.?/gi, 'يتعذر العثور على الملف أو المسار المحدد.')
}

export function localizeState<T extends { language: string; appsPage: any; historyPage: any }>(state: T): T {
  const t = (value: unknown) => localizeText(value, state.language)
  const app = (item: any) => item ? { ...item, size: t(item.size), installDate: t(item.installDate), architecture: t(item.architecture), installerType: t(item.installerType), source: t(item.source), uninstallCommand: t(item.uninstallCommand), quietUninstallCommand: t(item.quietUninstallCommand) } : item
  const history = (item: any) => {
    if (!item) return item
    const count = Number(item.cleanedItemsCount || 0)
    const size = (Number(item.cleanedSizeBytes || 0) / 1024 / 1024).toFixed(1)
    const residualStatus = count > 0
      ? state.language === 'ar'
        ? `نُقل ${count === 1 ? 'عنصر واحد' : `${count} عناصر`} إلى الحجر (${size} م.ب)`
        : `${count} item${count === 1 ? '' : 's'} moved to quarantine (${size} MB)`
      : residualScanStatusText(!item.success && item.residualStatus === 'Scanning...' ? 'Not scanned' : item.residualStatus || '', state.language, item.installerType)
    return { ...item, installerType: t(item.installerType), residualStatus, notes: t(item.notes) }
  }
  const p = state.appsPage, op = p.operation || {}, residual = p.residual || {}, hp = state.historyPage
  return { ...state, appsPage: { ...p, status: t(p.status), items: (p.items || []).map(app), details: app(p.details), confirm: app(p.confirm), operation: { ...op, review: op.review ? { ...op.review, observation: t(op.review.observation), items: (op.review.items || []).map((r: any) => ({ ...r, evidence: t(r.evidence), registeredCommand: t(r.registeredCommand) })) } : undefined, app: app(op.app), status: t(op.status), stage: t(op.stage), errorDetails: t(op.errorDetails), logs: (op.logs || []).map(t), results: (op.results || []).map((r: any) => ({ ...r, app: app(r.app), message: t(r.message) })) }, residual: { ...residual, message: t(residual.message), stage: t(residual.stage), items: (residual.items || []).map((r: any) => ({ ...r, type: t(r.type), size: t(r.size), confidence: t(r.confidence), reason: t(r.reason) })) } }, historyPage: { ...hp, records: (hp.records || []).map(history), details: history(hp.details) } }
}

// Counts are meaningful only after a completed inventory, including a genuine empty result.
export function inventoryCount(page: { scanned?: boolean; loading?: boolean; items?: unknown[] }, pending: string, loading: string) {
  return page.loading ? loading : page.scanned ? String(page.items?.length ?? 0) : pending
}

export function historyTimestamp(value: string) {
  const parts = value.trim().match(/^(.*?)[T ]+(\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AP]M)?)(?:\.\d+)?$/i)
  return parts ? { date: parts[1], time: parts[2] } : { date: value, time: '' }
}
