export type AppItem = {
  id: string; name: string; publisher: string; version: string; size: string; sizeKb: number
  installDate: string; architecture: string; installerType: string; source: string
  icon: string | null; selected: boolean; installLocation: string; registryKey: string
  uninstallCommand: string; quietUninstallCommand: string
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
  success: boolean; outcome?: string; status: string; residualStatus: string; exitCode: number | null; icon: string | null; notes: string; test: boolean
  cleanedItemsCount: number; cleanedSizeBytes: number
}

export type State = {
  revision?: number; measurePerformance?: boolean
  language: 'en' | 'ar'; direction: 'ltr' | 'rtl'; reduceMotion: boolean; settingsOpen: boolean; notice: string; noticeIsError: boolean
  page: 'apps' | 'history' | 'extensions' | 'control'
  activeSidebarItem: string; navigation: Record<string, string>
  queueProtectionSettings?: { enabled: boolean; label: string; hint: string }
  appsPage: any; historyPage: any; extensionsPage: any; controlPage?: any
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
