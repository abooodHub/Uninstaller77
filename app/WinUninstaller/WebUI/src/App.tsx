import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { flushSync } from 'react-dom'
declare const __APP_VERSION__: string
import {
  AppWindow, ArrowDownAZ, ArrowDownWideNarrow, ArrowUpDown, BadgeCheck,
  Check, ChevronDown, CircleAlert, Globe2,
  Clock3, Download, ExternalLink, FileText, FolderOpen, History, Info,
  LoaderCircle, Package, Puzzle,
  RefreshCw, RotateCcw, Search, Settings, ShieldAlert, ShieldCheck, Sliders,
  Trash2, X
} from 'lucide-react'
import './styles.css'
import './desktop.css'
import './phase10.css'
import './motion.css'
import './control-center.css'
import './queue-progress.css'
import './unregistered.css'
import { words } from './ui-strings'
import chromeIcon from './assets/browsers/chrome.png'
import edgeIcon from './assets/browsers/edge.png'
import braveIcon from './assets/browsers/brave.png'
import productIcon from './assets/u77.png'
import { unregisteredErrorText, localizeState, localizeText, queueProgress, inventoryCount, historyTimestamp, browserGroups, resolveExtensionBrowser, extensionIdentity, extensionPermissionImpact, virtualRange, markFirstAppearance, verifiedQueuePercent, reconcileSearch, historyOutcome, queueFailureInfo } from './presentation'

type AppItem = {
  id: string; name: string; publisher: string; version: string; size: string; sizeKb: number
  installDate: string; architecture: string; installerType: string; source: string
  icon: string | null; selected: boolean; installLocation: string; registryKey: string
  uninstallCommand: string; quietUninstallCommand: string
  isBuiltIn?: boolean; isThirdParty?: boolean; isBundleware?: boolean; isLogged?: boolean
  description?: string
}

type ExtensionItem = {
  id: string; browser: string; profile: string; name: string; desc: string
  version: string; icon: string | null; isPolicyManaged: boolean; installationLocation: string
  permissions?: string[]; optionalPermissions?: string[]; hostPermissions?: string[]; optionalHostPermissions?: string[]
  contentScriptHosts?: string[]; enabledState?: 'Enabled' | 'Disabled' | 'Installed' | 'Unknown'; installationSource?: string; managementCapability?: string
}

type HistoryItem = {
  id: number; name: string; publisher: string; version: string; date: string; installerType: string
  success: boolean; outcome?: string; status: string; residualStatus: string; exitCode: number | null; icon: string | null; notes: string; test: boolean
  cleanedItemsCount: number; cleanedSizeBytes: number
}

type State = {
  revision?: number; measurePerformance?: boolean
  language: 'en' | 'ar'; direction: 'ltr' | 'rtl'; reduceMotion: boolean; settingsOpen: boolean; notice: string; noticeIsError: boolean
  page: 'apps' | 'history' | 'extensions' | 'control'
  activeSidebarItem: string; navigation: Record<string, string>
  appsPage: any; historyPage: any; extensionsPage: any; controlPage?: any
}

type RequestMessage =
  | { type: 'state'; state: State }
  | { type: 'response'; id: string; ok: boolean; state?: State; error?: string }
  | { type: 'error'; error: string }

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void
        addEventListener: (event: 'message', listener: (event: MessageEvent<RequestMessage>) => void) => void
        removeEventListener: (event: 'message', listener: (event: MessageEvent<RequestMessage>) => void) => void
      }
    }
  }
}

const defaultState: State = {
  language: 'ar', direction: 'rtl', reduceMotion: false, settingsOpen: false, notice: '', noticeIsError: false,
  page: 'apps', activeSidebarItem: 'AllApps', navigation: {},
  appsPage: {
    items: [], total: 0, filtered: 0, recent: 0, large: 0, store: 0, bundleware: 0, logged: 0, infrequent: 0,
    windowsApps: 0, thirdPartyApps: 0, selected: 0, category: 'All', sort: 'NameAsc', labels: {},
    operation: { open: false, isRunning: false, isBatch: false, logs: [], results: [] },
    residual: { open: false, items: [] }
  },
  historyPage: { records: [], labels: {} },
  extensionsPage: { loading: false, scanned: false, items: [] },
}

function useHost() {
  const [state, setState] = useState<State>(defaultState)
  const [toast, setToast] = useState('')
  const [toastIsError, setToastIsError] = useState(false)
  const [sharedDetailsId, setSharedDetailsId] = useState('')
  const sharedDetailsIdRef = useRef('')
  const stateRef = useRef(state)
  const pending = useRef(new Map<string, (message: RequestMessage) => void>())

  const send = useCallback((op: string, payload: Record<string, unknown> = {}) => new Promise<RequestMessage>((resolve, reject) => {
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
    const receive = (event: { data: any }) => {
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

  const act = useCallback(async (op: string, payload: Record<string, unknown> = {}) => {
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

  return { state, setState, act, toast, setToast, toastIsError, setToastIsError, sharedDetailsId }
}

interface AppIconProps {
  app: { name: string; icon?: string | null }
  small?: boolean
  variant?: 'list' | 'details' | 'progress' | 'small' | 'default'
  selected?: boolean
  isProcessing?: boolean
  operationState?: string
  sharedTransition?: boolean
  className?: string
  fallbackLabel?: string
}

function AppIcon({
  app,
  small = false,
  variant = 'default',
  selected = false,
  isProcessing = false,
  operationState,
  sharedTransition = false,
  className = '',
  fallbackLabel = ''
}: AppIconProps) {
  const [failedSource, setFailedSource] = useState<string | null>(null)
  const hasIcon = Boolean(app.icon && app.icon !== failedSource)
  const effectiveVariant = small ? 'small' : variant
  const verifiedIcon = operationState === 'Succeeded' || operationState === 'Completed'
  const stateCls = operationState ? `state-${operationState.toLowerCase()}` : ''

  return (
    <span
      className={`app-icon animated-icon-container variant-${effectiveVariant} ${selected ? 'is-selected' : ''} ${isProcessing ? 'is-processing' : ''} ${sharedTransition ? 'shared-icon-transition' : ''} ${stateCls} ${className}`}
      data-variant={effectiveVariant}
      data-selected={selected ? 'true' : 'false'}
    >
      {(effectiveVariant === 'progress' || isProcessing) && (
        <svg className="icon-processing-ring" viewBox="0 0 48 48" aria-hidden="true">
          <circle className="processing-track-bg" cx="24" cy="24" r="21" fill="none" strokeWidth="2.5" />
          <circle
            className={`processing-track-fg ${isProcessing ? 'spinning' : ''} ${stateCls}`}
            cx="24"
            cy="24"
            r="21"
            fill="none"
            strokeWidth="2.5"
            strokeDasharray="131.95"
            strokeDashoffset={isProcessing ? '40' : '0'}
          />
        </svg>
      )}
      <span className="animated-icon-inner" data-icon-source={hasIcon ? 'application' : 'unavailable'}>
        {hasIcon ? (
          <img className="app-icon-img" src={app.icon!} alt="" loading={effectiveVariant === 'progress' || effectiveVariant === 'details' ? 'eager' : 'lazy'} onError={() => setFailedSource(app.icon || null)} />
        ) : <span className="app-icon-fallback" title={fallbackLabel || undefined} role={fallbackLabel ? 'img' : undefined} aria-label={fallbackLabel || undefined}><Package aria-hidden="true" strokeWidth={1.5} /></span>}
      </span>
      {effectiveVariant === 'progress' && !isProcessing && operationState && (
        <span className={`icon-state-badge ${verifiedIcon ? 'success' : operationState === 'Failed' ? 'failure' : 'neutral'}`}>
          {verifiedIcon ? <Check size={11} strokeWidth={3} /> : <CircleAlert size={11} strokeWidth={3} />}
        </span>
      )}
    </span>
  )
}

function VirtualList({ items, render, rowHeight, className = '' }: { items: any[]; render: (item: any, index: number) => ReactNode; rowHeight: number; className?: string }) {
  const host = useRef<HTMLDivElement>(null)
  const entered = useMemo(() => new Set<string>(), [])
  const [viewport, setViewport] = useState({ top: 0, height: 640 })
  useEffect(() => {
    if (!host.current) return
    const observer = new ResizeObserver(entries => setViewport(v => ({ ...v, height: entries[0].contentRect.height })))
    observer.observe(host.current)
    return () => observer.disconnect()
  }, [])
  const { start, end, totalHeight } = virtualRange(items.length, viewport.top, viewport.height, rowHeight)
  return (
    <div className={`virtual-scroll ${className}`} ref={host} onScroll={e => { const top = e.currentTarget.scrollTop; setViewport(v => ({ ...v, top })) }}>
      <div style={{ height: totalHeight, position: 'relative' }}>
        {items.slice(start, end).map((item, i) => {
          const index = start + i
          const stableId = Array.isArray(item) ? item[0]?.id : item?.id ?? item?.name
          const key = String(stableId ?? index)
          return (
            <div key={key} className="virtual-row" style={{ height: rowHeight, transform: `translateY(${index * rowHeight}px)` }}>
              <div ref={node => { if (node && markFirstAppearance(key, entered)) node.classList.add('virtual-item-enter') }} style={{ '--stagger': Math.min(i, 8) } as React.CSSProperties}>
                {render(item, index)}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function App() {
  const host = useHost()
  const { act, setToast, setToastIsError } = host
  const { sharedDetailsId } = host
  const state = useMemo(() => localizeState(host.state) as State, [host.state])
  const lang = words[state.language] || words.en
  const rtl = state.language === 'ar'
  const p = state.appsPage
  const hp = state.historyPage
  const catalogTitles: Record<string, string> = { All: lang.allPrograms, Bundleware: lang.bundleware,
    Recent: lang.recentlyInstalled, Large: lang.largePrograms,
    Infrequent: lang.infrequentlyUsed, WindowsApps: lang.builtinWindowsApps, ThirdPartyApps: lang.thirdPartyApps, Store: lang.windowsApps }
  const catalogTitle = catalogTitles[p.category] || p.title || lang.allPrograms
  const labels = p.labels || {}
  const op = p.operation || {}
  const residual = p.residual || {}
  const [search, setSearch] = useState('')
  const pendingSearch = useRef<string | null>(null)
  const [queueReview, setQueueReview] = useState<AppItem[] | null>(null)
  const [clearReview, setClearReview] = useState(false)
  const [programsExpanded, setProgramsExpanded] = useState(true)
  const [extensionsExpanded, setExtensionsExpanded] = useState(true)
  const [extensionBrowser, setExtensionBrowser] = useState('')
  const [windowsAppsExpanded, setWindowsAppsExpanded] = useState(true)
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

  useEffect(() => {
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

  const run = (opName: string, payload: Record<string, unknown> = {}) => act(opName, payload)
  const navigate = (target: string) => { setQueueReview(null); void run('navigate', { target }) }
  const openExtensions = (browser?: string) => {
    setExtensionBrowser(browser ?? '')
    setExtensionsExpanded(true)
    if (state.page !== 'extensions') navigate('BrowserExtensions')
  }
  const extensionGroups = browserGroups((state.extensionsPage?.items || []) as ExtensionItem[], state.extensionsPage?.browsers || [])
  const activeExtensionBrowser = resolveExtensionBrowser(extensionGroups, extensionBrowser)
  const selectVisible = p.filtered > 0 && p.selected === p.filtered

  return (
    <div className={`app-shell ${rtl ? 'rtl' : ''}`} dir={rtl ? 'rtl' : 'ltr'} data-page={state.page}>
      {/* SIDEBAR NAVIGATION */}
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark"><img src={productIcon} alt="" /></div>
          <div>
            <strong>{lang.brand}</strong>
            <span className="brand-edition">{lang.edition}</span>
          </div>
        </div>

        <div className="sidebar-scroll">
          {/* Section 1: Programs */}
          <div className="nav-group">
            <button className={`nav-group-header ${state.page === 'apps' && ['AllApps', 'Bundleware', 'RecentApps', 'LargeApps', 'InfrequentApps'].includes(state.activeSidebarItem) ? 'active' : ''}`} aria-expanded={programsExpanded} aria-controls="programs-navigation" onClick={() => setProgramsExpanded(v => !v)}>
              <span className="nav-header-title"><Package size={16} className="nav-icon nav-icon-programs" />{lang.programs}</span>
              <ChevronDown size={14} className={`nav-chevron ${programsExpanded ? '' : 'collapsed'}`} />
            </button>
            <NavDisclosure id="programs-navigation" open={programsExpanded}>
                <button className={`nav-sub-item ${state.activeSidebarItem === 'AllApps' ? 'active' : ''}`} onClick={() => navigate('AllApps')}>
                  <span>{lang.allPrograms}</span>
                  <span className="nav-count">{p.hasConfirmedInventory ? p.total : '—'}</span>
                </button>
                <button className={`nav-sub-item ${state.activeSidebarItem === 'RecentApps' || state.activeSidebarItem === 'Recent' ? 'active' : ''}`} onClick={() => navigate('RecentApps')}>
                  <span>{lang.recentlyInstalled}</span>
                  <span className="nav-count">{p.hasConfirmedInventory ? p.recent : '—'}</span>
                </button>
                <button className={`nav-sub-item ${state.activeSidebarItem === 'LargeApps' || state.activeSidebarItem === 'Large' ? 'active' : ''}`} onClick={() => navigate('LargeApps')}>
                  <span>{lang.largePrograms}</span>
                  <span className="nav-count">{p.hasConfirmedInventory ? p.large : '—'}</span>
                </button>
                <div className="classification-unavailable" role="note">{lang.classificationUnavailable}</div>
            </NavDisclosure>
          </div>

          {/* Section 4: Browser Extensions */}
          <div className="nav-group">
            <button className={`nav-group-header ${state.page === 'extensions' || state.activeSidebarItem === 'BrowserExtensions' ? 'active' : ''}`} aria-expanded={extensionsExpanded} aria-controls="extensions-navigation" onClick={() => { if (state.page !== 'extensions') openExtensions(); else setExtensionsExpanded(v => !v) }}>
              <span className="nav-header-title"><Puzzle size={16} className="nav-icon nav-icon-extensions" />{lang.browserExtensions}</span>
              <ChevronDown size={14} className={`nav-chevron ${extensionsExpanded ? '' : 'collapsed'}`} />
            </button>
            <NavDisclosure id="extensions-navigation" open={extensionsExpanded}>
                {extensionGroups.length === 0 && <span className="extension-nav-status" role="status">{state.extensionsPage?.loading ? lang.scanning : state.extensionsPage?.error ? lang.scanError : state.extensionsPage?.scanned ? lang.extensionNoBrowsersFound : lang.notScanned}</span>}
                {extensionGroups.map(group => (
                  <button key={group.browser} className={`nav-sub-item ${state.page === 'extensions' && activeExtensionBrowser === group.browser ? 'active' : ''}`} onClick={() => openExtensions(group.browser)}>
                    <span className="extension-nav-label"><BrowserIcon browser={group.browser} />{group.browser}</span>
                    <span className="nav-count">{state.extensionsPage?.loading ? '…' : state.extensionsPage?.scanned ? group.items.length : '—'}</span>
                  </button>
                ))}
            </NavDisclosure>
          </div>

          {/* Section 5: Windows Apps */}
          <div className="nav-group">
            <button className={`nav-group-header ${['WindowsApps', 'ThirdPartyApps'].includes(state.activeSidebarItem) || state.appsPage?.category === 'WindowsApps' || state.appsPage?.category === 'ThirdPartyApps' ? 'active' : ''}`} aria-expanded={windowsAppsExpanded} aria-controls="windows-navigation" onClick={() => setWindowsAppsExpanded(v => !v)}>
              <span className="nav-header-title"><WindowsStoreIcon />{lang.windowsApps}</span>
              <ChevronDown size={14} className={`nav-chevron ${windowsAppsExpanded ? '' : 'collapsed'}`} />
            </button>
            <NavDisclosure id="windows-navigation" open={windowsAppsExpanded}>
                <button className={`nav-sub-item ${state.activeSidebarItem === 'WindowsApps' ? 'active' : ''}`} onClick={() => navigate('WindowsApps')}>
                  <span>{lang.builtinWindowsApps}</span>
                  <span className="nav-count">{p.hasConfirmedInventory ? p.windowsApps : '—'}</span>
                </button>
                <button className={`nav-sub-item ${state.activeSidebarItem === 'ThirdPartyApps' ? 'active' : ''}`} onClick={() => navigate('ThirdPartyApps')}>
                  <span>{lang.thirdPartyApps}</span>
                  <span className="nav-count">{p.hasConfirmedInventory ? p.thirdPartyApps : '—'}</span>
                </button>
            </NavDisclosure>
          </div>

          {/* Section 6: Control Center */}
          <div className="nav-group">
            <button className={`nav-item-root ${state.activeSidebarItem === 'ControlCenter' ? 'active' : ''}`} onClick={() => navigate('ControlCenter')}>
              <span className="nav-root-content"><Sliders size={16} className="nav-icon nav-icon-control" />{lang.controlCenter}</span>
            </button>
          </div>
        </div>

        {/* Bottom Sidebar: History & Settings */}
        <div className="sidebar-bottom">
          <button className={`nav-sub-item ${state.activeSidebarItem === 'History' ? 'active' : ''}`} onClick={() => navigate('History')}>
            <span className="nav-bottom-item"><History size={15} className="nav-icon nav-icon-history" />{lang.uninstallHistory}</span>
          </button>
          <button className="nav-sub-item" onClick={() => void run('openSettings')}>
            <span className="nav-bottom-item"><Settings size={15} className="nav-icon nav-icon-settings" />{lang.settings}</span>
          </button>
        </div>
      </aside>

      {/* MAIN VIEWPORT */}
      <main className="main-panel" ref={mainPanelRef}>
        {state.page === 'apps' && (
          <>
            <header className="page-header">
              <div>
                <h1>
                  {catalogTitle}
                  <span className="title-count">({p.hasConfirmedInventory ? p.filtered : '—'})</span>
                </h1>
                <p className="subtitle">{['WindowsApps', 'ThirdPartyApps'].includes(p.category) ? lang.windowsAppsIntro : lang.appsIntro}</p>
              </div>
              <div className="header-actions">
                {p.hasPendingQueueReview && <button className="button quiet" data-testid="open-queue-review" onClick={() => void run('openQueueReview')}><CircleAlert size={15} />{lang.pendingReview}</button>}
                <button className="button quiet" onClick={() => void run('refresh')} title={lang.refresh}><RotateCcw size={15} />{lang.refresh}</button>
              </div>
            </header>

            <section className="apps-surface">
              {/* Primary selection and uninstall actions stay above the inventory. */}
              <div className="bulk-bar bulk-bar-top">
                <div className="bulk-bar-left">
                  <label className="select-visible">
                    <input type="checkbox" checked={selectVisible} onChange={e => void run('selectAll', { selected: e.target.checked })} />
                    <span>{lang.selectAll}</span>
                  </label>
                  <span className="bulk-bar-count"><bdi dir="ltr"><b>{p.selected || 0}</b> / {p.hasConfirmedInventory ? p.filtered : '—'}</bdi> {lang.totalShown}</span>
                </div>
                <button className="button primary" disabled={!p.selected} onClick={() => setQueueReview(selectedItems.map(item => ({ ...item })))}>
                  <Trash2 size={15} />
                  {lang.uninstall} ({p.selected || 0})
                </button>
              </div>
              <div className="toolbar">
                <label className="searchbox">
                  <Search size={16} />
                  <input aria-label={lang.search} value={search} onChange={e => { pendingSearch.current = e.target.value; setSearch(e.target.value) }} placeholder={lang.search} />
                </label>
                <div className="toolbar-controls">
                  <SortButton mode="NameAsc" current={p.sort} label={labels.sortNameLabel || 'Name'} icon={<ArrowDownAZ size={14} />} act={act} />
                  <SortButton mode="SizeDesc" current={p.sort} label={labels.sortSizeLabel || 'Size'} icon={<ArrowDownWideNarrow size={14} />} act={act} />
                  <SortButton mode="DateDesc" current={p.sort} label={labels.sortDateLabel || 'Date'} icon={<ArrowUpDown size={14} />} act={act} />
                </div>
              </div>

              <div className="table-head">
                <input type="checkbox" checked={selectVisible} onChange={e => void run('selectAll', { selected: e.target.checked })} />
                <div className="head-app-name">{labels.applicationColumnLabel || 'Name'} ({p.hasConfirmedInventory ? p.filtered : '—'})</div>
                <div>{labels.sizeColumnLabel || lang.size}</div>
                <div>{labels.installedColumnLabel || lang.installDate}</div>
                <div>{labels.versionColumnLabel || lang.version}</div>
                <div className="align-center">{lang.actions}</div>
              </div>

              {p.isLoading && p.hasConfirmedInventory && (
                <div className="inventory-refresh" role="status"><LoaderCircle size={16} className="spin" />{lang.refreshing}<span>{lang.stages[p.inventoryStage as keyof typeof lang.stages] || p.inventoryStage}</span></div>
              )}
              {p.inventoryState === 'Failed' && (
                <div className="inventory-error" role="alert"><CircleAlert size={18} /><div><strong>{lang.inventoryFailed}</strong><p>{p.inventoryError}</p></div><button className="button quiet" onClick={() => void run('refresh')}>{lang.retry}</button></div>
              )}
              {p.inventoryState === 'Failed' && !p.hasConfirmedInventory ? null : !p.hasConfirmedInventory ? (
                <InventoryLoading stage={p.inventoryStage} lang={lang} />
              ) : (p.items || []).length === 0 ? (
                <div className="empty-state">
                  <div className="empty-icon"><Search /></div>
                  <strong>{lang.emptyApps}</strong>
                </div>
              ) : (
                <VirtualList items={p.items as AppItem[]} rowHeight={54} render={(app: AppItem) => (
                  <AppRow app={app} lang={lang} run={run} sharedDetailsId={sharedDetailsId} />
                )} />
              )}

            </section>
          </>
        )}

        {state.page === 'extensions' && (
          <ExtensionsPage page={state.extensionsPage} lang={lang} run={run} selectedBrowser={activeExtensionBrowser} onSelectBrowser={setExtensionBrowser} />
        )}

        {state.page === 'control' && (
          <ControlPage lang={lang} run={run} page={state.controlPage || {}} inventory={p} scope={{ title: catalogTitle, search: p.search || '', count: p.filtered, ready: p.hasConfirmedInventory }} />
        )}

        {state.page === 'history' && (
          <HistoryPage page={hp} lang={lang} run={run} onClear={() => setClearReview(true)} />
        )}
      </main>

      {/* MODALS */}
      {p.unregistered?.open && <UnregisteredDialog review={p.unregistered} lang={lang} run={run} />}
      {p.details && <DetailsDialog app={p.details} labels={labels} lang={lang} close={() => void run('closeDetails')} sharedDetailsId={sharedDetailsId} />}

      {p.confirm && (
        <UninstallConfirmation items={[p.confirm]} lang={lang} close={() => void run('cancelConfirm')}
          execute={() => run('confirmUninstall', { appId: p.confirm.id })}>
          <label className="option-row">
            <input type="checkbox" checked={p.quiet} onChange={e => void run('setUninstallOptions', { quiet: e.target.checked, scanResiduals: p.scanResiduals })} />
            <div><strong>{labels.quietModeLabel || lang.quiet}</strong><small>{labels.quietModeHintLabel}</small></div>
          </label>
          <label className="option-row">
            <input type="checkbox" checked={p.scanResiduals} onChange={e => void run('setUninstallOptions', { quiet: p.quiet, scanResiduals: e.target.checked })} />
            <div><strong>{labels.reviewLeftoversLabel || lang.scanAfter}</strong><small>{labels.reviewLeftoversHintLabel}</small></div>
          </label>
        </UninstallConfirmation>
      )}

      {queueReview && (
        <UninstallConfirmation items={queueReview} lang={lang} close={() => setQueueReview(null)}
          execute={async () => { const ids = queueReview.map(item => item.id); setQueueReview(null); return run('runBatch', { appIds: ids }) }} />
      )}

      {op.open && <ProgressDialog op={op} lang={lang} run={run} />}

      {residual.open && <ResidualDialog residual={residual} lang={lang} labels={labels} run={run} />}

      {clearReview && (
        <ConfirmDialog
          title={lang.clearHistory}
          body={lang.clearHistoryConfirm}
          confirm={lang.clearHistory}
          cancel={lang.cancel}
          onCancel={() => setClearReview(false)}
          onConfirm={async () => { setClearReview(false); await run('clearHistory') }}
        />
      )}

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

function NavDisclosure({ id, open, children }: { id: string; open: boolean; children: ReactNode }) {
  return <div id={id} className="nav-disclosure" data-open={open} aria-hidden={!open} inert={!open}>
    <div className="nav-disclosure-content"><div className="nav-sub-list">{children}</div></div>
  </div>
}

function SortButton({ mode, current, label, icon, act }: { mode: string; current: string; label: string; icon: ReactNode; act: (op: string, payload?: Record<string, unknown>) => Promise<boolean> }) {
  const selected = String(current || '')
  const active = selected === mode || (mode === 'NameAsc' && selected === 'NameDesc') || (mode === 'SizeDesc' && selected.startsWith('Size'))
  const next = mode === 'NameAsc' && selected === 'NameAsc' ? 'NameDesc' : mode === 'SizeDesc' && selected === 'SizeDesc' ? 'SizeAsc' : mode
  return (
    <button className={`sort-button ${active ? 'active' : ''}`} onClick={() => void act('sort', { value: next })}>
      {icon}<span>{label}</span>
    </button>
  )
}

function AppRow({ app, lang, run, sharedDetailsId = '' }: { app: AppItem; lang: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean>; sharedDetailsId?: string }) {
  return (
    <div className={`app-row ${app.selected ? 'is-selected' : ''}`}>
      <input type="checkbox" aria-label={`${lang.selectAll}: ${app.name}`} checked={app.selected} onChange={e => void run('selectApp', { appId: app.id, selected: e.target.checked })} />
      <div className="app-name-cell">
        <AppIcon app={app} selected={app.selected} variant="list" sharedTransition={sharedDetailsId === app.id} />
        <div className="app-name-meta">
          <strong title={app.name}><bdi dir="auto">{app.name}</bdi></strong>
          <span className="publisher-chip"><bdi dir="auto">{app.publisher || localizeText('Unknown', lang === words.ar ? 'ar' : 'en')}</bdi></span>
        </div>
      </div>
      <div className="cell-dim"><bdi dir="auto">{app.size}</bdi></div>
      <div className="cell-dim"><bdi dir="auto">{app.installDate}</bdi></div>
      <div className="cell-dim" title={app.version || ''}><bdi dir="ltr">{app.version || '—'}</bdi></div>
      <div className="row-actions align-center">
        <button className="icon-action-button trash" title={lang.uninstall} onClick={() => void run('promptUninstall', { appId: app.id })}>
          <Trash2 size={16} />
        </button>
        <button className="icon-action-button" title={lang.details} onClick={() => void run('openDetails', { appId: app.id })}>
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
const browserIconFiles: Record<string, string> = {
  'Google Chrome': chromeIcon, 'Microsoft Edge': edgeIcon, Brave: braveIcon
}

function BrowserIcon({ browser, className = '' }: { browser: string; className?: string }) {
  const file = browserIconFiles[browser]
  const brandCls = browser.toLowerCase().replace(/\s+/g, '-')
  return (
    <span className={`browser-icon-wrap brand-${brandCls} ${className}`}>
      {file ? (
        <img className="browser-icon" src={file} width="20" height="20" alt={browser} />
      ) : (
        <Globe2 className="browser-icon browser-icon-fallback" size={20} aria-hidden="true" />
      )}
    </span>
  )
}

function ExtensionsPage({ page, lang, run, selectedBrowser, onSelectBrowser }: {
  page: any; lang: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean>
  selectedBrowser: string; onSelectBrowser: (browser: string) => void
}) {
  const [profile, setProfile] = useState('All')
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState('All')
  const [sort, setSort] = useState('Name')
  const [details, setDetails] = useState<ExtensionItem | null>(null)
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
  const selectBrowser = (browser: string) => { setProfile('All'); setFilter('All'); onSelectBrowser(browser) }
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
  const row = (ext: ExtensionItem) => (
    <div className="app-row ext-row" key={extensionIdentity(ext)}>
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
      <div className="extension-status-cell"><span className={`extension-status-badge ${extensionStateClass(ext)}`}>{extensionStateLabel(ext)}</span><small><bdi dir="ltr">{ext.version || '—'}</bdi></small>{ext.isPolicyManaged && <small className="extension-policy-label">{lang.extensionManaged}</small>}</div>
    </div>
  )

  return (
    <div className="extensions-page">
      <header className="page-header">
        <div>
          <h1>{lang.browserExtensions} <span className="title-count">({page.error ? lang.scanError : inventoryCount({ ...page, items: browserItems }, lang.notScanned, lang.scanning)})</span></h1>
          <p className="subtitle">{lang.extensionsIntro}</p>
        </div>
        <button className="button quiet" disabled={page.loading} onClick={() => void run('scanExtensions')}>
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
        <label className="extension-search"><Search size={15}/><input value={query} onChange={event => setQuery(event.target.value)} placeholder={lang.extensionSearch}/></label>
        <label className="extension-select"><span>{lang.profiles}</span><select value={activeProfile} onChange={event => setProfile(event.target.value)}><option value="All">{lang.allProfiles}</option>{profiles.map(([key, item]) => <option key={key} value={key}>{item.profile}</option>)}</select></label>
        <label className="extension-select"><span>{lang.extensionSort}</span><select value={sort} onChange={event => setSort(event.target.value)}><option value="Name">{lang.extensionSortName}</option><option value="Version">{lang.extensionSortVersion}</option></select></label>
        <div className="extension-filter-bar"><button className={activeFilter === 'All' ? 'selected' : ''} onClick={() => setFilter('All')}>{lang.extensionFilterAll}</button>{canFilterManaged && <button className={activeFilter === 'Managed' ? 'selected' : ''} onClick={() => setFilter('Managed')}>{lang.extensionFilterManaged} <bdi dir="ltr">{policyManagedCount}</bdi></button>}</div>
      </div>}

      <section className="apps-surface extensions-surface" key={activeBrowser}>
          <div className="table-head ext-table-head">
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
      </section>
      {details && <div className="extension-detail-backdrop" onClick={() => setDetails(null)}><aside className="extension-detail-drawer" role="dialog" aria-modal="true" aria-label={lang.extensionDetails} onClick={event => event.stopPropagation()}>
        <div className="extension-detail-head"><div className="extension-detail-title"><AppIcon app={{name: details.name, icon: details.icon}} variant="list"/><div><h2><bdi dir="auto">{details.name}</bdi></h2><span><BrowserIcon browser={details.browser}/><bdi dir="ltr">{details.browser}</bdi> · <bdi dir="auto">{details.profile}</bdi></span></div></div><button className="button quiet icon-button" aria-label={lang.close} onClick={() => setDetails(null)}><X size={16}/></button></div>
        <div className="extension-detail-scroll extension-detail-compact">
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

// ---------------- CONTROL CENTER VIEW ----------------
function ControlPage({ lang, run, page, inventory, scope }: {
  lang: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean>; page: any; inventory: any
  scope: { title: string; search: string; count: number; ready: boolean }
}) {
  const text = lang.control
  const [busyAction, setBusyAction] = useState<string | null>(null)
  const busyRef = useRef(false)
  const invoke = async (id: string, action: () => Promise<boolean>) => {
    if (busyRef.current) return
    busyRef.current = true; setBusyAction(id)
    try { await action() } finally { busyRef.current = false; setBusyAction(null) }
  }
  const queueRunning = Boolean(inventory.operation?.isRunning)
  const pendingReview = Boolean(inventory.hasPendingQueueReview)
  const tools = [
    { id: 'restore', icon: <RotateCcw />, title: lang.quarantineRestore, desc: lang.quarantineRestoreDesc, cta: text.restore, action: () => run('restoreQuarantine') },
    { id: 'history', icon: <History />, title: lang.uninstallHistory, desc: lang.historyCardDesc, cta: text.open, action: () => run('navigate', { target: 'History' }) },
    { id: 'export', icon: <Download />, title: lang.exportApps, desc: lang.exportAppsDesc, cta: text.export, disabled: !scope.ready || inventory.isLoading, action: () => run('exportApps') },
    { id: 'windows', icon: <WindowsStoreIcon />, title: text.windowsSettings, desc: text.windowsSettingsDescription, cta: text.open, action: () => run('openWindowsAppsSettings') },
    { id: 'report', icon: <FileText />, title: text.supportReport, desc: text.supportDescription, cta: text.save, action: () => run('exportSupportReport') },
    { id: 'settings', icon: <Settings />, title: text.settings, desc: text.settingsDescription, cta: text.open, action: () => run('openSettings') }
  ]
  const folders = [
    { id: 'Logs', title: text.logs, desc: text.logsDescription, icon: <FileText size={19} /> },
    { id: 'Quarantine', title: text.quarantine, desc: text.quarantineDescription, icon: <RotateCcw size={19} /> },
    { id: 'Application', title: text.application, desc: text.applicationDescription, icon: <FolderOpen size={19} /> }
  ]
  return <div className="control-page control-workspace">
    <header className="page-header"><div><h1>{lang.controlCenter}</h1><p className="subtitle">{lang.controlCenterDesc}</p></div>
      <button className="button quiet" disabled={busyAction !== null || inventory.isLoading || queueRunning} onClick={() => void invoke('refresh', () => run('refreshControlCenter'))}>
        <RefreshCw size={15} className={inventory.isLoading || busyAction === 'refresh' ? 'spin' : ''} />{text.refresh}
      </button>
    </header>
    <section className="control-summary" aria-label={lang.controlCenter}>
      <div><span>{text.installed}</span><strong><bdi dir="ltr">{inventory.hasConfirmedInventory ? inventory.total : '—'}</bdi></strong></div>
      <div><span>{text.history}</span><strong><bdi dir="ltr">{page.historyCount ?? '—'}</bdi></strong>{page.historyIncludesTests && <small>{text.testHistory}</small>}</div>
      <div className={pendingReview ? 'needs-review' : ''}><span>{text.queue}</span><strong className="control-queue-state">{queueRunning ? text.running : pendingReview ? text.pending : text.idle}</strong></div>
    </section>
    {pendingReview && <div className="control-review-banner" role="status"><CircleAlert size={21} /><div><strong>{text.pendingTitle}</strong><p>{text.pendingDescription}</p></div><button className="button quiet" disabled={busyAction !== null} onClick={() => void invoke('review', () => run('openQueueReview'))}>{text.review}</button></div>}
    <section className="control-section"><h2>{text.management}</h2><div className="control-tools-grid">
      {tools.map(tool => <article className="control-tool" key={tool.id} data-busy={busyAction === tool.id} aria-busy={busyAction === tool.id}>
        <div className="control-tool-heading"><span className="control-tool-icon">{tool.icon}</span><h3>{tool.title}</h3></div>
        <p>{tool.desc}</p>
        {tool.id === 'export' && <small className="control-export-scope"><bdi dir="auto">{scope.title}</bdi> · <bdi dir="ltr">{scope.ready ? scope.count : '—'}</bdi> {lang.totalShown}{scope.search && <> · <bdi dir="auto">{scope.search}</bdi></>}</small>}
        <button className="button quiet" disabled={busyAction !== null || tool.disabled} onClick={() => void invoke(tool.id, tool.action)}>
          {busyAction === tool.id ? <LoaderCircle size={14} className="spin" /> : tool.id === 'windows' ? <ExternalLink size={14} /> : null}{tool.cta}
        </button>
      </article>)}
    </div></section>
    <section className="control-section"><h2>{text.files}</h2><div className="control-folder-grid">
      {folders.map(folder => <button className="control-folder" disabled={busyAction !== null} key={folder.id} onClick={() => void invoke(folder.id, () => run('openControlFolder', { folder: folder.id }))}>
        <span className="control-folder-icon">{busyAction === folder.id ? <LoaderCircle size={19} className="spin" /> : folder.icon}</span><span><strong>{folder.title}</strong><small>{folder.desc}</small></span><ExternalLink size={14} />
      </button>)}
    </div></section>

  </div>
}

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

function DetailsDialog({ app, labels, lang, close, sharedDetailsId = '' }: { app: AppItem; labels: any; lang: any; close: () => void; sharedDetailsId?: string }) {
  return (
    <div className="modal-backdrop">
      <div className="modal details-modal">
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
        <Detail label={labels.installLocationLabel || 'Install Location'} value={app.installLocation} mono />
        <Detail label={labels.registryKeyLabel || 'Registry Key'} value={app.registryKey} mono />
        <Detail label={labels.uninstallCommandLabel || 'Uninstall Command'} value={app.uninstallCommand} mono />
        <div className="modal-actions">
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

export function ProgressDialog({ op, lang, run }: { op: any; lang: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean> }) {
  const [stopPending, setStopPending] = useState(false)
  const [reviewRequest, setReviewRequest] = useState<string | null>(null)
  const reviewPhase = `${op.review?.queueId || ''}:${Boolean(op.review?.recovered)}`
  const reviewPending = reviewRequest === reviewPhase
  const [ackConsent, setAckConsent] = useState(false)
  const stopping = stopPending || op.stopRequested
  const attention = op.needsReview
  const done = !op.isRunning
  const color = attention || (stopping && !done) ? 'orange' : !done ? 'blue'
    : op.state === 'Succeeded' ? 'green' : ['PartialFailure', 'CompletedWithSkips'].includes(op.state) ? 'orange' : op.state === 'CriticalFailure' ? 'red' : 'gray'
  const summary = queueProgress(op)
  const verifiedPercent = verifiedQueuePercent(op)
  const title = attention ? lang.attention : stopping && !done ? lang.stopRequesting : !done ? lang.running
    : ['PartialFailure', 'CompletedWithSkips'].includes(op.state) ? lang.queuePartial : op.state === 'CriticalFailure' ? lang.queueCritical : op.state === 'Stopped' ? lang.queueStopped : lang.complete
  const rows = op.queueItems?.length ? op.queueItems : (op.results || []).map((item: any) => ({
    ...item, state: item.state === 'Succeeded' ? 'Completed' : item.state === 'NeedsAttention' ? 'NeedsUserAttention' : item.state === 'Skipped' ? 'Cancelled' : item.state
  }))
  const active = attention ? '—' : op.isRunning && !['Completed', 'Failed', 'Cancelled', 'ScanningResiduals'].includes(op.operationState) ? 1 : 0
  const index = rows.findIndex((item: any) => item.app?.id === op.app?.id) + 1
  const currentState = done ? rows.find((item: any) => item.app?.id === op.app?.id)?.state || op.operationState : op.operationState
  const stage = lang.stages[currentState as keyof typeof lang.stages] || op.stage || '—'
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
  return (
    <div className="modal-backdrop high-layer queue-backdrop" dir={lang === words.ar ? 'rtl' : 'ltr'}>
      <div className={'modal progress-modal phase10-progress status-' + color + ' queue-workspace' + (attention ? ' is-review' : '')} role="dialog" aria-modal="true" aria-label={title}>
        <header className="queue-header">
          <span className="queue-status-icon" aria-hidden="true">{attention ? <CircleAlert /> : !done ? <LoaderCircle className="spin" /> : op.state === 'Succeeded' ? <BadgeCheck /> : <Info />}</span>
          <div className="queue-heading"><small>{lang.queueSubtitle}</small><h2>{title}</h2></div>
          <div className="queue-elapsed"><Clock3 size={15} aria-hidden="true" /><span>{lang.elapsed}<bdi dir="ltr">{op.elapsed || '00:00:00'}</bdi></span></div>
          {done && !attention && <button className="icon-button" aria-label={lang.close} onClick={() => void run('closeProgress')}><X size={18} /></button>}
        </header>
        <div className="progress-body">
          <div className="queue-overview">
            {op.preview && <div className="queue-attention">{lang === words.ar ? 'معاينة اختبار اصطناعية صريحة — لم تُزل أي تطبيقات.' : 'Explicit synthetic UI preview — no applications were uninstalled.'}</div>}
            <section className="queue-current" aria-label={lang.current}>
              <AppIcon app={op.app || { name: '' }} variant="progress" isProcessing={op.isRunning && !attention} operationState={currentState} fallbackLabel={lang.iconUnavailable} />
              <div className="queue-current-copy">
                <div className="queue-current-label"><small>{attention ? lang.reviewApplication : done ? lang.lastApplication : lang.current}</small><span>{lang.position} <bdi dir="ltr">{index}/{op.total || 0}</bdi></span></div>
                <h3><bdi dir="auto">{op.app?.name || '—'}</bdi></h3>
                <div className="queue-current-stage" role="status"><span className={'queue-activity ' + (!done && !attention ? 'running' : '')} aria-hidden="true" /><span><span className="queue-stage-label">{lang.stage} · </span>{stage}</span></div>
              </div>
            </section>
            {attention && <section className="queue-registration" data-state={op.review?.registrationState || 'NotChecked'} aria-label={lang.currentRegistration} title={lang.registrationReviewHint}>
              <span>{lang.currentRegistration}</span><strong>{op.review?.registrationState === 'Absent' ? lang.registrationAbsent : op.review?.registrationState === 'Present' ? lang.registrationPresent : op.review?.registrationState === 'Unknown' ? lang.registrationUnknown : lang.registrationNotChecked}</strong>
              <small>{lang.registrationReviewHint}</small>
            </section>}
            {(stopping || attention) && <div className="queue-attention" role="status"><CircleAlert size={18} /><span>{attention ? op.status : lang.stopAfter}</span></div>}
            {done && !attention && ['RemovalNotObserved', 'RestartPending', 'VendorDependenciesInstalled'].includes(op.failureCode) && <div className="queue-attention" role="status"><CircleAlert size={18} /><span>{op.failureCode === 'VendorDependenciesInstalled' ? lang.vendorDependenciesInstalled : op.failureCode === 'RestartPending' ? lang.restartPending : lang.removalNotObserved}</span></div>}
            {(op.errorDetails || attention) && <details className="queue-diagnostics">
              <summary>{lang.queueDiagnostics}<ChevronDown size={14} /></summary>
              <p dir="auto">{op.errorDetails || op.status}</p>
              <div className="queue-diagnostic-codes">
                {op.failureCode && op.failureCode !== 'None' && <code dir="ltr">{op.failureCode}</code>}
                {op.exitCode != null && <span>{lang.exitCodeLabel}: <bdi dir="ltr">{op.exitCode}</bdi></span>}
              </div>
            </details>}
            {op.review?.recovered && <details className="queue-recovery-details"><summary>{lang.pendingReview}<ChevronDown size={14} /></summary><QueueReviewPanel review={op.review} lang={lang} /></details>}
            <section className="queue-progress-section" aria-label={lang.queueCompletion}>
              <div className="queue-progress-caption"><strong>{lang.queueCompletion}</strong><bdi dir="ltr">{summary.percent}%</bdi></div>
              <div role="progressbar" aria-label={lang.queueCompletion} aria-valuemin={0} aria-valuemax={100} aria-valuenow={summary.percent} className={'progress-track determinate ' + (done ? 'finished' : '')}><span style={{ width: summary.percent + '%' }} /></div>
              <div className="queue-progress-meta"><span>{lang.queueResolved} <bdi dir="ltr">{summary.resolved}</bdi></span><span>{lang.verifiedProgress} <bdi dir="ltr">{op.successful || 0}/{op.total || 0} · {verifiedPercent}%</bdi></span></div>
            </section>
            <div className="batch-counters overview">
              <Metric label={lang.successful} value={op.successful || 0} tone="green" />
              <Metric label={lang.failed} value={op.failed || 0} tone="red" />
              <Metric label={lang.skipped} value={op.skipped || 0} />
              <Metric label={lang.active} value={active} />
              <Metric label={lang.remaining} value={op.remaining || 0} />
              <Metric label={lang.totalApps} value={op.total || 0} />
            </div>
          </div>
          <section className="queue-list-section" aria-label={lang.queueItems}>
            <div className="queue-list-heading"><h3>{done && !attention ? lang.results : lang.queueItems}</h3><bdi dir="ltr">{summary.resolved}</bdi></div>
            {(attention || done) && <p className="queue-history-hint">{lang.queueHistoryHint}</p>}
            <div className="queue-results" ref={queueRef}>
              {rows.map((item: any) => <div className={'queue-result result-' + item.state.toLowerCase() + (item.app?.id === op.app?.id ? ' is-current' : '')} key={item.operationId || item.app.id} ref={item.app?.id === op.app?.id ? currentRef : undefined}>
                <AppIcon app={item.app} variant="small" fallbackLabel={lang.iconUnavailable} /><div><strong><bdi dir="auto">{item.app.name}</bdi></strong><small>{lang.stages[item.state as keyof typeof lang.stages] || item.state}</small>{(() => { const info = queueFailureInfo(item, lang === words.ar ? 'ar' : 'en'); return <>{info.reason && <p className="queue-result-reason" dir="auto"><span>{lang.failureReason}: </span>{info.reason}</p>}{(info.category || info.code || info.exitCode != null) && <p className="queue-result-reason queue-result-codes" dir="auto">{info.category && <strong>{info.category}</strong>}{info.code && <code dir="ltr"> {info.code}</code>}{info.exitCode != null && <span> · {lang.exitCodeLabel}: <bdi dir="ltr">{info.exitCode}</bdi></span>}</p>}</> })()}</div>
                <span className="queue-item-state" aria-hidden="true">{item.state === 'Completed' ? <BadgeCheck size={18} /> : item.state === 'Failed' || item.state === 'NeedsUserAttention' ? <CircleAlert size={18} /> : ['Idle', 'Cancelled', 'NotInstalled'].includes(item.state) ? <Clock3 size={17} /> : <LoaderCircle size={17} className={!done && item.app?.id === op.app?.id ? 'spin' : ''} />}</span>
              </div>)}
              {!rows.length && <div className="queue-list-empty">{lang.queueEmpty}</div>}
            </div>
          </section>
          <details className="queue-log"><summary>{lang.queueDiagnostics} · {lang.attempts} <bdi dir="ltr">{summary.attempts}</bdi><ChevronDown size={14} /></summary><div className="event-log" ref={logRef}>{(op.logs || []).map((line: string, i: number) => <div key={i}>{line}</div>)}</div></details>
        </div>
        <footer className="queue-footer">
          {op.review?.recovered && op.review?.canAcknowledge && <label className="review-consent"><input type="checkbox" data-testid="review-ack-consent" checked={ackConsent} onChange={e => setAckConsent(e.target.checked)} />{lang.ackConsent}</label>}
          <div className="modal-actions">
            <span className="queue-footer-hint">{!done && !attention ? lang.stopAfter : attention ? op.review?.recovered ? lang.noAutoResume : lang.reviewActionHint : ''}</span>
            {op.review?.recovered && op.review?.canAcknowledge && <button className="button quiet" data-testid="queue-acknowledge" disabled={!ackConsent || op.retrying || reviewPending} onClick={async () => { setReviewRequest(reviewPhase); if (!await run('acknowledgeQueueReview', { queueId: op.review.queueId, acknowledged: true })) setReviewRequest(null) }}>{lang.acknowledgeUnresolved}</button>}
            {attention && <><button className="button quiet" data-testid="queue-retry-verification" disabled={op.retrying || reviewPending} onClick={() => void run('retryQueueVerification')}><RefreshCw size={15} className={op.retrying ? 'spin' : ''} />{lang.retryVerification}</button><button className="button quiet stop-queue" data-testid="queue-review-stop" disabled={reviewPending} onClick={async () => { setReviewRequest(reviewPhase); if (!await run('reviewQueue')) setReviewRequest(null) }}>{reviewPending ? <LoaderCircle size={14} className="spin" /> : <X size={15} />}{reviewPending ? lang.stopRequesting : op.review?.recovered ? lang.keepPending : lang.stopAndReview}</button></>}
            {!done && !attention && <button className="button quiet stop-queue" disabled={stopping} onClick={() => void requestStop()}>{stopping ? <LoaderCircle size={15} className="spin" /> : <X size={15} />}{stopping ? lang.stopRequesting : lang.stopQueue}</button>}
            {!done && op.canCancelCurrent && <button className="button quiet" onClick={() => void run('cancelProgress')}>{lang.cancelCurrent}</button>}
            {done && !attention && <button className="button primary" onClick={() => void run('closeProgress')}><Check size={16} />{lang.close}</button>}
          </div>
        </footer>
      </div>
    </div>
  )
}
function QueueReviewPanel({ review, lang }: { review: any; lang: any }) {
  return <section className="queue-review-evidence" aria-label={lang.pendingReview}>
    <h3>{lang.pendingReview}</h3><p>{review.reason}</p>
    <dl><dt>{lang.originalQueue}</dt><dd><bdi dir="ltr">{review.queueId}</bdi></dd>
      <dt>{lang.interruptionDetected}</dt><dd><bdi dir="ltr">{review.detectedAt || '—'}</bdi></dd>
      <dt>{lang.originalCreated}</dt><dd><bdi dir="ltr">{review.createdAt || '—'}</bdi></dd></dl>
    {review.items?.map((item: any) => <article key={item.operationId}>
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

function InventoryLoading({ stage, lang }: { stage: string; lang: any }) {
  return <div className="inventory-loading" role="status" aria-live="polite"><div className="inventory-orbit"><img src={productIcon} alt="" /><span /></div><h2>{lang.inventoryTitle}</h2><p>{lang.stages[stage] || lang.inventoryHint}</p><small>{lang.inventoryHint}</small></div>
}

function UninstallConfirmation({ items, lang, close, execute, children }: {
  items: AppItem[]; lang: any; close: () => void; execute: () => Promise<boolean>; children?: ReactNode
}) {
  const [starting, setStarting] = useState(false)
  return <div className="modal-backdrop"><div className="modal premium-confirm" role="dialog" aria-modal="true" aria-label={lang.confirmTitle}>
    <ModalHeader closeLabel={lang.close} icon={<Trash2 />} title={lang.confirmTitle} close={starting ? undefined : close} />
    <div className="confirmation-summary"><strong>{lang.selectedCount}</strong><span>{items.length}</span></div>
    <div className="confirmation-apps">{items.map(app => <article className="confirmation-card" key={app.id}>
      <AppIcon app={app} variant="list" /><div className="confirmation-identity"><strong><bdi dir="auto">{app.name}</bdi></strong>
        <span><bdi dir="auto">{app.publisher || '—'}</bdi> · <bdi dir="ltr">{app.version || '—'}</bdi></span>
        <small>{lang.sourceType}: {app.source} · {app.installerType} · <bdi dir="ltr">{app.architecture}</bdi></small>
        <details><summary>{lang.installationIdentity}</summary><code dir="ltr">{app.registryKey || app.id}</code><code dir="ltr">{app.installLocation}</code></details>
      </div><span className="confirmation-size" dir="ltr">{app.size}</span></article>)}</div>
    {children}
    <div className="windows-change-note"><ShieldAlert size={18} /><p>{lang.windowsWarning}</p></div>
    <div className="modal-actions"><button className="button quiet" disabled={starting} onClick={close}>{lang.cancel}</button>
      <button className="button primary" disabled={starting || !items.length} onClick={async () => { setStarting(true); if (!await execute()) setStarting(false) }}>{starting ? <LoaderCircle size={16} className="spin" /> : <Trash2 size={16} />}{lang.startUninstall} <bdi dir="ltr">({items.length})</bdi></button>
    </div>
  </div></div>
}

export function UnregisteredDialog({ review, lang, run }: { review: any; lang: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean> }) {
  const [consent, setConsent] = useState(false)
  const [pending, setPending] = useState(false)
  const t = lang.unregistered
  const busy = pending || review.busy
  const deleted = review.state === 'Deleted', restored = review.state === 'Restored', incomplete = review.state === 'DeleteIncomplete'
  const size = (bytes: number) => bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(2)} GB` : bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MB` : `${Math.ceil((bytes || 0) / 1024)} KB`
  const errorText = unregisteredErrorText(String(review.error || ''), lang === words.ar ? 'ar' : 'en') || t.error
  const action = async (op: string) => { setPending(true); try { await run(op, { planId: review.planId, acknowledged: consent, permanent: true }) } finally { setPending(false) } }
  return <div className="modal-backdrop high-layer" dir={lang === words.ar ? 'rtl' : 'ltr'}>
    <div className="modal unregistered-modal" role="dialog" aria-modal="true" aria-label={t.title}>
      <ModalHeader title={t.title} icon={<Package />} closeLabel={lang.close} close={busy ? undefined : () => void run('closeUnregistered')} />
      <div className="unregistered-body">
        {['Scanning', 'Restoring', 'Deleting'].includes(review.state) ? <div className="unregistered-loading" role="status"><LoaderCircle className="spin" size={28} /><div><strong>{review.state === 'Restoring' ? t.restoring : review.state === 'Deleting' ? t.deleting : t.scanning}</strong>{review.state === 'Scanning' && <p>{t.scanningHint}</p>}{review.state === 'Deleting' && <p>{t.deletedFiles} <bdi dir="ltr">{review.deletedFiles || 0}/{review.deletionTotal || review.count || 0}</bdi></p>}</div></div> : <>
          {review.app && <div className="unregistered-app"><AppIcon app={review.app} variant="list" fallbackLabel={lang.iconUnavailable} /><div><h3><bdi dir="auto">{review.app.name}</bdi></h3><small>{t.subtitle}</small></div></div>}
          {review.fromBackup && <p className="unregistered-preserved">{t.storedNotice}</p>}
          {deleted || restored || incomplete ? <div className="unregistered-status" role="status"><Info size={22} /><div><strong>{restored ? t.restored : deleted ? t.deleted : t.incomplete}</strong><p>{restored ? t.restoreHint : deleted ? t.deletedHint : t.incompleteHint}</p></div></div> : <p>{review.state === 'Unsupported' ? t.unsupportedHint : t.explanation}</p>}
          {(deleted || incomplete) && <p>{t.deletedFiles} <bdi dir="ltr">{review.deletedFiles || 0}/{review.count || 0}</bdi>{incomplete && <> · {t.remainingFiles} <bdi dir="ltr">{review.remainingFiles >= 0 ? review.remainingFiles : '—'}</bdi>{review.unverifiedMissingFiles > 0 && <> · {t.unverifiedMissing} <bdi dir="ltr">{review.unverifiedMissingFiles}</bdi></>}</>}</p>}
          {incomplete && review.pendingPath && <dl className="unregistered-paths"><dt>{t.pendingPath}</dt><dd><bdi dir="ltr">{review.pendingPath}</bdi></dd></dl>}
          {review.root && <dl className="unregistered-paths"><div><dt>{t.scope}</dt><dd><bdi dir="ltr">{review.root}</bdi></dd></div><div><dt>{t.executable}</dt><dd><bdi dir="ltr">{review.target}</bdi></dd></div></dl>}
          {review.planId && <><div className="unregistered-metrics"><span>{t.files}<bdi dir="ltr">{review.count}</bdi></span><span>{t.size}<bdi dir="ltr">{size(review.bytes)}</bdi></span><span>{t.package}<bdi dir="ltr">{review.kind === 'ElectronBundle' ? 'Electron' : 'EXE'}</bdi></span></div>
            <p className="unregistered-preserved">{t.preserved}</p>
            {review.state === 'Review' && <details className="unregistered-files"><summary>{t.preview}</summary><div className="unregistered-file-list">{(review.files || []).map((file: any) => <div key={file.path}><bdi dir="ltr">{file.path}</bdi><bdi dir="ltr">{size(file.bytes)}</bdi></div>)}</div>{review.count > 200 && <p className="unregistered-note">{t.more}</p>}</details>}
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

function ResidualDialog({ residual, lang, labels, run }: { residual: any; lang: any; labels: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean> }) {
  return (
    <div className="modal-backdrop top-layer">
      <div className="modal residual-modal">
        <ModalHeader closeLabel={lang.close} icon={<ShieldCheck />} title={lang.residualTitle} close={() => void run('skipResiduals')} />
        <p className="modal-copy">{residual.message || lang.scanLeftovers}</p>
        <div className="residual-toolbar">
          <span><bdi dir="ltr">{(residual.items || []).length}</bdi> {lang.results}</span>
          <div>
            <button disabled={residual.cleaning} onClick={() => void run('selectAllResiduals')}>{labels.selectAllLabel || 'Select All'}</button>
            <button disabled={residual.cleaning} onClick={() => void run('deselectAllResiduals')}>{labels.deselectAllLabel || 'Deselect All'}</button>
          </div>
        </div>
        <div className="residual-list">
          {residual.items?.length ? (
            residual.items.map((item: any) => (
              <label className={`residual-row ${!item.canClean ? 'informational' : ''}`} key={item.index}>
                <input type="checkbox" checked={item.selected} disabled={!item.canClean || residual.cleaning} onChange={e => void run('selectResidual', { index: item.index, selected: e.target.checked })} />
                <div className="residual-row-main">
                  <strong><bdi dir="ltr">{item.path}</bdi></strong>
                  <span>{item.type} · {item.confidence} · {item.reason}</span>
                </div>
                <span className="residual-size">{item.canClean ? item.size : lang.cannotClean}</span>
              </label>
            ))
          ) : (
            <div className="empty-inline"><BadgeCheck size={20} /> {lang.noLeftovers}</div>
          )}
        </div>
        <div className="residual-safety">
          <ShieldCheck size={16} />
          <span>{lang.safety}</span>
          <strong>{residual.size || '0 B'}</strong>
        </div>
        <div className="modal-actions">
          <button className="button quiet" disabled={residual.cleaning} onClick={() => void run('skipResiduals')}>{lang.skip}</button>
          <button className="button primary" disabled={!residual.items?.some((x: any) => x.canClean && x.selected) || residual.cleaning} onClick={() => void run('cleanResiduals')}>
            {residual.cleaning ? <LoaderCircle size={15} className="spin" /> : <ShieldCheck size={15} />}
            {lang.cleanup}
          </button>
        </div>
      </div>
    </div>
  )
}

function HistoryPage({ page, lang, run, onClear }: { page: any; lang: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean>; onClear: () => void }) {
  const labels = page.labels || {}
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
          <VirtualList items={page.records as HistoryItem[]} rowHeight={56} render={(item: HistoryItem) => (
            <div className="app-row history-row" data-outcome={item.outcome}>
              <div className="app-name-cell">
                <AppIcon app={item} variant="small" />
                <div className="app-name-meta">
                  <strong title={item.name}><bdi dir="auto">{item.name}</bdi></strong>
                  <span className="publisher-chip"><bdi dir="auto">{item.publisher}</bdi> · <bdi dir="ltr">{item.version}</bdi></span>
                </div>
              </div>
              <div className="cell-dim">{item.installerType}</div>
              <div className={historyOutcome(item, lang === words.ar ? 'ar' : 'en').tone} title={item.status}>{historyOutcome(item, lang === words.ar ? 'ar' : 'en').label}{historyOutcome(item, lang === words.ar ? 'ar' : 'en').showExit && item.exitCode != null && <bdi dir="ltr"> ({item.exitCode})</bdi>}</div>
              <div className="history-residual" title={item.residualStatus}>{item.residualStatus}</div>
              <div className="history-timestamp" title={item.date}><bdi dir="ltr">{historyTimestamp(item.date).date}</bdi><bdi dir="ltr">{historyTimestamp(item.date).time}</bdi></div>
            </div>
          )} />
        ) : (
          <div className="empty-state">
            <History size={36} className="text-muted" />
            <strong>{lang.noHistoryRecords}</strong>
          </div>
        )}
      </section>
    </div>
  )
}

function SettingsDialog({ state, lang, run }: { state: State; lang: any; run: (op: string, payload?: Record<string, unknown>) => Promise<boolean> }) {
  return (
    <div className="modal-backdrop">
      <div className="modal settings-modal">
        <ModalHeader closeLabel={lang.close} icon={<Settings />} title={lang.settings} close={() => void run('closeSettings')} />
        <div className="setting-block">
          <h3>{lang.appearance}</h3>
          <div className="language-picker">
            <span>{lang.language}</span>
            <div>
              <button className={state.language === 'en' ? 'active' : ''} onClick={() => void run('setLanguage', { code: 'en' })}>{lang.english}</button>
              <button className={state.language === 'ar' ? 'active' : ''} onClick={() => void run('setLanguage', { code: 'ar' })}>{lang.arabic}</button>
            </div>
          </div>
        </div>
        <div className="modal-actions">
          <button className="button primary" onClick={() => void run('closeSettings')}>{lang.close}</button>
        </div>
      </div>
    </div>
  )
}

function ConfirmDialog({ title, body, confirm, cancel, onCancel, onConfirm }: { title: string; body: string; confirm: string; cancel: string; onCancel: () => void; onConfirm: () => void }) {
  return (
    <div className="modal-backdrop top-layer">
      <div className="modal confirm-modal">
        <ModalHeader closeLabel={cancel} icon={<CircleAlert />} title={title} close={onCancel} />
        <p className="modal-copy">{body}</p>
        <div className="modal-actions">
          <button className="button quiet" onClick={onCancel}>{cancel}</button>
          <button className="button danger" onClick={onConfirm}><Trash2 size={14} />{confirm}</button>
        </div>
      </div>
    </div>
  )
}

export default App
// Isolated component tests import these; no test state is included in the product.
export { AppIcon }



