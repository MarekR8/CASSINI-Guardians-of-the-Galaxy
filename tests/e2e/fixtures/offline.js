// Shared fixture: every test runs offline and clean.
// - Requests to any host other than localhost/127.0.0.1 are aborted and recorded; the list must stay empty.
// - Console errors, page errors, CSP violations and failed requests (HTTP >= 400) fail the test.
import { test as base, expect } from '@playwright/test';

const LOCAL_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]']);

export const test = base.extend({
  guard: [async ({ context }, use) => {
    const external = [];
    const problems = [];
    const note = (text, url = '') => problems.push(text + (url ? ' @ ' + url : ''));

    await context.route(url => !LOCAL_HOSTS.has(url.hostname) && !['data:', 'blob:'].includes(url.protocol), route => {
      external.push(route.request().method() + ' ' + route.request().url());
      return route.abort('blockedbyclient');
    });
    await context.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', event => {
        console.error('CSP violation: ' + event.violatedDirective + ' ' + event.blockedURI);
      });
    });
    context.on('console', message => {
      if (message.type() !== 'error') return;
      // Chromium reports a failed fetch as "Failed to load resource" with the URL only in the location.
      note('console: ' + message.text(), message.location()?.url ?? '');
    });
    context.on('weberror', error => problems.push('pageerror: ' + (error.error().stack ?? error.error().message)));
    context.on('response', response => {
      if (response.status() >= 400) note('HTTP ' + response.status() + ' ' + response.request().method(), response.url());
    });
    context.on('dialog', dialog => {
      problems.push('dialog: ' + dialog.message());
      return dialog.dismiss();
    });

    await use({ external, problems });

    expect(external, 'requests to non-local hosts').toEqual([]);
    expect(problems, 'console errors, page errors, CSP violations or failed requests').toEqual([]);
  }, { auto: true }],
});

export { expect };

// Reads a query through the Gateway from the test runner (not the page), so it is not recorded by the guard.
export async function query(request, service, path) {
  const response = await request.get('/api/' + service + '/queries/' + path);
  expect(response.ok(), 'GET ' + service + '/' + path).toBeTruthy();
  return response.json();
}

// Opens the dashboard and waits until the presentation panel has read the scenario.
export async function openDashboard(page) {
  await page.goto('/');
  await expect(page.locator('#map.leaflet-container')).toBeVisible();
  await expect(page.locator('#presentation-toggle')).toBeEnabled({ timeout: 60_000 });
}

export async function openPresentation(page) {
  await page.locator('#presentation-toggle').click();
  await expect(page.locator('#presentation-next')).toBeVisible();
}

// Clicks #presentation-next once and waits until the step finished: the cursor moved on, or the step failed.
export async function next(page) {
  const id = await page.locator('.presentation-step.current').getAttribute('data-step-id');
  await expect(page.locator('#presentation-next')).toBeEnabled();
  await page.locator('#presentation-next').click();
  await page.waitForFunction(stepId => {
    const current = document.querySelector('.presentation-step.current');
    const step = document.querySelector(`[data-step-id="${stepId}"]`);
    if (step?.dataset.stepStatus === 'running') return false;
    return current?.dataset.stepId !== stepId || step?.dataset.stepStatus === 'error';
  }, id, { timeout: 90_000 });
  return { id, status: await page.locator(`[data-step-id="${id}"]`).getAttribute('data-step-status') };
}

export const statuses = page => page.evaluate(() =>
  Object.fromEntries(window.cassiniPresentation.steps().map(step => [step.id, step.status])));
