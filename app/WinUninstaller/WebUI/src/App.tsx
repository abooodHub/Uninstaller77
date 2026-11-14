import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { flushSync } from 'react-dom'
declare const __APP_VERSION__: string
import {
  AppWindow, ArrowDownAZ, ArrowDownWideNarrow, ArrowUpDown, BadgeCheck,
  Check, ChevronDown, CircleAlert, CircleCheck,
  Clock3, ExternalLink, FileText, FolderOpen, History, Info,
  LayoutGrid, LoaderCircle, Package, Power, Puzzle,
  RefreshCw, RotateCcw, Search, Settings, ShieldCheck,
  Trash2, X
} from 'lucide-react'
import './styles.css'
import './theme.css'
import './desktop.css'
import './phase10.css'
import './motion.css'
import './queue-progress.css'
import './uninstall-flow.css'
import './unregistered.css'
import './workspace.css'
import { words } from './ui-strings'
import productIcon from './assets/u77.png'
import { unregisteredErrorText, localizeState, localizeText, queueProgress, inventoryCount, historyTimestamp, browserGroups, resolveExtensionBrowser, extensionIdentity, extensionPermissionImpact, verifiedQueuePercent, reconcileSearch, historyOutcome, queueFailureInfo } from './presentation'
import type { AppItem, ExtensionItem, HistoryItem, ResidualItem, State, RequestMessage } from './host-contract'
import type { HostAction, HostOperation } from './host-operations'
import { AppIcon } from './components/AppIcon'
import { BrowserIcon } from './components/BrowserIcon'
import { StartupPage } from './components/StartupPage'
import { InstallationPage } from './components/InstallationPage'
import { installationWords } from './installation-strings'
import { VirtualList } from './components/VirtualList'
import { useFocusTrap } from './hooks/use-focus-trap'

type LanguagePack = typeof words.en
type Labels = Record<string, string>

const defaultState: State = {
  language: 'en', direction: 'ltr', reduceMotion: false, settingsOpen: false, notice: '', noticeIsError: false,
  page: 'apps', activeSidebarItem: 'AllApps', navigation: {},
  appsPage: {
    items: [], total: 0, filtered: 0, recent: 0, large: 0, store: 0, bundleware: 0, logged: 0, infrequent: 0,
    windowsApps: 0, thirdPartyApps: 0, selected: 0, category: 'All', sort: 'NameAsc', labels: {},
    operation: { open: false, isRunning: false, isBatch: false, logs: [], results: [] },
    residual: { open: false, items: [], summary: [] }
  },
  historyPage: { records: [], labels: {} },
  extensionsPage: { loading: false, scanned: false, items: [] },
  startupPage: { loading: false, scanned: false, failed: false, failedSources: 0, items: [] },
}

function makeDevPreviewApp(id: string, name: string): AppItem {
  return { id, name, publisher: 'Microsoft Corporation', version: '1.0', size: '24 MB', sizeKb: 24576,
    installDate: '20261106', architecture: '64-bit', installerType: 'EXE', source: 'Fixture', icon: null,
    selected: true, installLocation: '', registryKey: '', uninstallCommand: '', quietUninstallCommand: '' }
}

const devPreviewFirst = makeDevPreviewApp('preview-1', 'Microsoft Visual C++ Runtime')
const devPreviewCurrent = makeDevPreviewApp('preview-2', 'Copilot')

function makeDevPreviewState(): State | null {
  if (!import.meta.env.DEV || typeof window === 'undefined') return null
  const mode = new URLSearchParams(window.location.search).get('preview')
  if (!['confirm', 'running', 'complete', 'residual'].includes(mode || '')) return null
  const complete = mode === 'complete' || mode === 'residual'
  const operation: State['appsPage']['operation'] = {
    open: mode === 'running' || mode === 'complete', preview: true, isRunning: mode === 'running', isBatch: true,
    state: complete ? 'Succeeded' : 'Running', operationState: complete ? 'Completed' : 'Running', total: 2,
    processed: complete ? 2 : 1, successful: complete ? 2 : 1, failed: 0, skipped: 0, remaining: 0,
    elapsed: '00:01:44', status: complete ? 'Both applications were removed successfully.' : 'Waiting for the uninstaller and its workers',
    stage: complete ? 'Verified removal' : 'Waiting for the uninstaller and its workers', app: devPreviewCurrent,
    queueItems: [
      { operationId: 'preview-op-1', app: devPreviewFirst, state: 'Completed' },
      { operationId: 'preview-op-2', app: devPreviewCurrent, state: complete ? 'Completed' : 'Running' },
    ],
    results: complete
      ? [{ app: devPreviewFirst, state: 'Succeeded', message: 'Verified removal' }, { app: devPreviewCurrent, state: 'Succeeded', message: 'Verified removal' }]
      : [{ app: devPreviewFirst, state: 'Succeeded', message: 'Verified removal' }],
    logs: ['Synthetic visual preview. No uninstall engine was started.'],
  }
  return {
    ...defaultState,
    appsPage: {
      ...defaultState.appsPage,
      items: [devPreviewFirst, devPreviewCurrent], total: 2, filtered: 2, selected: 2,
      confirm: mode === 'confirm' ? devPreviewCurrent : null,
      operation,
      residual: mode === 'residual' ? {
        open: true, reviewId: 'preview-residual', selectionVersion: 1, scannedLocations: 6, size: '0 B', foundSelectable: true,
        summary: [], groups: [{ app: devPreviewCurrent, offset: 0, count: 3, status: 'Verified installation leftovers' }],
        items: [
          { index: 0, title: 'Application data', path: 'C:\\Users\\Preview\\AppData\\Local\\Copilot', size: '212.4 MB', canClean: true, selected: false },
          { index: 1, title: 'Cached package data', path: 'C:\\Users\\Preview\\AppData\\Local\\Packages\\Copilot', size: '66.5 MB', canClean: true, selected: false },
          { index: 2, title: 'Registry evidence', path: 'HKCU\\Software\\Preview\\Copilot', size: 'Review only', canClean: false, selected: false },
        ],
      } : defaultState.appsPage.residual,
    },
  }
}

const devPreviewState = makeDevPreviewState()

function useHost() {
  const [state, setState] = useState<State>(devPreviewState || defaultState)
  const [hasHostState, setHasHostState] = useState(Boolean(devPreviewState))
  const [toast, setToast] = useState('')
  const [toastIsError, setToastIsError] = useState(false)
  const [sharedDetailsId, setSharedDetailsId] = useState('')
  const sharedDetailsIdRef = useRef('')
  const stateRef = useRef(state)
  const pending = useRef(new Map<string, (message: RequestMessage) => void>())

  const send = useCallback((op: HostOperation, payload: Record<string, unknown> = {}) => new Promise<RequestMessage>((resolve, reject) => {
    if (op === 'openDetails' && typeof payload.appId === 'string') {
      sharedDetailsIdRef.current = payload.appId
      setSharedDetailsId(payload.appId)
    } else if (op === 'closeDetails') {
      sharedDetailsIdRef.current = stateRef.current.appsPage?.details?.id || sharedDetailsIdRef.current
    }
    const id = crypto.randomUUID()
    const timer = window.setTimeout(() => { pending.current.delete(id); reject(new Error('The desktop host did not respond.')) }, 30000)
    pending.current.set(id, message => {
      clearTimeout(timer)
      if (message.type === 'response' && !message.ok) {
        reject(new Error(message.error || 'Request failed'))
      } else {
        resolve(message)
      }
    })
    window.chrome?.webview?.postMessage({ id, op, payload })
    if (!window.chrome?.webview) {
      clearTimeout(timer)
      pending.current.delete(id)
      reject(new Error('This interface must run inside Uninstaller 77.'))
    }
  }), [])

  useEffect(() => {
    const webview = window.chrome?.webview
    if (!webview) return
    const applyState = (next: State) => {
      if ((next.revision ?? 0) < (stateRef.current.revision ?? 0)) return
      const previousHasDetails = Boolean(stateRef.current.appsPage?.details)
      const nextHasDetails = Boolean(next.appsPage?.details)
      const wantsSharedTransition = Boolean(sharedDetailsIdRef.current && nextHasDetails && !previousHasDetails)
      const prefersMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches || next.reduceMotion
      const transitionDocument = document as Document & { startViewTransition?: (callback: () => void) => unknown }
      const commit = () => flushSync(() => {
        stateRef.current = next
        setState(next)
        setHasHostState(true)
      })
      if (wantsSharedTransition && !prefersMotion && transitionDocument.startViewTransition) {
        try { transitionDocument.startViewTransition(commit) } catch { commit() }
      } else commit()
      if (previousHasDetails && !nextHasDetails) {
        window.setTimeout(() => {
          if (!stateRef.current.appsPage?.details) {
            sharedDetailsIdRef.current = ''
            setSharedDetailsId('')
          }
        }, 280)
      }
    }
    const receive = (event: MessageEvent<RequestMessage>) => {
      const message = event.data
      if (message.type === 'state') { applyState(message.state); return }
      if (message.type === 'error') { setToastIsError(true); setToast(message.error); return }
      const resolve = pending.current.get(message.id)
      if (resolve) {
        pending.current.delete(message.id)
        if (message.state) applyState(message.state)
        resolve(message)
      }
    }
    webview.addEventListener('message', receive)
    void send('getState').catch(error => { setToastIsError(true); setToast(error.message) })
    return () => webview.removeEventListener('message', receive)
  }, [send])

  const act = useCallback(async (op: HostOperation, payload: Record<string, unknown> = {}) => {
    try {
      if (stateRef.current.measurePerformance && (op === 'search' || op === 'sort'))
        void send('performanceMark', { action: op === 'search' ? 'Search' : 'Sort' })
      await send(op, payload)
      return true
    } catch (error) {
      setToastIsError(true)
      setToast(error instanceof Error ? error.message : String(error))
      return false
    }
  }, [send])

  useEffect(() => {
    if (!toast) return
    const timer = window.setTimeout(() => setToast(''), 4600)
    return () => clearTimeout(timer)
  }, [toast])

  return { state, setState, hasHostState, act, toast, setToast, toastIsError, setToastIsError, sharedDetailsId }
}

function App() {
  const host = useHost()
  if (!host.hasHostState && typeof window !== 'undefined' && window.chrome?.webview) {
    return <div className="desktop-startup" aria-busy="true"><img src={productIcon} alt="Uninstaller 77" /></div>
  }
  return <DesktopApp host={host} />
}

function DesktopApp({ host }: { host: ReturnType<typeof useHost> }) {
  const { act, setToast, setToastIsError } = host
  const { sharedDetailsId } = host
  const state = useMemo(() => localizeState(host.state) as State, [host.state])
  const lang = words[state.language] || words.en
  const rtl = state.language === 'ar'
  const p = state.appsPage
  const hp = state.historyPage
  const labels = p.labels || {}
  const op = p.operation || {}
  const residual = p.residual || {}
  const [search, setSearch] = useState('')
  const pendingSearch = useRef<string | null>(null)
  const [queueReview, setQueueReview] = useState<AppItem[] | null>(null)
  const [extensionBrowser, setExtensionBrowser] = useState('')
  const lastNotice = useRef('')
  const mainPanelRef = useRef<HTMLElement>(null)

  const selectedItems: AppItem[] = useMemo(() => (p.items || []).filter((item: AppItem) => item.selected), [p.items])

  useEffect(() => {
    const panel = mainPanelRef.current
    const systemMotion = window.matchMedia('(prefers-reduced-motion: reduce)')
    if (!panel || state.reduceMotion || systemMotion.matches || typeof panel.animate !== 'function') return
    const entrance = panel.animate([
      { opacity: 0.45, transform: 'translateY(7px)' },
      { opacity: 1, transform: 'translateY(0)' }
    ], { duration: 220, easing: 'cubic-bezier(.2,.8,.2,1)' })
    const stop = () => entrance.cancel()
    systemMotion.addEventListener('change', stop)
    return () => { entrance.cancel(); systemMotion.removeEventListener('change', stop) }
  }, [state.page, p.category, state.reduceMotion])

  useEffect(() => {
    const next = reconcileSearch(search, pendingSearch.current, p.search || '')
    pendingSearch.current = next.pending
    if (next.draft !== search) setSearch(next.draft)
  }, [p.search, search])
  useEffect(() => {
    const timer = window.setTimeout(() => { if (search !== (p.search || '')) void act('search', { value: search }) }, 110)
    return () => clearTimeout(timer)
  }, [search, p.search, act])

  useLayoutEffect(() => {
    document.documentElement.lang = state.language
    document.documentElement.dir = state.language === 'ar' ? 'rtl' : 'ltr'
    document.documentElement.classList.toggle('reduce-motion', state.reduceMotion)
  }, [state.language, state.direction, state.reduceMotion])

  useEffect(() => {
    if (!state.notice || state.notice === lastNotice.current) return
    lastNotice.current = state.notice
    setToastIsError(Boolean(state.noticeIsError))
    setToast(state.notice)
  }, [state.notice, state.noticeIsError, setToast, setToastIsError])

  useEffect(() => {
    if (!state.measurePerformance) return
    let last = 0
    let idleTimer = 0
    const mark = (action: string) => {
      const now = performance.now()
      if (now - last < 600) return
      last = now
      void act('performanceMark', { action })
      clearTimeout(idleTimer)
      idleTimer = window.setTimeout(() => void act('performanceMark', { action: 'Idle' }), 2200)
    }
    const scroll = () => mark('Scroll')
    const hover = (event: Event) => { if ((event.target as HTMLElement).closest('.app-icon')) mark('Hover') }
    document.addEventListener('scroll', scroll, true)
    document.addEventListener('mouseover', hover, true)
    return () => { clearTimeout(idleTimer); document.removeEventListener('scroll', scroll, true); document.removeEventListener('mouseover', hover, true) }
  }, [state.measurePerformance, act])

  const renderedRevision = useRef(0)
  useEffect(() => {
    if (!p.hasConfirmedInventory || !p.inventoryRevision || renderedRevision.current === p.inventoryRevision) return
    renderedRevision.current = p.inventoryRevision
    const started = performance.now()
    requestAnimationFrame(() => requestAnimationFrame(() => {
      void act('inventoryRendered', { revision: p.inventoryRevision, durationMs: Math.round(performance.now() - started) })
    }))
  }, [p.hasConfirmedInventory, p.inventoryRevision, act])

  const run = (opName: HostOperation, payload: Record<string, unknown> = {}) => act(opName, payload)
  const navigate = (target: string) => { setQueueReview(null); void run('navigate', { target }) }
  const extensionGroups = browserGroups((state.extensionsPage?.items || []) as ExtensionItem[], state.extensionsPage?.browsers || [])
  const activeExtensionBrowser = resolveExtensionBrowser(extensionGroups, extensionBrowser)
  const selectableProgramCount = (p.items || []).filter(app => app.canUninstall !== false).length
  const selectVisible = selectableProgramCount > 0 && p.selected === selectableProgramCount
  const isWindowsCatalog = ['Store', 'WindowsApps', 'ThirdPartyApps'].includes(p.category)
  const destinations = [
    { target: 'AllApps', label: lang.programs, icon: <Package />, active: state.page === 'apps' && !isWindowsCatalog },
    { target: 'StoreApps', label: lang.windowsApps, icon: <WindowsStoreIcon />, active: state.page === 'apps' && isWindowsCatalog },
    { target: 'BrowserExtensions', label: lang.browserExtensions, icon: <Puzzle />, active: state.page === 'extensions' },
    { target: 'StartupApps', label: lang.startup.title, icon: <Power />, active: state.page === 'startup' },
    { target: 'InstallMonitor', label: installationWords[state.language].title, icon: <FileText />, active: state.page === 'install-monitor' },
  ]

  return (
    <div className={`app-shell ${rtl ? 'rtl' : ''}`} dir={rtl ? 'rtl' : 'ltr'} data-page={state.page}>
      <aside className="sidebar workspace-sidebar">
        <div className="brand">
          <div className="brand-mark"><img src={productIcon} alt="" /></div>
          <div><strong>{lang.brand}</strong><span className="brand-edition"><bdi dir="ltr">{__APP_VERSION__}</bdi></span></div>
        </div>
        <nav className="sidebar-scroll workspace-nav" aria-label={rtl ? 'التنقل الرئيسي' : 'Main navigation'}>
          {destinations.map(destination => <button key={destination.target} type="button"
            className={`workspace-nav-button ${destination.active ? 'active' : ''}`}
            aria-current={destination.active ? 'page' : undefined} title={destination.label}
            onClick={() => navigate(destination.target)}>
            {destination.icon}<span>{destination.label}</span>
          </button>)}
        </nav>
        <div className="sidebar-bottom workspace-nav">
          <button className={`workspace-nav-button ${state.page === 'history' ? 'active' : ''}`} aria-current={state.page === 'history' ? 'page' : undefined}
            title={lang.uninstallHistory} onClick={() => navigate('History')}>
            <History /><span>{lang.uninstallHistory}</span>
          </button>
          <button className="workspace-nav-button" title={lang.settings} onClick={() => void run('openSettings')}>
            <Settings /><span>{lang.settings}</span>
          </button>
        </div>
      </aside>

      {/* MAIN VIEWPORT */}
      <main className="main-panel" ref={mainPanelRef}>
        {state.page === 'apps' && (
          <>
            <header className="page-header workspace-page-header">
              <div className="workspace-heading">
                <h1>{isWindowsCatalog ? lang.windowsApps : lang.programs}</h1>
                {p.hasConfirmedInventory && <span className="workspace-count"><bdi dir="ltr">{p.filtered}</bdi> {lang.totalShown}</span>}
              </div>
              {p.hasPendingQueueReview ? <button className="button quiet" data-testid="open-queue-review" onClick={() => void run('openQueueReview')}><CircleAlert size={15} />{lang.pendingReview}</button>
                : isWindowsCatalog ? <button className="button quiet" onClick={() => void run('openWindowsAppsSettings')}><ExternalLink size={15} />{lang.windowsAppsSettings}</button>
                : <button className="button quiet" disabled={p.isLoading} onClick={() => void run('reviewProgramFile')}><FolderOpen size={15} />{lang.reviewProgramFile}</button>}
            </header>

            {isWindowsCatalog ? <WindowsAppsPage items={p.items || []} hasInventory={Boolean(p.hasConfirmedInventory)}
              category={p.category} navigate={navigate} loading={Boolean(p.isLoading)} lang={lang} search={search}
              setSearch={value => { pendingSearch.current = value; setSearch(value) }} run={run} onReview={setQueueReview} /> : <section className="apps-surface programs-workspace">
              <nav className="catalog-tabs" aria-label={lang.programs}>
                {([
                  ['AllApps', 'All', lang.allPrograms, p.total], ['RecentApps', 'Recent', lang.recentlyInstalled, p.recent], ['LargeApps', 'Large', lang.largePrograms, p.large],
                ] as const).map(([target, category, title, count]) => <button key={target}
                  className={`catalog-tab ${p.category === category ? 'active' : ''}`} aria-current={p.category === category ? 'page' : undefined}
                  onClick={() => navigate(target)}>{title}<bdi dir="ltr">{p.hasConfirmedInventory ? count : '—'}</bdi></button>)}
              </nav>
              <div className="toolbar catalog-toolbar">
                <label className="searchbox">
                  <Search size={16} />
                  <input type="search" aria-label={lang.search} value={search} onChange={e => { pendingSearch.current = e.target.value; setSearch(e.target.value) }} placeholder={lang.search} />
                </label>
                <div className="toolbar-controls">
                  <SortButton mode="NameAsc" current={p.sort} label={labels.sortNameLabel || (rtl ? 'الاسم' : 'Name')} icon={<ArrowDownAZ size={14} />} act={act} />
                  <SortButton mode="SizeDesc" current={p.sort} label={labels.sortSizeLabel || lang.size} icon={<ArrowDownWideNarrow size={14} />} act={act} />
                  <SortButton mode="DateDesc" current={p.sort} label={labels.sortDateLabel || lang.installDate} icon={<ArrowUpDown size={14} />} act={act} />
                </div>
                <button className="button quiet refresh-catalog" disabled={p.isLoading} onClick={() => void run('refresh')} title={lang.refresh}>
                  <RotateCcw size={15} className={p.isLoading ? 'spin' : ''} />{lang.refresh}
                </button>
              </div>
              <div className="table-head">
                <input type="checkbox" aria-label={lang.selectVisible} title={lang.selectVisible}
                  checked={selectVisible} disabled={!selectableProgramCount}
                  ref={input => { if (input) input.indeterminate = p.selected > 0 && !selectVisible }}
                  onChange={e => void run('selectAll', { selected: e.target.checked })} />
                <div className="head-app-name">{labels.applicationColumnLabel || lang.application}</div>
                <div>{labels.sizeColumnLabel || lang.size}</div>
                <div>{labels.installedColumnLabel || lang.installDate}</div>
                <div>{labels.versionColumnLabel || lang.version}</div>
                <div className="align-center">{lang.actions}</div>
              </div>

              {p.isLoading && p.hasConfirmedInventory && (
                <div className="inventory-refresh" role="status"><LoaderCircle size={16} className="spin" />{lang.refreshing}<span>{lang.stages[p.inventoryStage as keyof typeof lang.stages] || p.inventoryStage}</span></div>
              )}
              {['Failed', 'Partial'].includes(p.inventoryState || '') && (
                <div className="inventory-error" role="alert"><CircleAlert size={18} /><div><strong>{p.inventoryState === 'Partial' ? lang.inventoryPartial : lang.inventoryFailed}</strong><p>{p.inventoryError}</p></div><button className="button quiet" onClick={() => void run('refresh')}>{lang.retry}</button></div>
              )}
              {p.inventoryState === 'Failed' && !p.hasConfirmedInventory ? null : !p.hasConfirmedInventory ? (
                <InventoryLoading stage={p.inventoryStage || 'Loading'} lang={lang} />
              ) : (p.items || []).length === 0 ? (
                <div className="empty-state">
                  <div className="empty-icon"><Search /></div>
                  <strong>{lang.emptyApps}</strong>
                </div>
              ) : (
                <VirtualList items={p.items as AppItem[]} rowHeight={60} render={(app: AppItem) => (
                  <AppRow app={app} lang={lang} run={run} sharedDetailsId={sharedDetailsId} />
                )} />
              )}
              {p.selected > 0 && <SelectionBar count={p.selected} lang={lang} clear={() => void run('selectAll', { selected: false })}
                review={() => setQueueReview(selectedItems)} />}
            </section>}
          </>
        )}

        {state.page === 'extensions' && (
          <ExtensionsPage page={state.extensionsPage} lang={lang} run={run} selectedBrowser={activeExtensionBrowser} onSelectBrowser={setExtensionBrowser} />
        )}

        {state.page === 'history' && (
          <HistoryPage page={hp} lang={lang} run={run} onClear={() => void run('clearHistory')} />
        )}
        {state.page === 'startup' && <StartupPage page={state.startupPage} lang={lang} run={run} />}
        {state.page === 'install-monitor' && <InstallationPage page={state.installationPage} language={state.language} run={run} />}
      </main>

      {/* MODALS */}
      {p.unregistered?.open && <UnregisteredDialog review={p.unregistered} lang={lang} run={run} />}
      {p.details && <DetailsDialog app={p.details} labels={labels} lang={lang} run={run} close={() => void run('closeDetails')} sharedDetailsId={sharedDetailsId} />}

      {p.confirm && (
        <UninstallConfirmation key={p.confirm.id} items={[p.confirm]} lang={lang} protectDefault={state.queueProtectionSettings?.enabled} close={() => void run('cancelConfirm')}
          execute={options => run('confirmUninstall', { appId: p.confirm?.id, ...options })} />
      )}

      {queueReview && (
        <UninstallConfirmation items={queueReview} lang={lang} protectDefault={state.queueProtectionSettings?.enabled} close={() => setQueueReview(null)}
          execute={async options => {
            const accepted = await run('runBatch', { appIds: queueReview.map(item => item.id), ...options })
            if (accepted) setQueueReview(null)
            return accepted
          }} />
      )}

      {op.open && !residual.open && <ProgressDialog op={{ ...op, residual }} lang={lang} run={run} />}

      {residual.open && <ResidualDialog key={residual.reviewId} residual={residual} lang={lang} run={run} />}

      {state.settingsOpen && <SettingsDialog state={state} lang={lang} run={run} />}

      {host.toast && (
        <div role="status" className={`toast ${host.toastIsError ? 'error' : 'success'}`}>
          <span>{host.toastIsError ? <CircleAlert size={17} /> : <BadgeCheck size={17} />}</span>
          <p>{localizeText(host.toast, state.language)}</p>
          <button onClick={() => { setToast('') }} aria-label={lang.close}><X size={16} /></button>
        </div>
      )}
    </div>
  )
}

function WindowsStoreIcon() {
  return <svg className="nav-icon nav-icon-windows" width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
    <path d="M8 7V5a4 4 0 0 1 8 0v2" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
    <path d="M4 6.5h16l1 13.2a2 2 0 0 1-2 2.3H5a2 2 0 0 1-2-2.3L4 6.5Z" fill="#edf3f8" stroke="#c2d1df" strokeWidth=".7" strokeLinejoin="round" />
    <path d="M7.5 10.5h4v4h-4Z" fill="#f25022" />
    <path d="M12.5 10.5h4v4h-4Z" fill="#7fba00" />
    <path d="M7.5 15.5h4v4h-4Z" fill="#00a4ef" />
    <path d="M12.5 15.5h4v4h-4Z" fill="#ffb900" />
  </svg>
}

function SortButton({ mode, current, label, icon, act }: { mode: string; current: string; label: string; icon: ReactNode; act: HostAction }) {
  const selected = String(current || '')
  const active = selected === mode || (mode === 'NameAsc' && selected === 'NameDesc') || (mode === 'SizeDesc' && selected.startsWith('Size'))
  const next = mode === 'NameAsc' && selected === 'NameAsc' ? 'NameDesc' : mode === 'SizeDesc' && selected === 'SizeDesc' ? 'SizeAsc' : mode
  return (
    <button className={`sort-button ${active ? 'active' : ''}`} onClick={() => void act('sort', { value: next })}>
      {icon}<span>{label}</span>
    </button>
  )
}

function AppRow({ app, lang, run, sharedDetailsId = '' }: { app: AppItem; lang: LanguagePack; run: HostAction; sharedDetailsId?: string }) {
  const blocked = app.canUninstall === false
  return (
    <div className={`app-row program-row ${app.selected ? 'is-selected' : ''}`}>
      <input type="checkbox" aria-label={`${lang.selectAll}: ${app.name}`} checked={app.selected} disabled={blocked} onChange={e => void run('selectApp', { appId: app.id, selected: e.target.checked })} />
      <div className="app-name-cell">
        <AppIcon app={app} selected={app.selected} variant="list" sharedTransition={sharedDetailsId === app.id} />
        <div className="app-name-meta">
          <strong title={app.name}><bdi dir="auto">{app.name}</bdi></strong>
          <span className="publisher-chip" title={blocked ? lang.removalBlockedReason : undefined}><bdi dir="auto">{app.publisher || localizeText('Unknown', lang === words.ar ? 'ar' : 'en')}</bdi>{blocked && <> · {lang.removalBlocked}</>}</span>
        </div>
      </div>
      <div className="cell-dim"><bdi dir="auto">{app.size}</bdi></div>
      <div className="cell-dim"><bdi dir="auto">{app.installDate}</bdi></div>
      <div className="cell-dim" title={app.version || ''}><bdi dir="ltr">{app.version || '—'}</bdi></div>
      <div className="row-actions align-center">
        {blocked ? <button className="icon-action-button" title={lang.windowsAppsSettings} aria-label={`${lang.windowsAppsSettings}: ${app.name}`} onClick={() => void run('openWindowsAppsSettings')}>
          <ExternalLink size={16} />
        </button> : <button className="icon-action-button trash" title={lang.uninstall} aria-label={`${lang.uninstall}: ${app.name}`} onClick={() => void run('promptUninstall', { appId: app.id })}>
          <Trash2 size={16} />
        </button>}
        <button className="icon-action-button" title={lang.details} aria-label={`${lang.details}: ${app.name}`} onClick={() => void run('openDetails', { appId: app.id })}>
          <Info size={16} />
        </button>
      </div>
    </div>
  )
}

function SkeletonRows() {
  return (
    <div className="skeleton-list">
      {Array.from({ length: 8 }, (_, i) => (
        <div className="skeleton-row" key={i}><i /><span /><span /><span /></div>
      ))}
    </div>
  )
}

// ---------------- BROWSER EXTENSIONS VIEW ----------------
function SelectionBar({ count, lang, label = lang.selectedCount, clear, review, action }: {
  count: number; lang: LanguagePack; label?: string; clear: () => void; review?: () => void; action?: ReactNode
}) {
  return <footer className="workspace-selection">
    <span role="status">{label} <bdi dir="ltr">{count}</bdi></span>
    <button className="button quiet" onClick={clear}>{lang.residualDeselectAll}</button>
    {review && <button className="button primary" onClick={review}><Trash2 size={15} />{lang.uninstall} <bdi dir="ltr">({count})</bdi></button>}
    {action}
  </footer>
}

function WindowsAppsPage({ items, hasInventory, category, navigate, loading, lang, search, setSearch, run, onReview }: {
  items: AppItem[]; hasInventory: boolean; category: string; navigate: (target: string) => void; loading: boolean
  lang: LanguagePack; search: string; setSearch: (value: string) => void; run: HostAction; onReview: (items: AppItem[]) => void
}) {
  const selectable = items.filter(app => app.canUninstall !== false)
  const selected = selectable.filter(app => app.selected)
  const allSelected = selectable.length > 0 && selected.length === selectable.length
  const groups = [
    { id: 'built-in', title: lang.builtinWindowsApps, items: items.filter(app => app.isBuiltIn) },
    { id: 'third-party', title: lang.thirdPartyApps, items: items.filter(app => !app.isBuiltIn) },
  ].filter(group => group.items.length)
  return <section className="windows-apps-surface">
    <nav className="catalog-tabs" aria-label={lang.windowsApps}>
      {([['StoreApps', 'Store', lang.allWindowsApps], ['WindowsApps', 'WindowsApps', lang.builtinWindowsApps], ['ThirdPartyApps', 'ThirdPartyApps', lang.thirdPartyApps]] as const).map(([target, key, label]) =>
        <button key={target} className={`catalog-tab ${category === key ? 'active' : ''}`} aria-current={category === key ? 'page' : undefined}
          onClick={() => navigate(target)}>{label}</button>)}
    </nav>
    <div className="toolbar catalog-toolbar">
      <label className="searchbox"><Search size={16}/><input type="search" aria-label={lang.search} value={search} onChange={event => setSearch(event.target.value)} placeholder={lang.search}/></label>
      <button className="button quiet" disabled={loading} onClick={() => void run('refresh')}><RotateCcw size={15} className={loading ? 'spin' : ''}/>{lang.refresh}</button>
    </div>
    {selectable.length > 0 && <label className="windows-select-visible">
      <input type="checkbox" checked={allSelected}
        ref={input => { if (input) input.indeterminate = selected.length > 0 && !allSelected }}
        onChange={event => void run('selectAll', { selected: event.target.checked })}/>{lang.selectVisible}
    </label>}
    <div className="windows-app-groups">
      {!groups.length && <div className="empty-state">{hasInventory ? lang.emptyApps : lang.inventoryTitle}</div>}
      {groups.map(group => <details key={group.id} open><summary><span>{group.title}</span><bdi dir="ltr">{group.items.length}</bdi></summary>
        {group.items.map(app => {
          const removable = app.canUninstall !== false
          return <div className={`windows-app-row ${!removable ? 'removal-blocked' : ''}`} key={app.id}>
            <input type="checkbox" aria-label={`${lang.selectAll}: ${app.name}`} checked={app.selected} disabled={!removable} onChange={event => void run('selectApp', { appId: app.id, selected: event.target.checked })}/>
            <AppIcon app={app} variant="small" fallbackLabel={lang.iconUnavailable}/><strong><bdi dir="auto">{app.name}</bdi></strong>
            <span className="windows-app-state" title={!removable ? lang.removalBlockedReason : undefined}>{removable ? lang.installedState : lang.removalBlocked}</span>
          </div>
        })}
      </details>)}
    </div>
    {selected.length > 0 && <SelectionBar count={selected.length} lang={lang} clear={() => void run('selectAll', { selected: false })}
      review={() => onReview(selected)} />}
  </section>
}

function ExtensionsPage({ page, lang, run, selectedBrowser, onSelectBrowser }: {
  page: State['extensionsPage']; lang: LanguagePack; run: HostAction
  selectedBrowser: string; onSelectBrowser: (browser: string) => void
}) {
  const [profile, setProfile] = useState('All')
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState('All')
  const [sort, setSort] = useState('Name')
  const [details, setDetails] = useState<ExtensionItem | null>(null)
  const [selection, setSelection] = useState<Set<string>>(() => new Set())
  const [opening, setOpening] = useState(false)
  const [detailsRef, onDetailsKeyDown] = useFocusTrap<HTMLElement>(Boolean(details))
  const rtl = lang === words.ar
  const items: ExtensionItem[] = page.items || []
  const groups = browserGroups(items, page.browsers || [])
  const activeBrowser = resolveExtensionBrowser(groups, selectedBrowser)
  const browserItems = items.filter(e => e.browser === activeBrowser)
  const profiles = Array.from(new Map(browserItems.map(e => [`${e.browser}\0${e.profile}`, e])).entries())
  const policyManagedCount = browserItems.filter(e => e.isPolicyManaged).length
  const canFilterManaged = policyManagedCount > 0
  const activeProfile = profiles.some(([key]) => key === profile) ? profile : 'All'
  const activeFilter = canFilterManaged ? filter : 'All'
  const searchValue = query.trim().toLocaleLowerCase()
  const filtered = browserItems.filter(e => {
    if (activeProfile !== 'All' && `${e.browser}\0${e.profile}` !== activeProfile) return false
    if (activeFilter === 'Managed' && !e.isPolicyManaged) return false
    const searchable = [e.name, e.desc, e.browser, e.profile, e.id, e.version,
      ...(e.permissions || []), ...(e.optionalPermissions || []), ...(e.hostPermissions || []), ...(e.contentScriptHosts || [])].join(' ').toLocaleLowerCase()
    return !searchValue || searchable.includes(searchValue)
  }).sort((a, b) => sort === 'Version' ? a.version.localeCompare(b.version, undefined, { numeric: true }) || a.name.localeCompare(b.name)
      : a.name.localeCompare(b.name) || a.browser.localeCompare(b.browser) || a.profile.localeCompare(b.profile))
  const listLabel = activeBrowser || lang.browserExtensions
  const countLabel = (count: number) => page.loading ? lang.scanning : page.error ? lang.scanError : page.scanned ? String(count) : lang.notScanned
  const manageable = (item: ExtensionItem) => item.managementCapability === 'Browser' && !item.isPolicyManaged
  const selectable = page.scanned && !page.loading && !page.error ? filtered.filter(manageable) : []
  const selected = selectable.filter(item => selection.has(extensionIdentity(item)))
  const selectedCount = selected.length
  const allSelected = selectable.length > 0 && selectedCount === selectable.length
  const clearSelection = () => setSelection(new Set())
  const openManager = async (targets: ExtensionItem[]) => {
    if (opening || targets.length === 0) return
    setOpening(true)
    try {
      const profiles = new Map<string, ExtensionItem[]>()
      for (const item of targets) {
        const group = profiles.get(item.profile)
        if (group) group.push(item)
        else profiles.set(item.profile, [item])
      }
      for (const [profileName, extensions] of profiles) {
        const ok = await run('openExtensionManager', { browser: extensions[0].browser, profile: profileName, ids: extensions.map(item => item.id) })
        if (!ok) break
      }
    } finally { setOpening(false) }
  }
  const selectBrowser = (browser: string) => {
    if (browser === activeBrowser) return
    clearSelection()
    setProfile('All')
    setFilter('All')
    onSelectBrowser(browser)
  }
  const selectExtension = (id: string, checked: boolean) => {
    setSelection(previous => {
      const next = new Set(previous)
      if (checked) next.add(id)
      else next.delete(id)
      return next
    })
  }
  const permissionLabels: Record<string, string> = rtl ? {
    activeTab: 'علامة التبويب النشطة', tabs: 'علامات التبويب', storage: 'التخزين', scripting: 'تشغيل السكربتات',
    webNavigation: 'تصفح الويب', downloads: 'التنزيلات', history: 'سجل التصفح', bookmarks: 'الإشارات المرجعية',
    cookies: 'ملفات تعريف الارتباط', clipboardRead: 'قراءة الحافظة', clipboardWrite: 'الكتابة إلى الحافظة',
    management: 'إدارة الإضافات', nativeMessaging: 'التواصل مع تطبيقات خارجية', webRequest: 'طلبات الشبكة',
    debugger: 'تصحيح المتصفح', geolocation: 'الموقع الجغرافي', notifications: 'الإشعارات', identity: 'هوية الحساب',
    contentSettings: 'إعدادات المحتوى', proxy: 'إعدادات الوكيل', downloadsOpen: 'فتح التنزيلات', unlimitedStorage: 'تخزين واسع'
  } : {
    activeTab: 'Active tab', tabs: 'Tabs', storage: 'Storage', scripting: 'Script execution',
    webNavigation: 'Web navigation', downloads: 'Downloads', history: 'Browsing history', bookmarks: 'Bookmarks',
    cookies: 'Cookies', clipboardRead: 'Read clipboard', clipboardWrite: 'Write clipboard',
    management: 'Extension management', nativeMessaging: 'Native messaging', webRequest: 'Network requests',
    debugger: 'Browser debugging', geolocation: 'Geolocation', notifications: 'Notifications', identity: 'Account identity',
    contentSettings: 'Content settings', proxy: 'Proxy settings', 'downloads.open': 'Open downloads', unlimitedStorage: 'Extended storage'
  }
  const permissionLabel = (value: string) => permissionLabels[value] || value
  const hostsFor = (extension: ExtensionItem) => [...new Set([...(extension.hostPermissions || []), ...(extension.contentScriptHosts || [])])]
  const isBroadHost = (host: string) => host === '<all_urls>' || host === '*://*/*' || host === 'http://*/*' || host === 'https://*/*'
  const extensionStateLabel = (extension: ExtensionItem) => extension.enabledState === 'Enabled' ? lang.extensionEnabled : extension.enabledState === 'Disabled' ? lang.extensionDisabled : lang.extensionUnknown
  const extensionStateClass = (extension: ExtensionItem) => extension.enabledState === 'Enabled' ? 'enabled' : extension.enabledState === 'Disabled' ? 'disabled' : 'installed'
  const row = (ext: ExtensionItem) => {
    const id = extensionIdentity(ext)
    const checked = manageable(ext) && selection.has(id)
    return <div className={`app-row ext-row${checked ? ' is-selected' : ''}`} key={id}>
      <input type="checkbox" checked={checked} disabled={!manageable(ext) || opening} aria-label={`${lang.selectItem}: ${ext.name} · ${ext.profile}`}
        onChange={event => selectExtension(id, event.currentTarget.checked)} />
      <div className="app-name-cell">
        <button className="extension-name-open" aria-label={`${lang.extensionDetails}: ${ext.name}`} onClick={() => setDetails(ext)}>
          <AppIcon app={{ name: ext.name, icon: ext.icon }} variant="list" />
          <span className="app-name-meta">
            <strong><bdi dir="auto">{ext.name}</bdi></strong>
            <span className="publisher-chip"><bdi dir="ltr">{ext.id}</bdi> · <bdi dir="auto">{ext.profile}</bdi></span>
            <span className="extension-permission-badges">{(ext.permissions || []).slice(0, 2).map(permission => <span className="extension-permission-badge" key={permission}>{permissionLabel(permission)}</span>)}{hostsFor(ext).some(isBroadHost) && <span className="extension-permission-badge broad">{lang.extensionBroadHosts}</span>}</span>
          </span>
        </button>
      </div>
      <div className="cell-dim browser-cell"><BrowserIcon browser={ext.browser} /><div><bdi dir="ltr">{ext.browser}</bdi><small><bdi dir="auto">{ext.profile}</bdi></small></div></div>
      <div className="extension-status-cell"><span className={`extension-status-badge ${extensionStateClass(ext)}`}>{extensionStateLabel(ext)}</span><small><bdi dir="ltr">{ext.version || '—'}</bdi></small>{ext.isPolicyManaged && <small className="extension-policy-label">{lang.extensionManaged}</small>}
        {manageable(ext) && <button className="extension-open-manager" type="button" disabled={opening} title={lang.extensionManageInBrowser}
          aria-label={`${lang.extensionManageInBrowser}: ${ext.name}`} onClick={() => void openManager([ext])}><ExternalLink size={14} /></button>}</div>
    </div>
  }

  return (
    <div className="extensions-page">
      <header className="page-header">
        <div>
          <h1>{lang.browserExtensions} <span className="title-count">({page.error ? lang.scanError : inventoryCount({ ...page, items: browserItems }, lang.notScanned, lang.scanning)})</span></h1>
          <p className="subtitle">{lang.extensionsIntro}</p>
        </div>
        <button className="button quiet" disabled={page.loading} onClick={() => { clearSelection(); void run('scanExtensions') }}>
          {page.loading ? <LoaderCircle size={15} className="spin" /> : <RotateCcw size={15} />}
          {lang.refresh}
        </button>
      </header>

      {page.incomplete && !page.error && <div className="extension-scan-warning" role="status"><CircleAlert size={17} /><span>{lang.extensionScanPartial}</span></div>}
      {groups.length > 0 && <div className="sub-tabs extension-tabs" role="tablist" aria-label={lang.browserExtensions}>
        {groups.map(group => ({ browser: group.browser, count: group.items.length })).map(tab => (
          <button key={tab.browser} role="tab" aria-selected={activeBrowser === tab.browser}
            className={`sub-tab ${activeBrowser === tab.browser ? 'active' : ''}`} onClick={() => selectBrowser(tab.browser)}>
            <BrowserIcon browser={tab.browser} />
            <span><bdi dir="ltr">{tab.browser}</bdi></span>
            <span className="sub-tab-badge">{countLabel(tab.count)}</span>
          </button>
        ))}
      </div>}

      {page.scanned && !page.error && activeBrowser && <div className="extension-toolbar">
        <label className="extension-search"><Search size={15}/><input type="search" aria-label={lang.extensionSearch} value={query} onChange={event => { clearSelection(); setQuery(event.target.value) }} placeholder={lang.extensionSearch}/></label>
        <label className="extension-select"><span>{lang.profiles}</span><select value={activeProfile} onChange={event => { clearSelection(); setProfile(event.target.value) }}><option value="All">{lang.allProfiles}</option>{profiles.map(([key, item]) => <option key={key} value={key}>{item.profile}</option>)}</select></label>
        <label className="extension-select"><span>{lang.extensionSort}</span><select value={sort} onChange={event => setSort(event.target.value)}><option value="Name">{lang.extensionSortName}</option><option value="Version">{lang.extensionSortVersion}</option></select></label>
        <div className="extension-filter-bar"><button className={activeFilter === 'All' ? 'selected' : ''} onClick={() => { if (activeFilter !== 'All') clearSelection(); setFilter('All') }}>{lang.extensionFilterAll}</button>{canFilterManaged && <button className={activeFilter === 'Managed' ? 'selected' : ''} onClick={() => { if (activeFilter !== 'Managed') clearSelection(); setFilter('Managed') }}>{lang.extensionFilterManaged} <bdi dir="ltr">{policyManagedCount}</bdi></button>}</div>
      </div>}

      <section className="apps-surface extensions-surface" key={activeBrowser}>
          <div className="table-head ext-table-head">
          <input type="checkbox" checked={allSelected} disabled={selectable.length === 0 || opening} aria-label={lang.selectAllExtensions} title={lang.selectAllExtensions}
            ref={input => { if (input) input.indeterminate = selectedCount > 0 && !allSelected }}
            onChange={event => setSelection(new Set(event.currentTarget.checked ? selectable.map(extensionIdentity) : []))} />
          <div className="head-app-name">{listLabel} ({countLabel(filtered.length)})</div><div>{lang.browser} / {lang.profiles}</div><div>{lang.extensionStatus} / {lang.version}</div>
        </div>

        {page.loading ? (
          <div className="extension-skeleton" aria-label={lang.scanning}><SkeletonRows /></div>
        ) : page.error ? (
          <div className="empty-state extension-error"><CircleAlert size={34} /><strong>{lang.scanError}</strong><p>{page.error}</p><button className="button quiet" onClick={() => void run('scanExtensions')}>{lang.retryScan}</button></div>
        ) : filtered.length === 0 ? (
          <div className="empty-state">
            <Puzzle size={36} className="text-muted" />
            <strong>{page.scanned ? groups.length === 0 ? lang.extensionNoBrowsersFound : (query || activeProfile !== 'All' || activeFilter !== 'All') ? lang.extensionSearchEmpty : lang.noExtensionsView : lang.notScanned}</strong>
          </div>
        ) : (
          <div className="ext-list">
            {filtered.map(row)}
          </div>
        )}
        {selectedCount > 0 && <SelectionBar count={selectedCount} label={lang.selectedExtensions} lang={lang} clear={clearSelection}
          action={<button className="button primary" disabled={opening} onClick={() => void openManager(selected)}><ExternalLink size={15} />{lang.extensionManageInBrowser}</button>} />}
      </section>
      {details && <div className="extension-detail-backdrop" onClick={() => setDetails(null)}><aside ref={detailsRef} onKeyDown={onDetailsKeyDown} tabIndex={-1} className="extension-detail-drawer" role="dialog" aria-modal="true" aria-label={lang.extensionDetails} onClick={event => event.stopPropagation()}>
        <div className="extension-detail-head"><div className="extension-detail-title"><AppIcon app={{name: details.name, icon: details.icon}} variant="list"/><div><h2><bdi dir="auto">{details.name}</bdi></h2><span><BrowserIcon browser={details.browser}/><bdi dir="ltr">{details.browser}</bdi> · <bdi dir="auto">{details.profile}</bdi></span></div></div><button className="button quiet icon-button" aria-label={lang.close} onClick={() => setDetails(null)}><X size={16}/></button></div>
        <div className="extension-detail-scroll extension-detail-compact">
          {manageable(details) && <section className="extension-detail-section"><h3>{lang.extensionManagement}</h3>
            <p>{lang.extensionBrowserManagementNote}</p><button className="button primary" disabled={opening || page.loading} onClick={() => void openManager([details])}><ExternalLink size={15} />{lang.extensionManageInBrowser}</button>
          </section>}
          <section className="extension-detail-section"><h3>{lang.extensionOverview}</h3><div className="extension-summary-grid"><div><span>{lang.version}</span><bdi dir="ltr">{details.version || '—'}</bdi></div><div><span>{lang.extensionStatus}</span><strong className={`extension-status-badge ${extensionStateClass(details)}`}>{extensionStateLabel(details)}</strong></div>{details.isPolicyManaged && <div><span>{lang.extensionManaged}</span><strong>{lang.extensionManaged}</strong></div>}</div>{details.desc && <p className="extension-description">{details.desc}</p>}</section>
          <section className="extension-detail-section"><h3>{lang.extensionPermissions}</h3>{extensionPermissionImpact(details).broadSiteAccess && <p className="extension-impact-note"><Info size={14}/>{lang.extensionBroadHosts}</p>}<PermissionSection title={lang.extensionSensitivePermissions} values={extensionPermissionImpact(details).sensitivePermissions} label={permissionLabel}/><details className="extension-advanced-details"><summary>{lang.extensionPermissionDetails}</summary><PermissionSection title={lang.extensionApiPermissions} values={details.permissions || []} label={permissionLabel}/><PermissionSection title={lang.extensionOptionalPermissions} values={details.optionalPermissions || []} label={permissionLabel}/><PermissionSection title={lang.extensionHostPermissions} values={hostsFor(details)} label={permissionLabel}/><PermissionSection title={lang.extensionOptionalHostPermissions} values={details.optionalHostPermissions || []} label={permissionLabel}/><PermissionSection title={lang.extensionContentScriptHosts} values={details.contentScriptHosts || []} label={permissionLabel}/><p className="extension-permission-caution">{lang.extensionPermissionCaution}</p></details></section>
          <details className="extension-advanced-details extension-technical-details"><summary>{lang.extensionIdentity}</summary><DetailField label={lang.extensionId || 'Extension ID'} value={details.id} mono/><DetailField label={lang.extensionInstallLocation} value={details.installationLocation} mono/><DetailField label={lang.extensionInstallationSource} value={details.installationSource === 'Policy-managed' ? lang.extensionManaged : lang.extensionNoSourceEvidence}/></details>
        </div>
      </aside></div>}
    </div>
  )
}

function DetailField({label,value,mono=false}:{label:string;value:string;mono?:boolean}) { return <div className={`extension-detail-field ${mono?'mono':''}`}><span>{label}</span><bdi dir={mono?'ltr':'auto'}>{value || '—'}</bdi></div> }
function PermissionSection({title,values,label}:{title:string;values:string[];label:(value:string)=>string}) { if (!values.length) return null; return <div className="extension-permission-section"><h4>{title}</h4><div className="extension-permission-list">{values.map(value=><span className="extension-permission-badge" key={value} title={value}><bdi dir="auto">{label(value)}</bdi></span>)}</div></div> }

// ---------------- APP DETAILS / PROGRESS / RESIDUAL DIALOGS ----------------
function ModalHeader({ icon, title, close, closeLabel }: { icon: ReactNode; title: string; close?: () => void; closeLabel: string }) {
  return (
    <header className="modal-header">
      <div className="modal-title-icon">{icon}</div>
      <div><h2>{title}</h2><span>Uninstaller 77 {__APP_VERSION__}</span></div>
      {close && <button className="icon-button" onClick={close} aria-label={closeLabel}><X size={18} /></button>}
    </header>
  )
}

function DetailsDialog({ app, labels, lang, run, close, sharedDetailsId = '' }: { app: AppItem; labels: Labels; lang: LanguagePack; run: HostAction; close: () => void; sharedDetailsId?: string }) {
  const [dialogRef, onDialogKeyDown] = useFocusTrap<HTMLDivElement>(true)
  return (
    <div className="modal-backdrop">
      <div ref={dialogRef} onKeyDown={onDialogKeyDown} tabIndex={-1} className="modal details-modal" role="dialog" aria-modal="true" aria-label={lang.details}>
        <ModalHeader closeLabel={lang.close} icon={<AppWindow />} title={lang.details} close={close} />
        <div className="details-app">
          <AppIcon app={app} variant="details" sharedTransition={sharedDetailsId === app.id} />
          <div><h3><bdi dir="auto">{app.name}</bdi></h3><span><bdi dir="auto">{app.publisher || localizeText('Unknown', lang === words.ar ? 'ar' : 'en')}</bdi></span></div>
        </div>
        <div className="detail-grid">
          <Detail label={labels.detailsVersionLabel || lang.version} value={app.version || '—'} />
          <Detail label={labels.architectureLabel || 'Architecture'} value={app.architecture} />
          <Detail label={labels.installerEngineLabel || 'Engine'} value={app.installerType} />
          <Detail label={labels.estimatedSizeLabel || lang.size} value={app.size} />
          <Detail label={labels.installDateLabel || lang.installDate} value={app.installDate} />
          <Detail label={lang === words.ar ? 'المصدر' : 'Source'} value={app.source} />
        </div>
        {app.canUninstall === false && <Detail label={lang.removalBlocked} value={lang.removalBlockedReason} />}
        <Detail label={labels.installLocationLabel || 'Install Location'} value={app.installLocation} mono />
        <Detail label={labels.registryKeyLabel || 'Registry Key'} value={app.registryKey} mono />
        <Detail label={labels.uninstallCommandLabel || 'Uninstall Command'} value={app.uninstallCommand} mono />
        <div className="modal-actions">
          {app.canUninstall === false && <button className="button quiet" onClick={() => void run('openWindowsAppsSettings')}><ExternalLink size={15} />{lang.windowsAppsSettings}</button>}
          <button className="button quiet" onClick={close}>{labels.closeLabel || lang.close}</button>
        </div>
      </div>
    </div>
  )
}

function Detail({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className={`detail-field ${mono ? 'mono' : ''}`}>
      <span>{label}</span>
      <strong dir={mono || /^[\d\s.:/−-]+$/.test(value || '') ? 'ltr' : 'auto'} title={value}>{value || '—'}</strong>
    </div>
  )
}

export function ProgressDialog({ op, lang, run }: { op: State['appsPage']['operation']; lang: LanguagePack; run: HostAction }) {
  const [dialogRef, onDialogKeyDown] = useFocusTrap<HTMLDivElement>(true)
  const [stopPending, setStopPending] = useState(false)
  const [reviewRequest, setReviewRequest] = useState<string | null>(null)
  const reviewPhase = `${op.review?.queueId || ''}:${Boolean(op.review?.recovered)}`
  const reviewPending = reviewRequest === reviewPhase
  const [ackConsent, setAckConsent] = useState(false)
  const [carouselOffset, setCarouselOffset] = useState(0)
  const stopping = stopPending || op.stopRequested
  const attention = op.needsReview
  const done = !op.isRunning
  const compact = !done && !attention
  const scanning = op.operationState === 'ScanningResiduals'
  const finishing = op.isRunning && op.state === 'Succeeded' && op.operationState === 'Completed'
  const color = attention || (stopping && !done) ? 'orange' : !done ? 'blue'
    : op.state === 'Succeeded' ? 'green' : ['PartialFailure', 'CompletedWithSkips'].includes(op.state || '') ? 'orange' : op.state === 'CriticalFailure' ? 'red' : 'gray'
  const summary = queueProgress(op)
  const verifiedPercent = verifiedQueuePercent(op)
  const title = scanning ? lang.scanLeftovers : attention ? lang.attention : stopping && !done ? lang.stopRequesting : !done ? lang.running
    : ['PartialFailure', 'CompletedWithSkips'].includes(op.state || '') ? lang.queuePartial : op.state === 'CriticalFailure' ? lang.queueCritical : op.state === 'Stopped' ? lang.queueStopped : lang.complete
  const rows = op.queueItems?.length ? op.queueItems : (op.results || []).map(item => ({
    ...item, state: item.state === 'Succeeded' ? 'Completed' : item.state === 'NeedsAttention' ? 'NeedsUserAttention' : item.state === 'Skipped' ? 'Cancelled' : item.state
  }))
  const scanSummary = op.residual?.summary || []
  const active = attention ? '—' : op.isRunning && !op.preparingProtection && !['Completed', 'Failed', 'Cancelled', 'ScanningResiduals'].includes(op.operationState || '') ? 1 : 0
  const index = rows.findIndex(item => item.app?.id === op.app?.id) + 1
  const actualIndex = index > 0 ? index - 1 : Math.max(0, rows.findIndex(item => item.state === 'Running' || item.state === 'Launching' || item.state === 'Preparing'))
  const carouselIndex = Math.max(0, Math.min(rows.length - 1, actualIndex + carouselOffset))
  const carouselStart = Math.max(0, Math.min(carouselIndex, rows.length - 1) - 1)
  const visibleQueueItems = rows.slice(carouselStart, carouselStart + 3)
  const currentState = done ? rows.find(item => item.app?.id === op.app?.id)?.state || op.operationState : op.operationState
  const stage = scanning ? op.residual?.stage || lang.scanLeftovers : op.preparingProtection ? op.protectionStage : lang.stages[currentState as keyof typeof lang.stages] || op.stage || '—'
  const logRef = useRef<HTMLDivElement>(null)
  const queueRef = useRef<HTMLDivElement>(null)
  const currentRef = useRef<HTMLDivElement>(null)
  useEffect(() => { if (logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight }, [op.logs?.length])
  useEffect(() => {
    if (queueRef.current && currentRef.current)
      queueRef.current.scrollTop = Math.max(0, currentRef.current.offsetTop - queueRef.current.clientHeight / 2 + currentRef.current.clientHeight / 2)
  }, [op.app?.id])
  const requestStop = async () => {
    if (stopping) return
    setStopPending(true)
    if (!await run('stopBatch')) setStopPending(false)
  }
  // The compact progress surface follows the engine's current item. Browsing the
  // full queue remains available in Details without changing execution order.
  if (!attention) {
    const currentItem = rows[actualIndex]
    const start = Math.max(0, Math.min(actualIndex - 1, rows.length - 3))
    const nearby = rows.slice(start, start + 3)
    const localeAr = lang === words.ar
    const outcome = (state: string) => state === 'Completed' ? <CircleCheck size={24} />
      : ['Failed', 'Partial', 'NeedsUserAttention'].includes(state) ? <CircleAlert size={17} /> : <Clock3 size={17} />
    const resultSummary = localeAr
      ? `تمت إزالة ${op.successful || 0} من ${op.total || rows.length} · جزئي ${op.partial || 0} · فشل ${op.failed || 0} · تخطي ${op.skipped || 0}`
      : `Removed ${op.successful || 0} of ${op.total || rows.length} · ${op.partial || 0} partial · ${op.failed || 0} failed · ${op.skipped || 0} skipped`
    const reviewOnly = scanSummary.reduce((count, group) => count + (group.reviewOnlyCount || 0), 0)
    const allRemoved = (op.successful || 0) > 0 && op.successful === (op.total || rows.length)
    const cleanSuccess = allRemoved && !op.residual?.skippedCount && !op.residual?.failedCount &&
      !scanSummary.some(group => group.remainingCount || group.reason || group.coverageIssues?.length)
    const removedMessage = localeAr
      ? (op.successful === 1 ? 'تم إلغاء تثبيت برنامج واحد بنجاح.' : `تم إلغاء تثبيت البرامج بنجاح (${op.successful}).`)
      : `Successfully uninstalled ${op.successful} ${op.successful === 1 ? 'program' : 'programs'}.`
    const successMessage = op.residual?.deletedCount
      ? `${removedMessage} ${localeAr ? 'تم تنظيف المخلفات التالية.' : 'The following leftovers were deleted.'}`
      : removedMessage
    return <div className="modal-backdrop high-layer queue-backdrop" dir={localeAr ? 'rtl' : 'ltr'}>
      <div ref={dialogRef} onKeyDown={onDialogKeyDown} tabIndex={-1} className={`modal uninstall-shell progress-modal simple-progress ${done ? 'is-done' : 'is-running'}`} role="dialog" aria-modal="true" aria-label={done ? lang.results : lang.running}>
        <header className="simple-progress-header workflow-header"><span>Uninstaller 77</span>
          <button className="icon-button" aria-label={done ? lang.close : lang.stopAfterCurrent}
            disabled={!done && (stopping || scanning || finishing)} onClick={() => done ? void run('closeProgress') : void requestStop()}><X size={16} /></button>
        </header>
        <div className="simple-progress-body">
          {op.preview && <p className="simple-progress-preview">{localeAr ? 'معاينة اختبار اصطناعية — لم تُزل أي تطبيقات.' : 'Synthetic UI preview — no applications were uninstalled.'}</p>}
          {done ? <>
            <div className={`simple-result-summary ${cleanSuccess ? '' : 'has-failures'}`} role="status">
              {cleanSuccess ? <CircleCheck /> : <CircleAlert />}
              <h3>{allRemoved ? successMessage : resultSummary}</h3>
            </div>
            {Boolean(op.residual?.deletedCount) && <div className="reference-result-metrics">
              <div><LayoutGrid size={38} /><strong>{op.residual?.deletedRegistryCount || 0}</strong><span>{lang.residualRegistry}</span></div>
              <div><FolderOpen size={38} /><strong dir="ltr">{formatByteSize(op.residual?.deletedFileBytes || 0)}</strong><span>{lang.residualFilesSection}</span></div>
            </div>}
            <div className="simple-result-list" aria-label={lang.results} hidden={cleanSuccess}>
              {rows.map(item => {
                const info = queueFailureInfo(item, localeAr ? 'ar' : 'en')
                const scan = scanSummary.find(entry => entry.id === item.app.id)
                return <div className={`simple-result-row result-${item.state.toLowerCase()}`} key={item.operationId || item.app.id}>
                  <AppIcon app={item.app} variant="small" fallbackLabel={lang.iconUnavailable} />
                  <div>
                    <strong title={item.app.name}><bdi dir="auto">{item.app.name}</bdi></strong>
                    <small>{lang.stages[item.state as keyof typeof lang.stages] || item.state}</small>
                    {(info.reason || info.exitCode != null) && <small className="simple-result-reason" dir="auto">
                      {info.reason || info.category}{info.exitCode != null && <> · {lang.exitCodeLabel} <bdi dir="ltr">{info.exitCode}</bdi></>}
                    </small>}
                    <small className="simple-result-residual">{lang.residuals}: {scan?.status || (localeAr ? 'لم يُجرَ الفحص' : 'Not scanned')}
                      {scan?.cleanedCount ? <> · {lang.residualDeleted} <bdi dir="ltr">{scan.cleanedCount}</bdi></> : null}
                      {scan?.reviewOnlyCount ? <> · {lang.residualReviewOnly} <bdi dir="ltr">{scan.reviewOnlyCount}</bdi></> : null}
                      {scan?.reason && <span dir="auto"> — {scan.reason}</span>}
                    </small>
                  </div>
                  <span className="simple-result-outcome" aria-hidden="true">{outcome(item.state)}</span>
                </div>
              })}
            </div>
            {Boolean(op.residual?.skippedCount || op.residual?.failedCount) && <div className="residual-result-summary" role="status">
              <span>{lang.residualDeleted} <bdi dir="ltr">{op.residual?.deletedCount || 0}</bdi></span>
              <span>{lang.residualSkipped} <bdi dir="ltr">{op.residual?.skippedCount || 0}</bdi></span>
              <span>{lang.residualDeleteFailed} <bdi dir="ltr">{op.residual?.failedCount || 0}</bdi></span>
            </div>}
            {reviewOnly > 0 && <p className="simple-result-review-only">{lang.residualReviewOnly}: <bdi dir="ltr">{reviewOnly}</bdi></p>}
            {op.protectionWarning && <p className="simple-result-review-only" role="status">{op.protectionWarning}</p>}
          </> : <>
            <div className={`simple-progress-cards count-${nearby.length}`} aria-label={lang.queueItems}>
              {nearby.map(item => {
                const isCurrent = item === currentItem && !scanning && !['Completed', 'Failed', 'Partial', 'Cancelled', 'NotInstalled'].includes(item.state)
                const isPrevious = ['Completed', 'Failed', 'Partial', 'Cancelled', 'NotInstalled'].includes(item.state) && !isCurrent
                return <div className={`simple-progress-card ${isCurrent ? 'current' : ''} ${isPrevious ? 'previous' : ''} result-${item.state.toLowerCase()}`} key={item.operationId || item.app.id} aria-current={isCurrent ? 'step' : undefined} title={item.app.name}>
                  {isCurrent && <svg className="workflow-ring" viewBox="0 0 184 184" role="progressbar" aria-label={lang.queueCompletion} aria-valuenow={summary.percent} aria-valuemin={0} aria-valuemax={100}>
                    <circle cx="92" cy="92" r="90" />
                    <circle className="workflow-ring-value" cx="92" cy="92" r="90" pathLength="100" strokeDasharray={`${summary.percent} 100`} />
                  </svg>}
                  <AppIcon app={item.app} variant="list" fallbackLabel={lang.iconUnavailable} />
                  <strong><bdi dir="auto">{item.app.name}</bdi></strong>
                  <small dir="ltr">{item.app.size || '—'}</small>
                  <span className="simple-neighbor-result" aria-label={isPrevious ? lang.stages[item.state as keyof typeof lang.stages] || item.state : undefined}>
                    {isCurrent ? <Trash2 size={22} /> : isPrevious ? outcome(item.state) : null}
                  </span>
                </div>
              })}
            </div>
            <p className="simple-progress-status" role="status">{scanning ? lang.scanLeftovers : stopping ? lang.stopAfter : finishing ? lang.complete : op.preparingProtection ? op.protectionStage
              : op.operationState === 'WaitingForMsiRetry' ? lang.stages.WaitingForMsiRetry
              : <>{op.operationState === 'AwaitingCompletion' ? `${lang.stages.AwaitingCompletion} — ` : lang.uninstalling.split('{name}')[0]}<bdi dir="auto">{op.app?.name || currentItem?.app.name || '—'}</bdi> <bdi dir="ltr">({Math.max(1, index)}/{op.total || rows.length})</bdi></>}</p>
          </>}
        </div>
        <footer className="simple-progress-footer">{done
          ? <>{!cleanSuccess && op.residual?.canReview && <button className="button quiet" onClick={() => void run('openResidualReview')}>{lang.residualTitle}</button>}{!cleanSuccess && <button className="button quiet" onClick={async () => { if (await run('closeProgress')) void run('navigate', { target: 'History' }) }}>{lang.uninstallHistory}</button>}<button className="button primary" onClick={() => void run('closeProgress')}>{cleanSuccess ? (localeAr ? 'موافق' : 'OK') : lang.close}</button></>
          : <button className="button quiet stop-queue" disabled={stopping || scanning || finishing} onClick={() => void requestStop()}>{lang.stopAfterCurrent}</button>}
        </footer>
      </div>
    </div>
  }
  return (
    <div className="modal-backdrop high-layer queue-backdrop" dir={lang === words.ar ? 'rtl' : 'ltr'}>
      <div ref={dialogRef} onKeyDown={onDialogKeyDown} tabIndex={-1} className={'modal uninstall-shell progress-modal phase10-progress status-' + color + ' queue-workspace' + (attention ? ' is-review' : '') + (compact ? ' is-compact is-live' : '')} role="dialog" aria-modal="true" aria-label={title}>
        <header className="queue-header">
          <span className="queue-status-icon" aria-hidden="true">{attention ? <CircleAlert /> : !done ? <LoaderCircle className="spin" /> : op.state === 'Succeeded' ? <BadgeCheck /> : <Info />}</span>
          <div className="queue-heading"><small>{lang.queueSubtitle}</small><h2>{title}</h2></div>
          <div className="queue-elapsed"><Clock3 size={15} aria-hidden="true" /><span>{lang.elapsed}<bdi dir="ltr">{op.elapsed || '00:00:00'}</bdi></span></div>
          {done && !attention && <button className="icon-button" aria-label={lang.close} onClick={() => void run('closeProgress')}><X size={18} /></button>}
        </header>
        {!compact && <UninstallFlowSteps active={scanning ? 2 : done && !attention ? 3 : 1} lang={lang} />}
        <div className="progress-body">
          <div className="queue-overview">
            {op.preview && <div className="queue-attention">{lang === words.ar ? 'معاينة اختبار اصطناعية صريحة — لم تُزل أي تطبيقات.' : 'Explicit synthetic UI preview — no applications were uninstalled.'}</div>}
            {done && !attention ? <section className="uninstall-completion" aria-label={lang.complete}>
              <div className="completion-check"><Check size={34} /></div>
              <h3>{lang.complete}</h3>
              <p>{op.status || lang.results}</p>
              {op.app && <div className="completion-last-app">
                <AppIcon app={op.app} variant="progress" operationState={currentState} fallbackLabel={lang.iconUnavailable} />
                <div><strong><bdi dir="auto">{op.app.name}</bdi></strong><span className="queue-current-stage">{stage}</span></div>
              </div>}
              <div className="completion-progress" role="progressbar" aria-label={lang.queueCompletion} aria-valuemin={0} aria-valuemax={100} aria-valuenow={100}><span /></div>
              <div className="completion-metrics">
                <Metric label={lang.successful} value={op.successful || 0} tone="green" />
                <Metric label={lang.failed} value={op.failed || 0} tone="red" />
                <Metric label={lang.skipped} value={op.skipped || 0} />
              </div>
              <div className="completion-verification"><span>{lang.queueResolved} <bdi dir="ltr">{summary.resolved}</bdi></span><span>{lang.verifiedProgress} <bdi dir="ltr">{op.successful || 0}/{op.total || 0} · {verifiedPercent}%</bdi></span></div>
              {Boolean(op.residual?.deletedCount || op.residual?.keptCount) && <div className="completion-cleanup-summary">
                <Metric label={lang.unregistered.deletedFiles} value={op.residual?.deletedCount || 0} tone="green" />
                <Metric label={lang.size} value={formatByteSize(op.residual?.deletedBytes || 0)} tone="green" />
                <Metric label={lang.unregistered.remainingFiles} value={op.residual?.keptCount || 0} tone={op.residual?.keptCount ? 'orange' : undefined} />
              </div>}
            </section> : <section className="queue-carousel" aria-label={lang.queueItems}>
              <div className="queue-carousel-cards">
                {visibleQueueItems.map((item, offset) => {
                  const isCurrent = item.app?.id === op.app?.id
                  return <button type="button" key={item.operationId || item.app.id} className={`queue-carousel-card ${isCurrent ? 'current' : ''} ${item.state.toLowerCase()}`} aria-current={isCurrent ? 'step' : undefined} onClick={() => setCarouselOffset(carouselStart + offset - actualIndex)}>
                    <span className="queue-carousel-ring"><AppIcon app={item.app} variant="progress" isProcessing={isCurrent && op.isRunning && !attention} operationState={item.state} fallbackLabel={lang.iconUnavailable} /></span>
                    <strong><bdi dir="auto">{item.app.name}</bdi></strong><small dir="ltr">{item.app.size || '—'}</small>
                    <span className="queue-carousel-result">{lang.stages[item.state as keyof typeof lang.stages] || item.state}</span>
                  </button>
                })}
              </div>
              {rows.length > 3 && <div className="queue-carousel-controls">
                <button className="button quiet" disabled={carouselIndex <= 0} onClick={() => setCarouselOffset(carouselIndex - 1 - actualIndex)}>{lang.previous}</button>
                <span><bdi dir="ltr">{carouselIndex + 1}/{rows.length}</bdi></span>
                <button className="button quiet" onClick={() => setCarouselOffset(0)}>{lang.current}</button>
                <button className="button quiet" disabled={carouselIndex >= rows.length - 1} onClick={() => setCarouselOffset(carouselIndex + 1 - actualIndex)}>{lang.next}</button>
              </div>}
              <div className="queue-current-stage queue-carousel-stage" role="status"><span className={'queue-activity ' + (!done && !attention ? 'running' : '')} aria-hidden="true" /><span>{lang.uninstalling.replace('{name}', op.app?.name || '—')} · <bdi dir="ltr">{index}/{op.total || 0}</bdi> — {stage}</span></div>
            </section>}
            {attention && <section className="queue-registration" data-state={op.review?.registrationState || 'NotChecked'} aria-label={lang.currentRegistration} title={lang.registrationReviewHint}>
              <span>{lang.currentRegistration}</span><strong>{op.review?.registrationState === 'Absent' ? lang.registrationAbsent : op.review?.registrationState === 'Present' ? lang.registrationPresent : op.review?.registrationState === 'Unknown' ? lang.registrationUnknown : lang.registrationNotChecked}</strong>
              <small>{lang.registrationReviewHint}</small>
            </section>}
            {attention && done && <div className="completion-verification"><span>{lang.queueResolved} <bdi dir="ltr">{summary.resolved}</bdi></span><span>{lang.verifiedProgress} <bdi dir="ltr">{op.successful || 0}/{op.total || 0} · {verifiedPercent}%</bdi></span></div>}
            {(stopping || attention) && <div className="queue-attention" role="status"><CircleAlert size={18} /><span>{attention ? op.status : lang.stopAfter}</span></div>}
            {op.protectionWarning && <div className="queue-attention" role="alert"><CircleAlert size={18} /><span>{op.protectionWarning}</span></div>}
            {done && !attention && ['RemovalNotObserved', 'RestartPending', 'VendorDependenciesInstalled'].includes(op.failureCode || '') && <div className="queue-attention" role="status"><CircleAlert size={18} /><span>{op.failureCode === 'VendorDependenciesInstalled' ? lang.vendorDependenciesInstalled : op.failureCode === 'RestartPending' ? lang.restartPending : lang.removalNotObserved}</span></div>}
            {(op.errorDetails || attention) && <details className="queue-diagnostics">
              <summary>{lang.queueDiagnostics}<ChevronDown size={14} /></summary>
              <p dir="auto">{op.errorDetails || op.status}</p>
              <div className="queue-diagnostic-codes">
                {op.failureCode && op.failureCode !== 'None' && <code dir="ltr">{op.failureCode}</code>}
                {op.exitCode != null && <span>{lang.exitCodeLabel}: <bdi dir="ltr">{op.exitCode}</bdi></span>}
              </div>
            </details>}
            {op.review?.recovered && <details className="queue-recovery-details"><summary>{lang.pendingReview}<ChevronDown size={14} /></summary><QueueReviewPanel review={op.review} lang={lang} /></details>}
            {scanning ? <div className="queue-scan-status" role="status"><LoaderCircle size={16} className="spin" /><span>{lang.residualLocations} <bdi dir="ltr">{op.residual?.scannedLocations || 0}</bdi></span></div> : !done && <section className="queue-progress-section" aria-label={lang.queueCompletion}>
              <div className="queue-progress-caption"><strong>{lang.queueCompletion}</strong><span>{lang.processed} <bdi dir="ltr">{summary.resolved}/{op.total || 0}</bdi></span><bdi dir="ltr">{summary.percent}%</bdi></div>
              <div role="progressbar" aria-label={lang.queueCompletion} aria-valuemin={0} aria-valuemax={100} aria-valuenow={summary.percent} className={'progress-track determinate ' + (done ? 'finished' : '')}><span style={{ width: summary.percent + '%' }} /></div>
              <div className="queue-progress-meta"><span>{lang.queueResolved} <bdi dir="ltr">{summary.resolved}</bdi></span>{!compact && <span>{lang.verifiedProgress} <bdi dir="ltr">{op.successful || 0}/{op.total || 0} · {verifiedPercent}%</bdi></span>}</div>
            </section>}
            {!done && !compact && <div className="batch-counters overview">
              <Metric label={lang.successful} value={op.successful || 0} tone="green" />
              <Metric label={lang.failed} value={op.failed || 0} tone="red" />
              <Metric label={lang.skipped} value={op.skipped || 0} />
              <Metric label={lang.active} value={active} />
              <Metric label={lang.remaining} value={op.remaining || 0} />
              <Metric label={lang.totalApps} value={op.total || 0} />
            </div>}
          </div>
          <details className="queue-list-section queue-disclosure" open={done && !attention || undefined}>
            <summary className="queue-list-heading"><strong>{done && !attention ? lang.results : lang.queueItems}</strong><ChevronDown size={14} /></summary>
            {(attention || done) && <p className="queue-history-hint">{lang.queueHistoryHint}</p>}
            <div className="queue-results" ref={queueRef}>
              {rows.map(item => <div className={'queue-result result-' + item.state.toLowerCase() + (item.app?.id === op.app?.id ? ' is-current' : '')} key={item.operationId || item.app.id} ref={item.app?.id === op.app?.id ? currentRef : undefined}>
                <AppIcon app={item.app} variant="small" fallbackLabel={lang.iconUnavailable} /><div><strong><bdi dir="auto">{item.app.name}</bdi></strong><small>{lang.stages[item.state as keyof typeof lang.stages] || item.state}</small>{(() => { const info = queueFailureInfo(item, lang === words.ar ? 'ar' : 'en'); return <>{info.reason && <p className="queue-result-reason" dir="auto"><span>{lang.failureReason}: </span>{info.reason}</p>}{(info.category || info.code || info.exitCode != null) && <p className="queue-result-reason queue-result-codes" dir="auto">{info.category && <strong>{info.category}</strong>}{info.code && <code dir="ltr"> {info.code}</code>}{info.exitCode != null && <span> · {lang.exitCodeLabel}: <bdi dir="ltr">{info.exitCode}</bdi></span>}</p>}</> })()}</div>
                <span className="queue-item-state" aria-hidden="true">{item.state === 'Completed' ? <BadgeCheck size={18} /> : ['Failed', 'Partial', 'NeedsUserAttention'].includes(item.state) ? <CircleAlert size={18} /> : ['Idle', 'Cancelled', 'NotInstalled'].includes(item.state) ? <Clock3 size={17} /> : <LoaderCircle size={17} className={!done && item.app?.id === op.app?.id ? 'spin' : ''} />}</span>
              </div>)}
              {!rows.length && <div className="queue-list-empty">{lang.queueEmpty}</div>}
            </div>
          </details>
          {done && !attention && op.residual && op.residual.summary.length > 0 && <ResidualScanSummary residual={op.residual} lang={lang} />}
          <details className="queue-log"><summary>{lang.queueDiagnostics} · {lang.attempts} <bdi dir="ltr">{summary.attempts}</bdi><ChevronDown size={14} /></summary><div className="event-log" ref={logRef}>{(op.logs || []).map((line: string, i: number) => <div key={i}>{line}</div>)}</div></details>
        </div>
        <footer className="queue-footer">
          {op.review?.recovered && op.review?.canAcknowledge && <label className="review-consent"><input type="checkbox" data-testid="review-ack-consent" checked={ackConsent} onChange={e => setAckConsent(e.target.checked)} />{lang.ackConsent}</label>}
          <div className="modal-actions">
            <span className="queue-footer-hint">{!done && !attention && !scanning ? lang.stopAfter : attention ? op.review?.recovered ? lang.noAutoResume : lang.reviewActionHint : ''}</span>
            {op.review?.recovered && op.review?.canAcknowledge && <button className="button quiet" data-testid="queue-acknowledge" disabled={!ackConsent || op.retrying || reviewPending} onClick={async () => { setReviewRequest(reviewPhase); if (!await run('acknowledgeQueueReview', { queueId: op.review?.queueId, acknowledged: true })) setReviewRequest(null) }}>{lang.acknowledgeUnresolved}</button>}
            {attention && <><button className="button quiet" data-testid="queue-retry-verification" disabled={op.retrying || reviewPending} onClick={() => void run('retryQueueVerification')}><RefreshCw size={15} className={op.retrying ? 'spin' : ''} />{lang.retryVerification}</button><button className="button quiet stop-queue" data-testid="queue-review-stop" disabled={reviewPending} onClick={async () => { setReviewRequest(reviewPhase); if (!await run('reviewQueue')) setReviewRequest(null) }}>{reviewPending ? <LoaderCircle size={14} className="spin" /> : <X size={15} />}{reviewPending ? lang.stopRequesting : op.review?.recovered ? lang.keepPending : lang.stopAndReview}</button></>}
            {!done && !attention && !scanning && <button className="button quiet stop-queue" disabled={stopping} onClick={() => void requestStop()}>{stopping ? <LoaderCircle size={15} className="spin" /> : <X size={15} />}{stopping ? lang.stopRequesting : lang.stopAfterCurrent}</button>}
            {!done && op.canCancelCurrent && <button className="button quiet" onClick={() => void run('cancelProgress')}>{lang.cancelCurrent}</button>}
            {done && !attention && <button className="button primary" onClick={() => void run('closeProgress')}><Check size={16} />{lang.close}</button>}
            {done && !attention && op.canOpenSystemRestore && <button className="button quiet" onClick={() => void run('openSystemRestore')}><RotateCcw size={15} />{op.openSystemRestore}</button>}
          </div>
        </footer>
      </div>
    </div>
  )
}
function QueueReviewPanel({ review, lang }: { review: NonNullable<State['appsPage']['operation']['review']>; lang: LanguagePack }) {
  return <section className="queue-review-evidence" aria-label={lang.pendingReview}>
    <h3>{lang.pendingReview}</h3><p>{review.reason}</p>
    <dl><dt>{lang.originalQueue}</dt><dd><bdi dir="ltr">{review.queueId}</bdi></dd>
      <dt>{lang.interruptionDetected}</dt><dd><bdi dir="ltr">{review.detectedAt || '—'}</bdi></dd>
      <dt>{lang.originalCreated}</dt><dd><bdi dir="ltr">{review.createdAt || '—'}</bdi></dd></dl>
    {review.items?.map(item => <article key={item.operationId}>
      <strong><bdi dir="auto">{item.name}</bdi></strong>
      <div>{lang.originalOperation}: <bdi dir="ltr">{item.operationId}</bdi></div>
      <div>{lang.lastKnownState}: {lang.stages[item.lastState as keyof typeof lang.stages] || item.lastState}</div>
      <code dir="ltr">{item.appId}</code><code dir="ltr">{item.registryKey || item.source}</code>
      <div>{lang.registeredCommand}</div><code dir="ltr">{item.registeredCommand || '—'}</code>
      <div>{lang.lastEvidenceTime}: <bdi dir="ltr">{item.finishedAt || item.startedAt || '—'}</bdi></div>
      <p dir="auto">{item.evidence || lang.noRecordedEvidence}</p>
      {item.exitCode != null && <code dir="ltr">Exit: {item.exitCode}</code>}
    </article>)}
    {review.observation && <pre dir="auto">{review.observation}</pre>}
  </section>
}

function UninstallFlowSteps({ active, lang }: { active: 0 | 1 | 2 | 3; lang: LanguagePack }) {
  const steps = [lang.confirmTitle, lang.running, lang.scanLeftovers, lang.results]
  return <ol className="uninstall-flow-steps" aria-label={lang.current}>
    {steps.map((label, index) => <li key={label} className={index < active ? 'complete' : index === active ? 'active' : ''} aria-current={index === active ? 'step' : undefined}>
      <span>{index < active ? <Check size={12} /> : index + 1}</span><bdi dir="auto">{label}</bdi>
    </li>)}
  </ol>
}

function InventoryLoading({ stage, lang }: { stage: string; lang: LanguagePack }) {
  return <div className="inventory-loading" role="status" aria-live="polite"><div className="inventory-orbit"><img src={productIcon} alt="" /><span /></div><h2>{lang.inventoryTitle}</h2><p>{lang.stages[stage as keyof typeof lang.stages] || lang.inventoryHint}</p><small>{lang.inventoryHint}</small></div>
}

function UninstallConfirmation({ items, lang, protectDefault = false, close, execute }: {
  items: AppItem[]; lang: LanguagePack; protectDefault?: boolean; close: () => void
  execute: (options: { protect: boolean; review: boolean }) => Promise<boolean>
}) {
  const [starting, setStarting] = useState(false)
  const [protect, setProtect] = useState(protectDefault)
  const [review, setReview] = useState(false)
  const [dialogRef, onDialogKeyDown] = useFocusTrap<HTMLDivElement>(true)
  const ar = lang === words.ar
  const title = items.length > 1
    ? (ar ? `هل تريد إلغاء تثبيت البرامج المحددة (${items.length})؟` : `Are you sure you want to uninstall the selected ${items.length} programs?`)
    : lang.confirmOne
  return <div className="modal-backdrop"><div ref={dialogRef} onKeyDown={onDialogKeyDown} tabIndex={-1} className="modal uninstall-shell premium-confirm" role="dialog" aria-modal="true" aria-label={title}>
    <header className="workflow-header"><span>Uninstaller 77</span><button className="icon-button" aria-label={lang.close} disabled={starting} onClick={close}><X size={17} /></button></header>
    <div className="confirmation-apps">{items.map(app => <article className="confirmation-card" key={app.id}>
      <AppIcon app={app} variant="list" /><div className="confirmation-identity"><strong title={app.name}><bdi dir="auto">{app.name}</bdi></strong></div>
      <span className="confirmation-size" dir="ltr">{app.size || '—'}</span></article>)}</div>
    <p className="confirmation-question">{title}</p>
    <div className="uninstall-options">
      <label className="option-row"><input type="checkbox" checked={protect} disabled={starting} onChange={event => setProtect(event.target.checked)} />
        {ar ? 'إنشاء نقطة استعادة قبل الإزالة' : 'Create a restore point before uninstall'}
      </label>
      <label className="option-row"><input type="checkbox" checked={review} disabled={starting} onChange={event => setReview(event.target.checked)} />
        {lang.scanAfterShort}
      </label>
    </div>
    <div className="modal-actions"><button className="button quiet" disabled={starting} onClick={close}>{lang.cancel}</button>
      <button className="button primary" disabled={starting || !items.length} onClick={async () => {
        setStarting(true)
        if (!await execute({ protect, review })) setStarting(false)
      }}>{starting && <LoaderCircle size={16} className="spin" />}{lang.uninstall}</button>
    </div>
  </div></div>
}

export function UnregisteredDialog({ review, lang, run }: { review: NonNullable<State['appsPage']['unregistered']>; lang: LanguagePack; run: HostAction }) {
  const [consent, setConsent] = useState(false)
  const [pending, setPending] = useState(false)
  const [dialogRef, onDialogKeyDown] = useFocusTrap<HTMLDivElement>(true)
  const t = lang.unregistered
  const busy = pending || review.busy
  const deleted = review.state === 'Deleted', restored = review.state === 'Restored', incomplete = review.state === 'DeleteIncomplete'
  const size = (bytes: number) => bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(2)} GB` : bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MB` : `${Math.ceil((bytes || 0) / 1024)} KB`
  const errorText = unregisteredErrorText(String(review.error || ''), lang === words.ar ? 'ar' : 'en') || t.error
  const action = async (op: HostOperation) => { setPending(true); try { await run(op, { planId: review.planId, acknowledged: consent, permanent: true }) } finally { setPending(false) } }
  return <div className="modal-backdrop high-layer" dir={lang === words.ar ? 'rtl' : 'ltr'}>
    <div ref={dialogRef} onKeyDown={onDialogKeyDown} tabIndex={-1} className="modal unregistered-modal" role="dialog" aria-modal="true" aria-label={t.title}>
      <ModalHeader title={t.title} icon={<Package />} closeLabel={lang.close} close={busy ? undefined : () => void run('closeUnregistered')} />
      <div className="unregistered-body">
        {['Scanning', 'Restoring', 'Deleting'].includes(review.state) ? <div className="unregistered-loading" role="status"><LoaderCircle className="spin" size={28} /><div><strong>{review.state === 'Restoring' ? t.restoring : review.state === 'Deleting' ? t.deleting : t.scanning}</strong>{review.state === 'Scanning' && <p>{t.scanningHint}</p>}{review.state === 'Deleting' && <p>{t.deletedFiles} <bdi dir="ltr">{review.deletedFiles || 0}/{review.deletionTotal || review.count || 0}</bdi></p>}</div></div> : <>
          {review.app && <div className="unregistered-app"><AppIcon app={review.app} variant="list" fallbackLabel={lang.iconUnavailable} /><div><h3><bdi dir="auto">{review.app.name}</bdi></h3><small>{t.subtitle}</small></div></div>}
          {review.fromBackup && <p className="unregistered-preserved">{t.storedNotice}</p>}
          {deleted || restored || incomplete ? <div className="unregistered-status" role="status"><Info size={22} /><div><strong>{restored ? t.restored : deleted ? t.deleted : t.incomplete}</strong><p>{restored ? t.restoreHint : deleted ? t.deletedHint : t.incompleteHint}</p></div></div> : <p>{review.state === 'Unsupported' ? t.unsupportedHint : t.explanation}</p>}
          {(deleted || incomplete) && <p>{t.deletedFiles} <bdi dir="ltr">{review.deletedFiles || 0}/{review.count || 0}</bdi>{incomplete && <> · {t.remainingFiles} <bdi dir="ltr">{(review.remainingFiles ?? -1) >= 0 ? review.remainingFiles : '—'}</bdi>{(review.unverifiedMissingFiles ?? 0) > 0 && <> · {t.unverifiedMissing} <bdi dir="ltr">{review.unverifiedMissingFiles}</bdi></>}</>}</p>}
          {incomplete && review.pendingPath && <dl className="unregistered-paths"><dt>{t.pendingPath}</dt><dd><bdi dir="ltr">{review.pendingPath}</bdi></dd></dl>}
          {review.root && <dl className="unregistered-paths"><div><dt>{t.scope}</dt><dd><bdi dir="ltr">{review.root}</bdi></dd></div><div><dt>{t.executable}</dt><dd><bdi dir="ltr">{review.target}</bdi></dd></div></dl>}
          {review.planId && <><div className="unregistered-metrics"><span>{t.files}<bdi dir="ltr">{review.count}</bdi></span><span>{t.size}<bdi dir="ltr">{size(review.bytes)}</bdi></span><span>{t.package}<bdi dir="ltr">{review.kind === 'ElectronBundle' ? 'Electron' : 'EXE'}</bdi></span></div>
            <p className="unregistered-preserved">{t.preserved}</p>
            {review.state === 'Review' && <details className="unregistered-files"><summary>{t.preview}</summary><div className="unregistered-file-list">{(review.files || []).map(file => <div key={file.path}><bdi dir="ltr">{file.path}</bdi><bdi dir="ltr">{size(file.bytes)}</bdi></div>)}</div>{review.count > 200 && <p className="unregistered-note">{t.more}</p>}</details>}
          </>}
          {review.error && <div className="unregistered-error" role="alert"><p>{errorText}</p><details className="unregistered-technical"><summary>{t.diagnostics}</summary><code dir="ltr">{review.error}</code></details></div>}
          {(review.state === 'Review' || incomplete && review.canRetryDelete) && <label className="unregistered-consent"><input data-testid="unregistered-consent" type="checkbox" checked={consent} disabled={busy} onChange={event => setConsent(event.target.checked)} />{t.consent}</label>}
          <small className="unregistered-note">{t.historyNote}</small>
        </>}
      </div>
      <div className="modal-actions"><button className="button quiet" disabled={busy} onClick={() => void run('closeUnregistered')}>{lang.close}</button>
        {review.state === 'Review' && <button data-testid="unregistered-confirm" className="button primary" disabled={busy || !consent} onClick={() => void action('confirmUnregistered')}>{busy ? <LoaderCircle className="spin" size={16} /> : <Package size={16} />}{t.action}</button>}
        {incomplete && review.canRetryDelete && <button data-testid="unregistered-retry-delete" className="button primary" disabled={busy || !consent} onClick={() => void action('retryUnregisteredDelete')}><RefreshCw size={16} />{t.retryDelete}</button>}
        {incomplete && <button className="button quiet" onClick={() => void run('openControlFolder', { folder: review.pendingFolder || 'PendingRemoval' })}>{t.openPending}</button>}
      </div>
    </div>
  </div>
}

function Metric({ label, value, tone }: { label: string; value: number | string; tone?: string }) {
  return <div className={`metric ${tone || ''}`}><span>{label}</span><strong><bdi dir="ltr">{value}</bdi></strong></div>
}

function formatByteSize(bytes: number) {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`
  if (bytes >= 1024) return `${Math.ceil(bytes / 1024)} KB`
  return `${bytes} B`
}

function ResidualScanSummary({ residual, lang }: { residual: State['appsPage']['residual']; lang: LanguagePack }) {
  return <details className="residual-scan-summary">
    <summary><ShieldCheck size={16} /><strong>{residual.foundSelectable ? lang.residualScanDetails : lang.residualNothingToDelete}</strong><ChevronDown size={14} /></summary>
    <div className="residual-scan-summary-content">
      <p>{lang.residualLocations} <bdi dir="ltr">{residual.scannedLocations}</bdi></p>
      {residual.summary.map(group => <section key={group.id}>
        <strong><bdi dir="auto">{group.name}</bdi></strong>
        <p>{group.status}</p>
        {group.folder && <p>{group.folder}</p>}
        {group.folder && group.location && <div><bdi dir="ltr">{group.location}</bdi></div>}
        {(group.informational?.length ?? 0) > 0 && <ul aria-label={lang.residualReviewOnly}>{group.informational?.map((path: string) =>
          <li key={path}><bdi dir="ltr">{path}</bdi> · {lang.residualReviewOnly}</li>)}</ul>}
      </section>)}
    </div>
  </details>
}

function ResidualDialog({ residual, lang, run }: { residual: State['appsPage']['residual']; lang: LanguagePack; run: HostAction }) {
  const [pending, setPending] = useState(false)
  const [deleteRequested, setDeleteRequested] = useState(false)
  const [stopPending, setStopPending] = useState(false)
  const deleteObserved = useRef(false)
  const [copiedPath, setCopiedPath] = useState<number | null>(null)
  const [dialogRef, onDialogKeyDown] = useFocusTrap<HTMLDivElement>(true)
  useEffect(() => {
    if (residual.cleaning) deleteObserved.current = true
    else if (deleteObserved.current) { deleteObserved.current = false; setDeleteRequested(false) }
  }, [residual.cleaning])
  const cleaning = Boolean(residual.cleaning || deleteRequested)
  const busy = pending || cleaning
  const ar = lang === words.ar
  const items = residual.items || []
  const eligible = items.filter(item => item.canClean)
  const selected = eligible.filter(item => item.selected).length
  const reviewId = residual.reviewId
  const act = async (action: HostOperation, payload = {}) => {
    setPending(true)
    try { return await run(action, { reviewId, ...payload }) }
    finally { setPending(false) }
  }
  const copyPath = async (index: number, path: string) => {
    let copied = false
    if (navigator.clipboard?.writeText) {
      try { await navigator.clipboard.writeText(path); copied = true } catch { /* fall back to the document copy command */ }
    }
    if (!copied) {
      try {
        const field = document.createElement('textarea')
        field.value = path; field.style.position = 'fixed'; field.style.opacity = '0'
        document.body.appendChild(field); field.select()
        try { copied = document.execCommand('copy') } finally { field.remove() }
      } catch { copied = false }
    }
    setCopiedPath(copied ? index : null)
  }
  const deleteSelected = async () => {
    if (!selected || busy) return
    setDeleteRequested(true)
    try { await run('deleteReviewedResiduals', { reviewId, selectionVersion: residual.selectionVersion ?? 0, confirmed: true }) }
    finally { setDeleteRequested(false) }
  }
  const cancel = async () => {
    if (!cleaning) { await act('skipResiduals'); return }
    if (!residual.cleaning || residual.deleteStopRequested || stopPending) return
    setStopPending(true)
    try { await run('stopResidualCleanup', { reviewId }) }
    finally { setStopPending(false) }
  }
  const toggleSelection = (candidates: ResidualItem[], label: string, update: (selected: boolean) => void) => {
    if (!candidates.length) return <span className="residual-check-spacer" aria-hidden="true" />
    const count = candidates.filter(item => item.selected).length
    return <input type="checkbox" aria-label={label} checked={count === candidates.length}
      ref={input => { if (input) input.indeterminate = count > 0 && count < candidates.length }}
      disabled={busy} onClick={event => event.stopPropagation()}
      onChange={event => update(event.target.checked)} />
  }
  const isSystemItem = (item: typeof items[number]) =>
    !['File', 'Directory'].includes(item.kind || 'File')
  const systemType = (kind?: string) => kind === 'Service' ? lang.residualService
    : kind === 'ScheduledTask' ? lang.residualScheduledTask
    : kind === 'Shortcut' ? lang.residualShortcut
    : kind === 'InstallationInfo' ? lang.residualInstallationInfo : lang.residualRegistry
  const reviewReason = (reason?: string) => {
    if (!reason || lang === words.en) return reason
    if (reason.startsWith('The installation log links')) return 'يربط سجل التثبيت هذا العنصر المطابق بالتثبيت السابق. للمراجعة فقط.'
    if (reason.startsWith('Exact package-family')) return 'تطابق معرّف الحزمة؛ قد يحتوي بيانات شخصية ويحتاج مراجعة.'
    if (reason.startsWith('Service image') || reason.startsWith('Shortcut target')) return 'يشير إلى مسار التثبيت السابق؛ يجب التحقق من الملكية قبل أي إجراء.'
    if (reason.startsWith('Recorded uninstall identity')) return 'بقيت هوية التثبيت في سجل Windows؛ يلزم التحقق من حالتها.'
    if (reason.startsWith('Scheduled-task name') || reason.startsWith('Shortcut name') || reason.includes('Name match') || reason.includes('name path match') || reason.includes('path match'))
      return 'مطابقة اسم فقط؛ ملكية العنصر غير مؤكدة.'
    return reason
  }
  const renderItem = (item: typeof items[number]) => {
    const system = isSystemItem(item)
    const copyablePath = item.path.replace(/ \[(?:32|64)-bit\]$/, '')
    return <div className={`residual-row ${system ? 'system' : ''} ${!item.canClean ? 'informational' : ''}`} key={item.index}>
      {item.canClean ? <label className="residual-check"><input type="checkbox" checked={item.selected} disabled={busy} aria-label={item.title}
        onChange={e => void act('selectResidual', { index: item.index, selected: e.target.checked })} /></label> : <span className="residual-check-spacer" />}
      {system ? <LayoutGrid size={14} aria-hidden="true" /> : <FileText size={14} aria-hidden="true" />}
      <code className="residual-inline-path" dir="ltr" title={item.path}>{item.path}</code>
      {system && item.kind !== 'RegistryKey' && item.kind !== 'RegistryValue' && <span className="residual-item-kind">{systemType(item.kind)}</span>}
      {!item.canClean && <span className="residual-review-badge" title={item.reason}>{lang.residualReviewOnly}</span>}
      <button type="button" className="residual-copy-path" title={lang.residualCopyPath} onClick={() => void copyPath(item.index, copyablePath)}>{copiedPath === item.index ? lang.residualCopied : lang.residualCopyPath}</button>
      {!item.canClean && item.reason && <small className="residual-item-reason" dir="auto" title={item.reason}>{reviewReason(item.reason)}</small>}
    </div>
  }
  return (
    <div className="modal-backdrop top-layer" dir={lang === words.ar ? 'rtl' : 'ltr'}>
      <div ref={dialogRef} onKeyDown={onDialogKeyDown} tabIndex={-1} className="modal uninstall-shell residual-modal residual-review" role="dialog" aria-modal="true" aria-label={lang.residualTitle}>
        <header className="workflow-header"><span>Uninstaller 77</span>
          <button className="icon-button" aria-label={lang.close} disabled={busy} onClick={() => void cancel()}><X size={16} /></button>
        </header>
        <p className="residual-reference-intro" role="status">{cleaning
          ? residual.deleteStopRequested ? (ar ? 'سيتوقف التنظيف بعد العنصر الجاري.' : 'Cleanup will stop after the current item.') : lang.residualDeleteRunning
          : items.length
            ? (ar ? 'اكتملت الإزالة. راجع المخلفات التالية.' : 'Uninstallation completed. The following leftovers were found.')
            : (ar ? 'نتائج فحص المخلفات' : 'Leftover scan results')}</p>
        <div className={`residual-list ${cleaning ? 'is-cleaning' : ''}`} aria-label={lang.residualTitle}>
          {(residual.groups || []).map(group => { const groupItems = items.slice(group.offset, group.offset + group.count)
            const files = groupItems.filter(item => !isSystemItem(item))
            const registry = groupItems.filter(item => item.kind === 'RegistryKey' || item.kind === 'RegistryValue')
            const system = groupItems.filter(item => isSystemItem(item) && !registry.includes(item))
            const selectGroup = (selected: boolean, kind?: string) =>
              void act('selectResidualGroup', { appId: group.app.id, selected, ...(kind ? { kind } : {}) })
            return group.count === 0 ?
            <div key={group.app.id} className="residual-group residual-group-empty-row" title={group.reason || group.status}>
              <AppIcon app={group.app} variant="small" fallbackLabel={lang.iconUnavailable} />
              <strong><bdi dir="auto">{group.app.name}</bdi></strong>
              <span className="residual-group-scan-status" title={[group.reason, ...(group.coverageIssues || [])].filter(Boolean).join('\n') || group.status}>{group.status}{group.reason && <> — {group.reason}</>}{group.coverageIssues?.length ? <> — {lang.residualUnavailableScope}: {group.coverageIssues[0]}</> : null}</span>
              <span>{lang.residualFound} <bdi dir="ltr">0</bdi></span>
            </div> : <details key={group.app.id} className="residual-group" open>
            <summary><span className="tree-expander" aria-hidden="true" />
              {toggleSelection(groupItems.filter(item => item.canClean), group.app.name, selectGroup)}
              <AppIcon app={group.app} variant="small" fallbackLabel={lang.iconUnavailable} />
              <strong><bdi dir="auto">{group.app.name}</bdi></strong>
              <span><bdi dir="ltr">({group.count})</bdi></span>
            </summary>
            <div className="residual-group-items">
              {group.coverageIssues?.length ? <small title={group.coverageIssues.join('\n')}>{lang.residualUnavailableScope}: {group.coverageIssues.join('; ')}</small> : null}
              {([
                { kind: 'registry', label: lang.residualRegistry, rows: registry, icon: <LayoutGrid size={14} /> },
                { kind: 'files', label: lang.residualFilesSection, rows: files, icon: <FolderOpen size={14} /> },
                { kind: 'system', label: ar ? 'مكونات النظام' : 'System components', rows: system, icon: <Settings size={14} /> },
              ]).filter(category => category.rows.length > 0).map(category =>
                <details className="residual-kind-group" key={category.kind} open>
                  <summary><span className="tree-expander" aria-hidden="true" />
                    {toggleSelection(category.rows.filter(item => item.canClean), category.label, checked => selectGroup(checked, category.kind))}
                    {category.icon}{category.label} <bdi dir="ltr">({category.rows.length})</bdi>
                  </summary>
                  {category.rows.map(renderItem)}
                </details>)}
            </div>
          </details>})}
        </div>
        <footer className="residual-footer">
        {cleaning ? <div className="residual-delete-progress">
          <progress aria-label={lang.residualDeleteRunning} value={residual.deleteProcessed || 0} max={Math.max(1, residual.deleteTotal || selected)} />
          <bdi dir="ltr" title={residual.deleteCurrentPath}>{residual.deleteCurrentPath || residual.deleteCurrentApp || '—'}</bdi>
        </div> : eligible.length ? <>
          <label className="residual-select-toggle">
            {toggleSelection(eligible, lang.residualSelectAll, checked => void act(checked ? 'selectAllResiduals' : 'deselectAllResiduals'))}
            {lang.residualSelectAll}
          </label>
          <span className="residual-select-toggle" role="status">{lang.residualSelected} <bdi dir="ltr">{selected}/{eligible.length}</bdi></span>
        </> : <p className="residual-select-toggle" role="status">{lang.residualNothingToDelete}</p>}
        <div className="modal-actions">
            <button className="button quiet" disabled={pending || stopPending || (cleaning && (!residual.cleaning || residual.deleteStopRequested))} onClick={() => void cancel()}>{!cleaning && eligible.length === 0 ? lang.close : lang.cancel}</button>
            <button className="button residual-danger" disabled={selected === 0 || busy} onClick={() => void deleteSelected()}>
              {ar ? 'حذف' : 'Delete'}
            </button>
        </div>
        </footer>
      </div>
    </div>
  )
}

function HistoryPage({ page, lang, run, onClear }: { page: State['historyPage']; lang: LanguagePack; run: HostAction; onClear: () => void }) {
  const labels = page.labels || {}
  const [changingPage, setChangingPage] = useState(false)
  const [detailsRef, onDetailsKeyDown] = useFocusTrap<HTMLDivElement>(Boolean(page.details))
  const changePage = async (direction: 'newer' | 'older') => {
    setChangingPage(true)
    try { await run('changeHistoryPage', { direction, page: page.pageNumber }) }
    finally { setChangingPage(false) }
  }
  return (
    <div className="history-page">
      <header className="page-header">
        <div>
          <h1>{lang.uninstallHistory}</h1>
          <p className="subtitle">{lang.historyDescription}</p>
        </div>
        <div className="header-actions">
          <button className="button quiet" onClick={() => void run('loadHistory')}><RotateCcw size={15} />{lang.refresh}</button>
          <button className="button quiet" disabled={!page.records?.length} onClick={onClear}><Trash2 size={15} />{lang.clearHistory}</button>
        </div>
      </header>
      <section className="apps-surface">
        <div className="table-head history-head">
          <div className="head-app-name">{labels.applicationLabel || lang.application}</div>
          <div>{lang.engine}</div>
          <div>{lang.status}</div>
          <div>{lang.residuals}</div>
          <div>{lang.date}</div>
        </div>
        {(page.records || []).length ? (
          <VirtualList key={page.records[0].id} items={page.records as HistoryItem[]} rowHeight={56} render={(item: HistoryItem) => (
            <div className="app-row history-row" data-outcome={item.outcome}>
              <div className="app-name-cell">
                <AppIcon app={item} variant="small" />
                <div className="app-name-meta">
                  {item.outcome === 'QueueProtection' ? <button className="button quiet" onClick={() => void run('openHistoryDetails', { id: item.id })}>{item.name}</button> : <>
                    <strong title={item.name}><bdi dir="auto">{item.name}</bdi></strong>
                    <span className="publisher-chip"><bdi dir="auto">{item.publisher}</bdi> · <bdi dir="ltr">{item.version}</bdi></span>
                  </>}
                </div>
              </div>
              <div className="cell-dim">{item.outcome === 'QueueProtection' ? page.restorePointLabel : item.installerType}</div>
              {item.outcome === 'QueueProtection' ? <div className="history-residual" title={item.status}>{item.status}</div> : <div className={historyOutcome(item, lang === words.ar ? 'ar' : 'en').tone} title={item.status}>{historyOutcome(item, lang === words.ar ? 'ar' : 'en').label}{historyOutcome(item, lang === words.ar ? 'ar' : 'en').showExit && item.exitCode != null && <bdi dir="ltr"> ({item.exitCode})</bdi>}</div>}
              <div className="history-residual" title={item.residualStatus}>{item.residualStatus}{item.canRescan && <button className="button quiet icon-button" title={lang.rescanHistory} aria-label={`${lang.rescanHistory}: ${item.name}`} onClick={() => void run('openHistoryDetails', { id: item.id })}><RotateCcw size={14}/></button>}</div>
              <div className="history-timestamp" title={item.date}><bdi dir="ltr">{historyTimestamp(item.date).date}</bdi><bdi dir="ltr">{historyTimestamp(item.date).time}</bdi></div>
            </div>
          )} />
        ) : (
          <div className="empty-state">
            <History size={36} className="text-muted" />
            <strong>{lang.noHistoryRecords}</strong>
          </div>
        )}
        {page.records?.length > 0 && page.range && (
          <nav className="history-pagination" aria-label={lang.historyPagination}>
            <span aria-live="polite">{lang.historyShowing} <bdi dir="ltr">{page.range}</bdi></span>
            <div className="history-pagination-actions">
              <button className="button quiet" disabled={changingPage || !page.hasNewerPage} onClick={() => void changePage('newer')}>{lang.historyNewerPage}</button>
              <button className="button quiet" disabled={changingPage || !page.hasOlderPage} onClick={() => void changePage('older')}>{lang.historyOlderPage}</button>
            </div>
          </nav>
        )}
      </section>
      {page.details && <div className="modal-backdrop">
        <div ref={detailsRef} onKeyDown={onDetailsKeyDown} tabIndex={-1} className="modal settings-modal" role="dialog" aria-modal="true" aria-label={page.details.name}>
          <ModalHeader closeLabel={lang.close} icon={<History />} title={page.details.name} close={() => void run('closeHistoryDetails')} />
          {page.details.outcome === 'QueueProtection' ? <div className="setting-block protection-details">
            <h3>{page.restorePointLabel}</h3><p>{page.details.status}</p>
            <h3>{page.registryBackupLabel}</h3><p>{page.details.residualStatus}</p>
            <p className="protection-evidence" dir="auto">{page.details.notes}</p>
          </div> : <div className="setting-block protection-details">
            <p>{page.details.status} · {page.details.residualStatus}</p>
            <p className="protection-evidence" dir="auto">{page.details.notes}</p>
            {page.details.canRescan ? <div className="modal-actions">
              <button className="button quiet" onClick={() => void run('rescanHistoryResiduals', { id: page.details?.id, deep: false })}>{lang.rescanHistory}</button>
              <button className="button quiet" onClick={() => void run('rescanHistoryResiduals', { id: page.details?.id, deep: true })}>{lang.rescanHistoryDeep}</button>
            </div> : <p>{lang.rescanHistoryUnavailable}</p>}
          </div>}
        </div>
      </div>}
    </div>
  )
}

function SettingsDialog({ state, lang, run }: { state: State; lang: LanguagePack; run: HostAction }) {
  const [dialogRef, onDialogKeyDown] = useFocusTrap<HTMLDivElement>(true)
  return (
    <div className="modal-backdrop">
      <div ref={dialogRef} onKeyDown={onDialogKeyDown} tabIndex={-1} className="modal settings-modal workspace-settings" role="dialog" aria-modal="true" aria-label={lang.settings}>
        <ModalHeader closeLabel={lang.close} icon={<Settings />} title={lang.settings} close={() => void run('closeSettings')} />
        <div className="setting-block">
          <h3>{lang.appearance}</h3>
          <div className="language-picker">
            <span>{lang.language}</span>
            <div>
              <button className={state.language === 'en' ? 'active' : ''} aria-pressed={state.language === 'en'} onClick={() => void run('setLanguage', { code: 'en' })}>{lang.english}</button>
              <button className={state.language === 'ar' ? 'active' : ''} aria-pressed={state.language === 'ar'} onClick={() => void run('setLanguage', { code: 'ar' })}>{lang.arabic}</button>
            </div>
          </div>
          <label className="protection-option">
            <input type="checkbox" checked={state.reduceMotion} onChange={event => void run('setReduceMotion', { enabled: event.currentTarget.checked })} />
            {state.language === 'ar' ? 'تقليل الحركة' : 'Reduce motion'}
          </label>
        </div>
        {state.queueProtectionSettings && <div className="setting-block">
          <label className="protection-option">
            <input type="checkbox" checked={state.queueProtectionSettings.enabled} onChange={event => void run('setQueueProtection', { enabled: event.currentTarget.checked })} />
            {state.queueProtectionSettings.label}
          </label>
          <p className="subtitle">{state.queueProtectionSettings.hint}</p>
        </div>}
        <div className="setting-block">
          <button type="button" className="button quiet" onClick={() => void run('restoreQuarantine')}><RotateCcw size={14} />{lang.restoreLeftoverBackup}</button>
        </div>
        <div className="modal-actions">
          <button className="button quiet" onClick={() => void run('openProductWebsite')}><ExternalLink size={14} />{lang.productWebsite}</button>
          <button className="button primary" onClick={() => void run('closeSettings')}>{lang.close}</button>
        </div>
      </div>
    </div>
  )
}

export default App
// Isolated component tests import these; no test state is included in the product.
export { AppIcon }



