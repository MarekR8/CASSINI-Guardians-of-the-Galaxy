// End-to-end run of the synthetic `sample` exercise (Kłodzko) through presentation mode.
// CI has no Valencia artifacts, so this is the scenario the pipeline checks. Needs a fresh stack
// (docker compose down -v && docker compose up -d --wait), because the services have no reset command.
import { expect, linkAvailable, next, openDashboard, openPresentation, query, statuses, test } from '../fixtures/offline.js';

test.skip(process.env.E2E_SCENARIO === 'valencia', 'Stack runs the Valencia scenario.');

const EXPECTED = {
  reset: 'done', alert: 'done', draw: 'done', assist: 'done', closure: 'done', replan: 'done',
  'unit-progress': 'done', link: 'blocked', ewss: 'blocked', exit: 'done', 'no-exit': 'done',
};
// Steps replayed with the same commandIds right after they ran; the read model must not change.
const REPLAY = ['alert', 'assist', 'closure'];

async function counts(request) {
  const [incident, dispatch, snapshot] = await Promise.all([query(request, 'management', 'incident'),
    query(request, 'dispatch', 'assignments'), query(request, 'situation', 'snapshot')]);
  return { alerts: incident.alerts.length, requests: dispatch.requests.length, assignments: dispatch.assignments.length,
    revision: snapshot.revision, closed: snapshot.closedSegments.length };
}

test('sample exercise: map, legend, presentation, route states, metrics and idempotent replay', async ({ page, request }) => {
  const initial = await query(request, 'situation', 'snapshot');
  expect(initial.revision, 'fresh stack required: docker compose down -v, then up -d --wait').toBe(0);
  // With task 6 merged the link step runs and its field alert is delivered after the switch to IRIS².
  const link = await linkAvailable(request);
  EXPECTED.link = link ? 'done' : 'blocked';
  await openDashboard(page);

  // Map (Leaflet) renders the exercise network as vector paths.
  await expect(page.locator('#map .leaflet-pane').first()).toBeAttached();
  await expect.poll(() => page.locator('#map path').count()).toBeGreaterThan(0);

  // Legend: entries with provenance; exercise layers carry SYMULACJA, local files do not.
  const legend = page.locator('#map-legend');
  await expect(legend.locator('[data-legend]').first()).toBeVisible();
  const segments = legend.locator('[data-legend="segments"]');
  await expect(segments.locator('.badge-simulation')).toHaveText('SYMULACJA');
  await expect(segments.locator('.legend-provenance').first()).not.toBeEmpty();
  await expect(segments.locator('[data-legend-toggle="segments"]')).toBeChecked();
  const files = legend.locator('[data-legend]').filter({ has: page.locator('.badge-file') });
  await expect(files.first()).toBeVisible();
  await expect(files.locator('.badge-simulation')).toHaveCount(0);

  await openPresentation(page);
  const ids = await page.locator('[data-step-id]').evaluateAll(items => items.map(item => item.dataset.stepId));
  expect(ids).toEqual(Object.keys(EXPECTED));

  const chips = page.locator('#map .map-status-chip');
  for (let i = 0; i < ids.length; i++) {
    const { id, status } = await next(page);
    expect(status, `step ${id}`).toBe(EXPECTED[id]);
    const message = page.locator('.presentation-result .presentation-message').first();

    if (id === 'reset') await expect(page.locator('.presentation-result')).toContainText('Stan świeży');
    if (id === 'alert') {
      const hazard = legend.locator('[data-legend="exercise-hazard"]');
      await expect(hazard.locator('.badge-simulation')).toHaveText('SYMULACJA');
      await expect(hazard.locator('.legend-provenance')).toContainText('symulacja');
    }
    if (id === 'draw') {
      // State 1: route available.
      await expect(chips.filter({ hasText: 'Trasa aktywna' })).toHaveClass(/status-current/);
      await expect(page.locator('#draw-result')).toContainText('Wylosowano');
    }
    if (id === 'closure') {
      // State 2: bridge closed, the drawn route is withdrawn (not "no route").
      await expect(message).toContainText('Most zamknięty');
      await expect(chips.filter({ hasText: 'Trasa wycofana po zmianie dostępności' })).toHaveClass(/status-withdrawn/);
      await expect(page.locator('#draw-result')).toContainText('Trasa wycofana');
      await expect(legend.locator('[data-legend="withdrawn"]')).toBeVisible();
      await expect(chips.filter({ hasText: 'Brak trasy' })).toHaveCount(0);
    }
    if (id === 'replan') await expect(chips.filter({ hasText: 'Trasa aktywna' })).toBeVisible();
    if (id === 'link' && link)
      await expect(page.locator('.presentation-result')).toContainText('IRIS²');
    if ((id === 'link' && !link) || id === 'ewss')
      await expect(page.locator(`[data-step-id="${id}"] .presentation-reason`)).toContainText('Oczekuje na task 6');
    if (id === 'no-exit') {
      // State 3: no exit from the node, shown separately from the withdrawn route.
      await expect(message).toContainText('Brak wyjścia');
      const noRoute = chips.filter({ hasText: 'Brak trasy' });
      await expect(noRoute).toHaveClass(/status-no-route/);
      await expect(noRoute).not.toHaveClass(/status-withdrawn/);
      await expect(legend.locator('[data-legend="no-route"]')).toBeVisible();
    }

    if (REPLAY.includes(id)) {
      const before = await counts(request);
      const sent = await page.evaluate(() => window.cassiniPresentation.commands());
      await page.locator('#presentation-replay').click();
      await expect(page.locator('#presentation-replay')).toBeEnabled({ timeout: 60_000 });
      await expect(page.locator(`[data-step-id="${id}"]`)).toHaveAttribute('data-step-status', 'done');
      await expect(page.locator('.presentation-result .presentation-note')).toContainText('Ponowiono z tymi samymi commandId');
      expect(await counts(request), `replay of ${id} must not duplicate`).toEqual(before);
      const after = await page.evaluate(() => window.cassiniPresentation.commands());
      const keys = Object.keys(after).filter(key => key.startsWith(id + ':'));
      expect(keys.length, `commands of ${id}`).toBeGreaterThan(0);
      for (const key of keys) {
        expect(after[key].commandId, key).toBe(sent[key].commandId);
        expect(after[key].attempts, key).toBe(sent[key].attempts + 1);
      }
    }
  }

  // Finished: no current step, "Dalej" disabled, final statuses as expected.
  await expect(page.locator('.presentation-step.current')).toHaveCount(0);
  await expect(page.locator('#presentation-next')).toBeDisabled();
  await expect(page.locator('.presentation-current')).toContainText('Koniec scenariusza');
  expect(await statuses(page)).toEqual(EXPECTED);
  expect(await counts(request)).toMatchObject({ alerts: link ? 2 : 1, requests: 2, assignments: 1, closed: 2 });

  // Metrics 1–4. Metric 2 has no value in this flow: Dispatch seeds request-15 before the page opens,
  // so the panel states why instead of estimating.
  const tiles = page.locator('#metrics-panel article.metric');
  await expect(tiles).toHaveCount(4);
  const values = tiles.locator('.metric-value');
  await expect(tiles.nth(0)).toContainText('Zgłoszenie');
  await expect(values.nth(0)).toHaveText(/\d.*s$/);
  await expect(tiles.nth(1)).toContainText(/Czas przydziału pomocy[\s\S]*(\d.*s|prośba sprzed otwarcia panelu)/);
  await expect(tiles.nth(2)).toContainText('Zamknięcie');
  await expect(values.nth(2)).toHaveText(/\d.*s$/);
  await expect(tiles.nth(3)).toContainText('bez potwierdzonego wyjścia');
  await expect(values.nth(3)).toHaveText('2 z 3');
  for (const tile of await tiles.all()) await expect(tile.locator('.metric-source')).not.toBeEmpty();
});
