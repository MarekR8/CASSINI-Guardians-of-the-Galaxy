// Valencia presentation: situation view, FlyPix closure, detour and no route.
// Runs only with E2E_SCENARIO=valencia against a fresh stack started with the local Valencia artifacts, e.g.
// python scripts/offline_demo.py --scenario valencia --port 5880 --data <new empty dir>.
import { expect, next, openDashboard, openPresentation, query, statuses, test } from '../fixtures/offline.js';

test.skip(process.env.E2E_SCENARIO !== 'valencia', 'Set E2E_SCENARIO=valencia and run a Valencia stack.');

const EXPECTED = {
  reset: 'done', situation: 'done', alert: 'blocked', 'city-route': 'done', distribution: 'done',
  transport: 'blocked', flypix: 'done', approve: 'done', detour: 'done', 'no-route': 'done',
  link: 'done', ewss: 'done', exit: 'blocked',
};

test('valencia: situation, closure, detour, no route, link and EWSS', async ({ page, request }) => {
  test.setTimeout(300_000);
  const initial = await query(request, 'situation', 'snapshot');
  expect(initial.revision, 'fresh Valencia state required').toBe(0);
  // Routing computes the evacuation plan in the background (503 PlanCalculating until ready); wait from the
  // runner so the page never sees that 503.
  await expect.poll(async () => (await request.get('/api/routing/queries/evacuation-plan')).status(),
    { timeout: 120_000 }).toBe(200);
  await openDashboard(page);
  await expect(page.locator('#map-legend [data-legend]').first()).toBeVisible();
  await openPresentation(page);
  expect(await page.locator('[data-step-id]').evaluateAll(items => items.map(item => item.dataset.stepId)))
    .toEqual(Object.keys(EXPECTED));

  const chips = page.locator('#map .map-status-chip');
  const message = page.locator('.presentation-result .presentation-message').first();
  for (let i = 0; i < Object.keys(EXPECTED).length; i++) {
    const { id, status } = await next(page);
    expect(status, `step ${id}`).toBe(EXPECTED[id]);
    if (status === 'blocked')
      await expect(page.locator(`[data-step-id="${id}"] .presentation-reason`)).toContainText('Oczekuje na');
    if (id === 'situation') {
      await expect(message).toContainText('Obraz sytuacji z rewizji 0');
      await expect(page.locator('.presentation-result')).toContainText('EMSR773');
    }
    if (id === 'city-route') {
      await expect(message).toContainText('Trasa piesza');
      await expect(chips.filter({ hasText: 'Trasa testowa OSM dostępna' })).toHaveClass(/status-current/);
    }
    if (id === 'distribution') {
      await expect(message).toContainText('Wylosowano');
      await expect(page.locator('.presentation-result')).toContainText('SYMULACJA');
      await expect(chips.filter({ hasText: 'Trasa aktywna' })).toBeVisible();
      await expect(page.locator('#map-legend [data-legend="path"]')).toBeVisible();
    }
    if (id === 'flypix') {
      await expect(message).toContainText('Detekcja do weryfikacji');
      await expect(page.locator('.presentation-result')).toContainText('SYMULACJA');
    }
    if (id === 'approve') {
      await expect(message).toContainText('Zatwierdzono zamknięcie');
      expect((await query(request, 'situation', 'snapshot')).closedSegments.length).toBeGreaterThan(0);
    }
    if (id === 'detour') {
      await expect(message).toContainText(/Objazd|Trasa bez zmian/);
      await expect(chips.filter({ hasText: 'Trasa testowa OSM dostępna' })).toBeVisible();
    }
    if (id === 'link') {
      // Profile switch only: no field command is sent in Valencia, so nothing is queued or claimed as delivered.
      await expect(message).toContainText('IRIS²');
      const status = await query(request, 'link', 'link-status');
      expect(status.profile.name).toBe('iris2');
      expect(status.entries).toEqual([]);
    }
    if (id === 'ewss') {
      await expect(message).toContainText('SYMULACJA');
      await expect(page.locator('.presentation-result')).toContainText('EMSR773');
      const { alerts } = await query(request, 'management', 'ewss-alerts');
      expect(alerts.map(item => [item.delivery, item.provenance.mode])).toEqual([['simulated', 'mock']]);
      // The zone is the bounding box of the EMSR773 hazard layer around Valencia.
      const [[west, south], , [east, north]] = alerts[0].zone.coordinates[0];
      expect(west).toBeLessThan(east);
      expect(south).toBeLessThan(north);
      expect(west).toBeGreaterThan(-1);
      expect(east).toBeLessThan(0);
      expect(south).toBeGreaterThan(39);
      expect(north).toBeLessThan(40);
    }
    if (id === 'no-route') {
      await expect(message).toContainText('Brak trasy');
      await expect(chips.filter({ hasText: 'Brak trasy w rewizji' })).toHaveClass(/status-no-route/);
      await expect(chips.filter({ hasText: 'Trasa testowa OSM dostępna' })).toHaveCount(0);
    }
  }
  await expect(page.locator('.presentation-current')).toContainText('Koniec scenariusza');
  expect(await statuses(page)).toEqual(EXPECTED);
});
