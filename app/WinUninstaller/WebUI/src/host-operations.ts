export const hostOperations = [
  'getState', 'navigate', 'refresh', 'search', 'sort', 'category',
  'reviewProgramFile', 'closeUnregistered', 'confirmUnregistered', 'retryUnregisteredDelete', 'reviewPendingDeletion',
  'startInstallationRecording', 'finishInstallationRecording', 'cancelInstallationRecording', 'exportInstallationRecording',
  'selectAll', 'selectApp', 'openDetails', 'closeDetails', 'promptUninstall',
  'cancelConfirm', 'setUninstallOptions', 'confirmUninstall', 'cancelProgress',
  'closeProgress', 'stopBatch', 'runBatch', 'openResidualReview', 'selectResidual', 'selectAllResiduals',
  'deselectAllResiduals', 'selectResidualGroup', 'skipResiduals', 'stopResidualCleanup', 'deleteReviewedResiduals', 'restoreQuarantine',
  'setLanguage', 'setReduceMotion', 'setQueueProtection', 'openSystemRestore', 'loadHistory', 'changeHistoryPage', 'setShowTestRecords',
  'openHistoryDetails', 'closeHistoryDetails', 'rescanHistoryResiduals', 'clearHistory', 'openSettings', 'closeSettings', 'openProductWebsite',
  'scanExtensions', 'openExtensionManager', 'scanStartupApps', 'setStartupEnabled', 'setStartupBatchEnabled', 'openStartupSettings', 'exportApps',
  'refreshControlCenter', 'openControlFolder', 'openWindowsAppsSettings', 'exportSupportReport',
  'retryQueueVerification', 'reviewQueue', 'openQueueReview', 'acknowledgeQueueReview', 'inventoryRendered', 'performanceMark'
] as const

export type HostOperation = typeof hostOperations[number]
export type HostAction = (operation: HostOperation, payload?: Record<string, unknown>) => Promise<boolean>
