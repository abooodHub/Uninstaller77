export const hostOperations = [
  'getState', 'navigate', 'refresh', 'search', 'sort', 'category',
  'closeUnregistered', 'confirmUnregistered', 'retryUnregisteredDelete', 'reviewPendingDeletion',
  'selectAll', 'selectApp', 'openDetails', 'closeDetails', 'promptUninstall',
  'cancelConfirm', 'setUninstallOptions', 'confirmUninstall', 'cancelProgress',
  'closeProgress', 'stopBatch', 'runBatch', 'selectResidual', 'selectAllResiduals',
  'deselectAllResiduals', 'skipResiduals', 'deleteReviewedResiduals', 'restoreQuarantine',
  'setLanguage', 'setQueueProtection', 'openSystemRestore', 'loadHistory', 'changeHistoryPage', 'setShowTestRecords',
  'openHistoryDetails', 'closeHistoryDetails', 'clearHistory', 'openSettings', 'closeSettings', 'openProductWebsite',
  'scanExtensions', 'exportApps',
  'refreshControlCenter', 'openControlFolder', 'openWindowsAppsSettings', 'exportSupportReport',
  'retryQueueVerification', 'reviewQueue', 'openQueueReview', 'acknowledgeQueueReview', 'inventoryRendered', 'performanceMark'
] as const

export type HostOperation = typeof hostOperations[number]
export type HostAction = (operation: HostOperation, payload?: Record<string, unknown>) => Promise<boolean>
