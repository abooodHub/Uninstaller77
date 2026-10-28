import test from 'node:test'
import assert from 'node:assert/strict'
import { extensionManagementCapabilities, healthOverallStatus, shouldAnimateHealthScan } from './presentation.ts'
test('Phase14 browser manager exposes only identity-checked browser fallback, never direct mutation', () => {
  for (const browser of ['Google Chrome', 'Microsoft Edge', 'Brave']) {
    const capability = extensionManagementCapabilities({ browser, profile: 'Default', id: 'a'.repeat(32), installationLocation: 'C:\\fixture\\extension' })
    assert.equal(capability.enabledState, 'Unknown')
    assert.equal(capability.canManageInBrowser, true)
    assert.equal(capability.canEnable || capability.canDisable || capability.canRemove || capability.directManagement, false)
  }
  assert.equal(extensionManagementCapabilities({ browser: 'Unknown Browser', profile: 'Default', id: 'a'.repeat(32), installationLocation: 'C:\\fixture' }).canManageInBrowser, false)
  assert.equal(extensionManagementCapabilities({ browser: 'Google Chrome', profile: '..\\Default', id: 'a'.repeat(32), installationLocation: 'C:\\fixture' }).canManageInBrowser, false)
  assert.equal(extensionManagementCapabilities({ browser: 'Google Chrome', profile: 'Default', id: 'z'.repeat(32), installationLocation: 'C:\\fixture' }).canManageInBrowser, false)
})

test('Phase12 overall health never reports healthy with unsupported, unscanned, failed or cancelled checks', () => {
  assert.equal(healthOverallStatus({ supportedCompleted: 1, supportedTotal: 1, unsupported: 8, findings: 0, errors: 0, scanned: true }), 'ScanIncomplete')
  assert.equal(healthOverallStatus({ supportedCompleted: 0, supportedTotal: 1, unsupported: 0, findings: 0, errors: 0 }), 'NotScanned')
  assert.equal(healthOverallStatus({ supportedCompleted: 1, supportedTotal: 1, unsupported: 0, findings: 1, errors: 0, scanned: true }), 'AttentionRequired')
  assert.equal(healthOverallStatus({ supportedCompleted: 1, supportedTotal: 1, unsupported: 0, findings: 0, errors: 1, scanned: true }), 'ScanIncomplete')
  assert.equal(healthOverallStatus({ supportedCompleted: 1, supportedTotal: 1, unsupported: 0, findings: 0, errors: 0, cancelled: true, scanned: true }), 'Cancelled')
  assert.equal(healthOverallStatus({ supportedCompleted: 1, supportedTotal: 1, unsupported: 0, findings: 0, errors: 0, scanned: true }), 'Healthy')
})

test('Phase12 reduce motion disables scan animations for both saved and system preferences', () => {
  assert.equal(shouldAnimateHealthScan(false, false), true)
  assert.equal(shouldAnimateHealthScan(true, false), false)
  assert.equal(shouldAnimateHealthScan(false, true), false)
})

