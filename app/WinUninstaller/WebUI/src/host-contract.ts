export type AppItem = {
  id: string; name: string; publisher: string; version: string; size: string; sizeKb: number
  installDate: string; architecture: string; installerType: string; source: string
  icon: string | null; selected: boolean; installLocation: string; registryKey: string
  uninstallCommand: string; quietUninstallCommand: string
  canUninstall?: boolean
  isBuiltIn?: boolean; isThirdParty?: boolean; isBundleware?: boolean; isLogged?: boolean
  description?: string
}
export type ExtensionItem = {
  id: string; browser: string; profile: string; name: string; desc: string
  version: string; icon: string | null; isPolicyManaged: boolean; installationLocation: string
  permissions?: string[]; optionalPermissions?: string[]; hostPermissions?: string[]; optionalHostPermissions?: string[]
  contentScriptHosts?: string[]; enabledState?: 'Enabled' | 'Disabled' | 'Installed' | 'Unknown'; installationSource?: string; managementCapability?: string
}

export type HistoryItem = {
  id: number; name: string; publisher: string; version: string; date: string; installerType: string
  success: boolean; outcome?: string; status: string; residualStatus: string; exitCode: number | null; icon: string | null; notes: string | null; test: boolean
  cleanedItemsCount: number; cleanedSizeBytes: number; residualPermanent?: string | null; canRescan?: boolean
}

export type QueueReviewItem = {
  operationId: string; appId: string; name: string; source: string; registryKey: string
  registeredCommand: string; lastState: string; startedAt?: string | null; finishedAt?: string | null
  evidence?: string; exitCode?: number | null
}

export type QueueReview = {
  queueId: string; recovered: boolean; canAcknowledge: boolean; detectedAt?: string | null
  createdAt?: string | null; observation?: string; registrationState?: string; reason: string
  items: QueueReviewItem[]
}

export type QueueItem = {
  operationId: string; app: AppItem; state: string; startedAt?: string | null; finishedAt?: string | null
  message?: string; exitCode?: number | null; failureCode?: string | null
}

export type QueueResult = {
  operationId?: string; app: AppItem; state: string; message: string; label?: string
  exitCode?: number | null; failureCode?: string | null
}

export type OperationState = {
  open: boolean; isBatch?: boolean; preview?: boolean; preparingProtection?: boolean
  protectionStage?: string; protectionWarning?: string; canOpenSystemRestore?: boolean; openSystemRestore?: string
  isRunning: boolean; title?: string; status?: string; stage?: string; app?: AppItem | null
  total?: number; processed?: number; percent?: number; successful?: number; partial?: number; failed?: number; skipped?: number
  elapsed?: string; state?: string; stateLabel?: string; operationState?: string; stopRequested?: boolean
  needsReview?: boolean; canCancelCurrent?: boolean; errorDetails?: string; failureCode?: string | null
  exitCode?: number | null; retrying?: boolean; remaining?: number; queueId?: string
  review?: QueueReview; queueItems?: QueueItem[]; logs: string[]; results: QueueResult[]
  residual?: ResidualPage
}

export type UnregisteredFile = { path: string; bytes: number }
export type UnregisteredReview = {
  open: boolean; busy: boolean; state: string; error?: string | null; planId?: string | null
  deletedFiles?: number; deletionTotal?: number | null; remainingFiles?: number; unverifiedMissingFiles?: number
  pendingPath?: string | null; canRetryDelete?: boolean; fromBackup?: boolean; pendingFolder?: string
  app?: AppItem | null; root?: string | null; target?: string | null; kind?: string | null
  bytes: number; count: number; files?: UnregisteredFile[] | null
}

export type ResidualItem = {
  index: number; title: string; path: string; kind?: string; size: string; sizeBytes?: number; reason?: string; canClean: boolean; selected: boolean
}
export type ResidualGroup = { app: AppItem; offset: number; count: number; sizeBytes?: number; status: string; reason?: string; deep?: boolean; locations?: number; coverageIssues?: string[] }
export type ResidualSummary = {
  id: string; name: string; status: string; reason?: string; folder?: string | null; location?: string | null; informational?: string[]
  cleanedCount?: number; reviewOnlyCount?: number; remainingCount?: number
  deep?: boolean; locations?: number; coverageIssues?: string[]
}
export type ResidualPage = {
  open: boolean; cleaning?: boolean; reviewId?: string; selectionVersion?: number; stage?: string
  scannedLocations?: number; size?: string; deletedCount?: number; keptCount?: number; deletedBytes?: number
  deletionAttempted?: boolean; deleteProcessed?: number; deleteTotal?: number; deleteCurrentApp?: string
  deletedRegistryCount?: number; deletedFileBytes?: number; deleteCurrentPath?: string; deleteStopRequested?: boolean
  skippedCount?: number; failedCount?: number; eligibleBytes?: number
  foundSelectable?: boolean; canReview?: boolean; groups?: ResidualGroup[]
  summary: ResidualSummary[]; items: ResidualItem[]; message?: string
}

export type AppsPage = {
  title?: string; total: number; hasPendingQueueReview?: boolean; filtered: number; recent: number; large: number
  store?: number; bundleware?: number | null; bundlewareAvailable?: boolean; logged?: number
  infrequent?: number | null; usageAvailable?: boolean; windowsApps: number; thirdPartyApps: number; selected: number
  isLoading?: boolean; inventoryState?: string; inventoryStage?: string; inventoryError?: string | null
  inventoryRevision?: number; hasConfirmedInventory?: boolean; discoveryDiagnostics?: unknown; status?: string
  category: string; sort: string; search?: string; labels: Record<string, string>; items: AppItem[]
  details?: AppItem | null; confirm?: AppItem | null; unregistered?: UnregisteredReview
  quiet?: boolean; scanResiduals?: boolean; deepResiduals?: boolean; operation: OperationState; residual: ResidualPage
}

export type HistoryPageState = {
  title?: string; description?: string; restorePointLabel?: string; registryBackupLabel?: string
  showTestRecords?: boolean; empty?: boolean; pageNumber?: number; range?: string
  hasOlderPage?: boolean; hasNewerPage?: boolean; labels: Record<string, string>
  records: HistoryItem[]; details?: HistoryItem | null
}

export type ExtensionsPageState = {
  loading: boolean; scanned: boolean; error?: string | null; incomplete?: boolean; diagnosticCount?: number
  browsers?: string[]; items: ExtensionItem[]
}

export type StartupPageState = {
  loading: boolean; scanned: boolean; failed: boolean; failedSources: number
  snapshotId?: string | null
  batchResult?: { enabled: boolean; changed: number; failed: string[] } | null
  items: { id: string; name: string; source: 'Run' | 'StartupFolder'; scope: 'User' | 'Machine'; location: string
    executable?: string | null; icon?: string | null; status?: 'Enabled' | 'Disabled' | 'Unknown'; changeToken?: string | null }[]
}

export type InstallationReport = {
  id: string; root: string; registryScope: string; state: string; startedAt: string; finishedAt?: string
  baselineFiles: number; baselineKeys: number; program?: string; count: number; linkedCount?: number; issues: string[]
  changes: { kind: string; change: string; path: string; at: string; phase: string; association: string; view: number }[]
}

export type State = {
  revision?: number; measurePerformance?: boolean
  language: 'en' | 'ar'; direction: 'ltr' | 'rtl'; reduceMotion: boolean; settingsOpen: boolean; notice: string; noticeIsError: boolean
  page: 'apps' | 'history' | 'extensions' | 'startup' | 'install-monitor'
  activeSidebarItem: string; navigation: Record<string, string>
  queueProtectionSettings?: { enabled: boolean; label: string; hint: string }
  appsPage: AppsPage; historyPage: HistoryPageState; extensionsPage: ExtensionsPageState
  startupPage: StartupPageState
  installationPage?: { busy: boolean; finishing: boolean; cancelling?: boolean; active?: InstallationReport | null; reports: InstallationReport[] } | null
}

export type RequestMessage =
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
