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

// Task 6 link simulator in the Gateway (contracts/gateway/README.md, src/Gateway/Link/):
//   GET  /api/link/queries/link-status  -> { profile: { name, available, roundTripMs, throughputKbps, basis },
//                                            profileChangedAt, pending, entries: [{ entryId, service, operation,
//                                            commandId, state, attempts, deliveredOver, status, ... }], profiles }
//   POST /api/link/commands/link-profile { commandId, profile: terrestrial | iris2 | govsatcom | down }
//   A command sent with X-Cassini-Link: field travels over the simulated link; while it is down the Gateway
//   answers 202 { status: 'Queued', entry }, which is not a delivery confirmation.
const PROFILES = { terrestrial: 'naziemne', iris2: 'IRIS² (symulacja)', govsatcom: 'GOVSATCOM (symulacja)', down: 'brak łącza' };
const ENTRY_STATES = { Queued: 'w kolejce, bez potwierdzenia dostawy', Delivered: 'dostarczona do usługi',
  Rejected: 'odrzucona przez usługę' };

const describeLink = status => 'Łącze: ' + (PROFILES[status.profile?.name] ?? status.profile?.name ?? '—') +
  (status.profile?.available ? ' · RTT ' + status.profile.roundTripMs + ' ms' : '') +
  ' · w kolejce: ' + (status.pending ?? '—') +
  (status.profileChangedAt ? ' · od ' + new Date(status.profileChangedAt).toISOString().slice(11, 19) + ' UTC' : '');

const sameEntry = (field, commandId) => item =>
  item.commandId === commandId && item.service === field.service && item.operation === field.operation;

async function waitForEntry(ctx, field, commandId, timeout = 15_000) {
  const until = Date.now() + timeout;
  let status = await ctx.read('link', 'link-status');
  for (;;) {
    const entry = status.entries?.findLast(sameEntry(field, commandId)) ?? null;
    if (!entry || entry.state !== 'Queued' || Date.now() > until) return { status, entry };
    await new Promise(resolve => setTimeout(resolve, 500));
    status = await ctx.read('link', 'link-status');
  }
}

// field: optional { name, service, operation, build, count?(ctx) -> { label, value } }, a field command sent while
// the link is down. count() reads the owner's records before and after, to show that it was delivered once.
export function linkStep(field = null) {
  return {
    id: 'link',
    title: 'Utrata łącza naziemnego → IRIS² (symulacja)',
    summary: 'Łącze naziemne przestaje działać, punkt koordynacji przełącza się na IRIS² (symulacja w Gateway, ' +
      'brak publicznego API IRIS²). ' + (field
      ? 'Komenda z terenu czeka w kolejce (HTTP 202, bez potwierdzenia dostawy) i trafia do usługi po przełączeniu, ' +
        'z tym samym commandId, bez duplikatów.'
      : 'W tym scenariuszu nie wysyłamy komendy z terenu (usługi spraw nie mają danych miasta); panel pokazuje ' +
        'profil łącza i kolejkę zgłoszone przez symulator.'),
    async run(ctx) {
      const before = field?.count ? await field.count(ctx) : null;
      const down = await ctx.command('down', 'link', 'link-profile', async () => ({ profile: 'down' }));
      const details = ['Po zerwaniu: ' + describeLink(down)];
      let sent = null;
      if (field) {
        const options = { headers: { 'X-Cassini-Link': 'field' } };
        const answer = await ctx.command(field.name, field.service, field.operation, field.build, options);
        sent = { commandId: ctx.commandIdOf(field.name), queued: answer?.status === 'Queued' };
        details.push(field.service + '/' + field.operation + ' z terenu: ' + (sent.queued
          ? 'HTTP 202 Queued (' + answer.entry.entryId + ') — brak potwierdzenia dostawy'
          : 'odpowiedź usługi (komenda dostarczona wcześniej, zapisany wynik)'));
        if (sent.queued) {
          // A retry from the field device with the same commandId must not queue a second copy.
          const retry = await ctx.command(field.name, field.service, field.operation, field.build, options);
          details.push('Ponowienie z tym samym commandId: ' + (retry?.entry?.entryId === answer.entry.entryId
            ? 'ten sam wpis kolejki (' + retry.entry.entryId + ')' : 'odpowiedź ' + (retry?.status ?? '—')));
        }
      }
      await ctx.command('iris2', 'link', 'link-profile', async () => ({ profile: 'iris2' }));
      const { status, entry } = sent
        ? await waitForEntry(ctx, field, sent.commandId) : { status: await ctx.read('link', 'link-status') };
      details.push('Po przełączeniu: ' + describeLink(status));
      if (entry) details.push('Komenda ' + entry.commandId + ': ' + (ENTRY_STATES[entry.state] ?? entry.state) +
        (entry.deliveredOver ? ' · przez ' + (PROFILES[entry.deliveredOver] ?? entry.deliveredOver) : '') +
        (entry.status ? ' · HTTP ' + entry.status : '') + ' · próby: ' + entry.attempts);
      const delivered = entry?.state === 'Delivered';
      if (before && delivered) {
        const after = await field.count(ctx);
        details.push(after.label + ': ' + before.value + ' → ' + after.value +
          (after.value - before.value === 1 ? ' (jedna dostawa, bez duplikatu)' : ''));
      }
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

// Galileo EWSS has no public broadcast interface: Management stores the message through its mock and labels it
// delivery "simulated", provenance mode "mock" (src/Management/Ewss.cs). The zone is the bounding box of the
// exercise area as one closed GeoJSON ring. zone(ctx) -> { bounds: [w, s, e, n], source }.
const EWSS_DELIVERY = { simulated: 'nadanie symulowane, brak potwierdzenia odbioru przez telefony' };
const round = value => Math.round(value * 10_000) / 10_000;

export function boxPolygon([west, south, east, north]) {
  const [w, s, e, n] = [round(west), round(south), round(east), round(north)];
  return { type: 'Polygon', coordinates: [[[w, s], [e, s], [e, n], [w, n], [w, s]]] };
}

const ewssMode = item => item.provenance?.mode === 'mock' ? 'SYMULACJA (mock ' + item.provenance.source + ')'
  : item.provenance?.mode ?? '—';

export function ewssStep({ text, zone, area }) {
  return {
    id: 'ewss',
    title: 'Komunikat EWSS (SYMULACJA nadania)',
    summary: 'Koordynator nadaje krótki komunikat ostrzegawczy dla strefy (' + area + '). Galileo EWSS nie ma ' +
      'publicznego interfejsu nadawczego, więc Management zapisuje komunikat przez mock i oznacza go jako symulację.',
    async run(ctx) {
      let source = null;
      const broadcast = await ctx.command('broadcast', 'management', 'ewss-alert', async () => {
        const picked = await zone(ctx);
        if (!picked?.bounds) throw new Error('Brak granic strefy do komunikatu EWSS.');
        source = picked.source;
        return { level: 'severe', text, zone: boxPolygon(picked.bounds) };
      });
      const ring = broadcast.zone.coordinates[0];
      const bounds = [ring[0][0], ring[0][1], ring[2][0], ring[2][1]];
      const { alerts } = await ctx.read('management', 'ewss-alerts');
      return {
        message: 'Komunikat ' + broadcast.broadcastId + ' (' + broadcast.level + '): ' + ewssMode(broadcast) + ', ' +
          (EWSS_DELIVERY[broadcast.delivery] ?? 'dostawa: ' + broadcast.delivery) + '.',
        details: [
          '„' + broadcast.text + '”',
          'Strefa: prostokąt ' + bounds.join(', ') + (source ? ' · ' + source : ''),
          'Nadano ' + new Date(broadcast.issuedAt).toISOString().slice(11, 19) + ' UTC · provenance ' +
            broadcast.provenance.source + ' / ' + broadcast.provenance.mode + ' · delivery ' + broadcast.delivery,
          'Komunikaty EWSS w Management: ' + alerts.length + ' (' +
            alerts.map(item => item.broadcastId + ' ' + ewssMode(item)).join(', ') + ')',
        ],
        focus: bounds,
      };
    },
  };
}
