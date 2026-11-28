// Shared building blocks for the presentation step lists (S4).
// A step is { id, title, summary, blocked?, probe?, run?(ctx) }. run() returns
// { message, details?, focus? } where focus is bounds [w, s, e, n] or a function evaluated after the refresh.
import { api } from '../api.js';
import { geometryBounds } from '../map.js';

export const read = (service, path) => api(service, 'queries/' + path);
export const revision = async () => (await read('situation', 'snapshot')).revision;

export function lineBounds(points) {
  return points?.length ? geometryBounds({ type: 'LineString', coordinates: points }) : null;
}

export function pointBounds(point, delta = .002) {
  return point ? [point[0] - delta, point[1] - delta, point[0] + delta, point[1] + delta] : null;
}

export function mergeBounds(...items) {
  const boxes = items.filter(Boolean);
  if (!boxes.length) return null;
  return [Math.min(...boxes.map(b => b[0])), Math.min(...boxes.map(b => b[1])),
    Math.max(...boxes.map(b => b[2])), Math.max(...boxes.map(b => b[3]))];
}

export function hazardBounds(snapshot) {
  return mergeBounds(...(snapshot?.hazards ?? []).map(hazard => geometryBounds(hazard.geometry)));
}

export function populationLine(snapshot) {
  const estimate = snapshot?.populationEstimate;
  if (!estimate) return null;
  return 'Szacunek ludności w strefie: ' + estimate.value.toLocaleString('pl-PL') + ' os. · ' +
    estimate.source + ' ' + estimate.year + ' · ' + estimate.method;
}

export function hazardLines(snapshot) {
  return (snapshot?.hazards ?? []).map(hazard => [hazard.productId ?? 'Zalanie ćwiczeniowe', hazard.source,
    hazard.provenance?.mode === 'mock' || hazard.provenance?.mode === 'synthetic' ? 'SYMULACJA' : hazard.provenance?.mode, hazard.observedAt]
    .filter(Boolean).join(' · '));
}

export function blocked(id, title, summary, reason) {
  return { id, title, summary, blocked: reason };
}

export function resetStep(scenario) {
  return {
    id: 'reset',
    title: 'Krok 0 · Świeży stan ćwiczenia',
    summary: 'Usługi nie mają komendy resetu. Aby zacząć od zera: docker compose down -v, a potem docker compose up -d ' +
      '(albo python scripts/offline_demo.py z nowym katalogiem --data). Następnie przeładuj stronę — ' +
      'identyfikatory komend prezentacji są trzymane tylko w pamięci tej karty.',
    async run(ctx) {
      const snapshot = await ctx.read('situation', 'snapshot');
      const incident = scenario === 'valencia' ? null : await ctx.read('management', 'incident');
      const fresh = snapshot.revision === 0 && !snapshot.closedSegments.length &&
        !(snapshot.reviewFeatures ?? []).some(feature => feature.layerId?.startsWith('presentation-')) &&
        (!incident || incident.alerts.length === 0);
      return {
        message: fresh
          ? 'Stan świeży: rewizja sytuacji 0, brak zamknięć' + (incident ? ' i zgłoszeń.' : '.')
          : 'Stan po wcześniejszych operacjach (rewizja ' + snapshot.revision + '). Kroki mogą zwrócić ' +
            'zapisane wcześniej wyniki lub konflikty. Dla czystego przebiegu wykonaj reset opisany wyżej.',
        details: ['Rewizja sytuacji: ' + snapshot.revision, 'Zamknięte odcinki: ' + snapshot.closedSegments.length],
      };
    },
  };
}

// Task 6 link simulator in the Gateway (contracts/gateway/README.md on its branch):
//   GET  /api/link/queries/link-status  -> { profile: { name, available, roundTripMs, throughputKbps, basis },
//                                            profileChangedAt, pending, entries: [{ commandId, state, ... }], profiles }
//   POST /api/link/commands/link-profile { commandId, profile: terrestrial | iris2 | govsatcom | down }
//   A command sent with X-Cassini-Link: field travels over the simulated link; while it is down the Gateway
//   answers 202 { status: 'Queued' }, which is not a delivery confirmation.
// Until the query answers (task 6 not merged) the step stays blocked and sends nothing.
export const LINK_STATUS = '/api/link/queries/link-status';
const PROFILES = { terrestrial: 'naziemne', iris2: 'IRIS² (symulacja)', govsatcom: 'GOVSATCOM (symulacja)', down: 'brak łącza' };
const ENTRY_STATES = { Queued: 'w kolejce, bez potwierdzenia dostawy', Delivered: 'dostarczona do usługi',
  Rejected: 'odrzucona przez usługę' };

const describeLink = status => 'Łącze: ' + (PROFILES[status.profile?.name] ?? status.profile?.name ?? '—') +
  (status.profile?.available ? ' · RTT ' + status.profile.roundTripMs + ' ms' : '') +
  ' · w kolejce: ' + (status.pending ?? '—') +
  (status.profileChangedAt ? ' · od ' + new Date(status.profileChangedAt).toISOString().slice(11, 19) + ' UTC' : '');

async function waitForEntry(ctx, commandId, timeout = 15_000) {
  const until = Date.now() + timeout;
  let status = await ctx.read('link', 'link-status');
  for (;;) {
    const entry = status.entries?.find(item => item.commandId === commandId) ?? null;
    if (!entry || entry.state !== 'Queued' || Date.now() > until) return { status, entry };
    await new Promise(resolve => setTimeout(resolve, 500));
    status = await ctx.read('link', 'link-status');
  }
}

// field: optional { name, service, operation, build } — a field command queued while the link is down.
export function linkStep(field = null) {
  const waiting = 'Oczekuje na task 6 — symulator łącza (GET ' + LINK_STATUS + ') jeszcze nie odpowiada.';
  return {
    id: 'link',
    title: 'Utrata łącza naziemnego → IRIS² (symulacja)',
    summary: 'Łącze naziemne przestaje działać, punkt koordynacji przełącza się na IRIS² (symulacja). ' +
      (field ? 'Komenda z terenu czeka w kolejce i trafia do usługi po przełączeniu, z tym samym commandId, bez duplikatów.'
        : 'Panel pokazuje profil łącza i kolejkę zgłoszone przez symulator.'),
    blocked: waiting,
    async probe() {
      try {
        const response = await fetch(LINK_STATUS, { cache: 'no-store' });
        return response.ok ? null : waiting + ' (HTTP ' + response.status + ')';
      } catch {
        return waiting;
      }
    },
    async run(ctx) {
      const down = await ctx.command('down', 'link', 'link-profile', async () => ({ profile: 'down' }));
      const details = ['Po zerwaniu: ' + describeLink(down)];
      let sent = null;
      if (field) {
        const answer = await ctx.command(field.name, field.service, field.operation, field.build,
          { headers: { 'X-Cassini-Link': 'field' } });
        sent = { commandId: ctx.commandIdOf(field.name), queued: answer?.status === 'Queued' };
        details.push(field.service + '/' + field.operation + ' z terenu: ' + (sent.queued
          ? 'HTTP 202 Queued — brak potwierdzenia dostawy' : 'odpowiedź usługi (zapisany wynik)'));
      }
      await ctx.command('iris2', 'link', 'link-profile', async () => ({ profile: 'iris2' }));
      const { status, entry } = sent ? await waitForEntry(ctx, sent.commandId) : { status: await ctx.read('link', 'link-status') };
      details.push('Po przełączeniu: ' + describeLink(status));
      if (entry) details.push('Komenda ' + entry.commandId + ': ' + (ENTRY_STATES[entry.state] ?? entry.state) +
        (entry.deliveredOver ? ' · przez ' + (PROFILES[entry.deliveredOver] ?? entry.deliveredOver) : '') +
        ' · próby: ' + entry.attempts);
      const delivered = entry?.state === 'Delivered';
      return {
        tone: sent && !delivered ? 'warning' : null,
        message: describeLink(status) + '. ' + (!sent ? 'Stan zgłoszony przez symulator łącza.'
          : delivered ? 'Kolejka opróżniona: komenda dostarczona po przełączeniu (stan z symulatora łącza).'
            : 'Komenda nadal bez potwierdzenia dostawy — brak potwierdzenia oznacza brak dostawy.'),
        details,
      };
    },
  };
}

export const ewssStep = () => blocked('ewss', 'Komunikat EWSS',
  'Krótki komunikat ostrzegawczy nadany przez EWSS.',
  'Oczekuje na task 6 — brak usługi nadawania EWSS (Android ma tylko mock, task 5). Krok nie jest symulowany w panelu.');
