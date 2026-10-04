import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createSSRApp, h, type Component } from 'vue';
import { createPinia } from 'pinia';
import { renderToString } from 'vue/server-renderer';
import ResourceMetric from '../components/ResourceMetric.vue';
import MonitoringView from './MonitoringView.vue';
import DashboardOverviewView from './DashboardOverviewView.vue';
import { useAuthStore } from '../stores/auth';

vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
beforeEach(() => { vi.stubGlobal('window', { location: { hostname: 'localhost', pathname: '/panel' } }); vi.stubGlobal('location', { hostname: 'localhost' }); });
afterEach(() => vi.unstubAllGlobals());

it.each([null, Number.NaN, Number.POSITIVE_INFINITY, -1, 101])('does not expose an invalid %s metric as a measured meter', async value => {
  const html = await renderToString(createSSRApp(ResourceMetric, { label: 'CPU', value, detail: '' }));
  expect(html).toContain('panel.monitor.unmeasured');
  expect(html).not.toContain('role="meter"');
  expect(html).not.toContain('aria-valuenow');
  expect(html).not.toContain('0.0%');
});
it('presents an explicitly measured zero as a real meter', async () => {
  const html = await renderToString(createSSRApp(ResourceMetric, { label: 'CPU', value: 0, detail: '' }));
  expect(html).toContain('aria-valuenow="0"');
  expect(html).toContain('0.0%');
  expect(html).not.toContain('panel.monitor.unmeasured');
});
for (const [name, view] of [['monitor', MonitoringView], ['dashboard', DashboardOverviewView]] as const) {
  it.each(['unknown', 'stale', 'zero'])(`${name} renders the actual metric, disk and network templates for %s data`, async state => {
    const measured = state !== 'unknown';
    const overview = { machineName: 'fixture', operatingSystem: 'Windows', osArchitecture: 'x64', frameworkDescription: '.NET', panelVersion: '0.8.77', currentServerTimeUtc: '2026-10-04T00:00:00Z', bootTimeUtc: '2026-10-03T00:00:00Z', uptime: '1.00:00:00', cpuMeasured: measured, memoryMeasured: measured, disksMeasured: measured, networkMeasured: measured, cpuUsagePercent: 0, memoryUsagePercent: 0, logicalProcessorCount: 4, totalMemoryBytes: 1000, availableMemoryBytes: 1000, usedMemoryBytes: 0, disks: [{ name: 'C', label: 'System', format: 'NTFS', measured, usagePercent: 0, freeBytes: 1000, totalBytes: 1000, usedBytes: 0 }], networkInterfaces: [{ name: 'Ethernet', type: 'Ethernet', status: 'Up', ratesMeasured: measured, totalBytesReceived: 0, totalBytesSent: 0, bytesReceivedPerSecond: 0, bytesSentPerSecond: 0 }] };
    const app = createSSRApp({ render: () => h(view as Component, { overview: { ...overview, isRestricted: false }, overviewConfirmed: state !== 'stale', loading: false, sitesCount: 0, totalBindingCount: 0, ftpUserCount: 0, databaseCount: 0, securityStatusLabel: '', securePath: 'panel', auditItems: [], allowedSections: [], panelUpdate: null }) });
    const pinia = createPinia();
    useAuthStore(pinia).session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "a@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
    app.use(pinia);
    const html = await renderToString(app);
    if (state === 'zero') {
      expect(html).toContain('aria-valuenow="0"');
      expect(html).toContain('0.0%');
      expect(html).not.toContain('panel.monitor.partialMeasurement');
    } else if (state === 'unknown') {
      expect(html).toContain('panel.monitor.partialMeasurement');
      expect(html).toContain('panel.monitor.unmeasured');
      expect(html).not.toContain('0.0%');
      expect(html).not.toContain('aria-valuenow="0"');
    } else {
      expect(html).toContain('panel.monitor.unmeasured');
      expect(html).toContain('panel.common.retry');
      expect(html).not.toContain('0.0%');
      expect(html).not.toContain('aria-valuenow="0"');
    }
  });
}
