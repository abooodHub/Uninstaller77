import type { BrowserExtension } from '../../../app/WinUninstaller/WebUI/src/presentation.ts'
/** Local manifest inventory is read-only. Only open a browser UI for a validated identity. */
export function extensionManagementCapabilities(item: BrowserExtension) {
  const browserSupported = ['Google Chrome', 'Microsoft Edge', 'Brave'].includes(item.browser)
  const identityValid = /^[a-p]{32}$/.test(item.id) &&
    (item.profile === 'Default' || /^Profile [1-9][0-9]{0,2}$/.test(item.profile)) &&
    Boolean(item.installationLocation)
  return {
    enabledState: 'Unknown' as const,
    directManagement: false,
    canEnable: false,
    canDisable: false,
    canRemove: false,
    canManageInBrowser: browserSupported && identityValid,
    policyManaged: Boolean(item.isPolicyManaged)
  }
}

export function healthOverallStatus(input: { supportedCompleted: number; supportedTotal: number; unsupported: number; findings: number; errors: number; cancelled?: boolean; scanning?: boolean; scanned?: boolean }) {
  if (input.scanning) return 'Running'
  if (!input.scanned) return 'NotScanned'
  if (input.cancelled) return 'Cancelled'
  if (input.findings > 0) return 'AttentionRequired'
  if (input.errors > 0 || input.supportedCompleted < input.supportedTotal || input.unsupported > 0) return 'ScanIncomplete'
  return 'Healthy'
}

export function shouldAnimateHealthScan(reduceMotion: boolean, prefersReducedMotion: boolean) {
  return !reduceMotion && !prefersReducedMotion
}

