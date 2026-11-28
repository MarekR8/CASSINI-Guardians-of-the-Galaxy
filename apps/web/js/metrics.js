// S3: metrics panel (#metrics-panel). Values come either from service timestamps ("z danych serwisu")
// or from moments this browser observed through onCommand/subscribe ("pomiar sesji"). Nothing is estimated.
import { $, element } from './dom.js';
import { onCommand, state, subscribe } from './state.js';

const SERVER = 'z danych serwisu';
const SESSION = 'pomiar sesji';
const MIXED = 'dane serwisu → pomiar sesji';
const UNAVAILABLE = 'niedostępne w tym scenariuszu';
const WITHDRAWN = ['Blocked', 'ReviewRequired'];

// Session measurements live in memory for the lifetime of the page; refreshes never clear them.
let session = freshSession();
let lastVm = null;
let signature = null;

function freshSession() {
  return {
    startedAt: Date.now(),
    baselined: false,
    alerts: new Map(),        // alertId -> { observedAt, seenAt, baseline }
    requests: new Map(),      // requestId -> { at, source:'command'|'seen'|'baseline' }
    requestCommands: new Map(), // caseId -> command time
    closures: [],             // { at, segments, revision, kind }
    withdrawals: [],          // { closure, seenAt, subject }
    routeStates: new Map(),   // 'draw:<id>' | 'assignment:<id>' -> last observed state
    visibleSegments: new Map(), // segmentId -> first moment shown as closed (Infinity for baseline)
    cityRecalcs: [],          // { closure, seenAt, found }
  };
}

const ms = value => new Date(value).getTime();

export function formatDuration(milliseconds) {
  if (!Number.isFinite(milliseconds)) return '—';
  const negative = milliseconds < 0;
  const exact = Math.abs(milliseconds) / 1000;
  const seconds = Math.round(exact);
  let result;
  if (exact < 10) result = exact.toLocaleString('pl-PL', { maximumFractionDigits: 1, minimumFractionDigits: 1 }) + ' s';
  else if (seconds < 120) result = seconds + ' s';
  else if (seconds < 7200) result = Math.floor(seconds / 60) + ' min ' + (seconds % 60) + ' s';
  else if (seconds < 172800) result = Math.floor(seconds / 3600) + ' h ' + Math.floor(seconds % 3600 / 60) + ' min';
  else result = Math.floor(seconds / 86400).toLocaleString('pl-PL') + ' dni';
  return (negative ? '−' : '') + result;
}

const clock = value => new Date(value).toLocaleTimeString('pl-PL', { timeZone: 'UTC', hour: '2-digit', minute: '2-digit', second: '2-digit' });
const dateTime = value => new Date(value).toLocaleString('pl-PL', { timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'short' });
const count = value => Number(value).toLocaleString('pl-PL');

// ---------- Session observation ----------

onCommand(result => {
  if (!result.ok) return;
  const at = ms(result.at);
  const { operation, body, response } = result;
  if (operation === 'request' && body?.caseId) session.requestCommands.set(body.caseId, at);
  if (operation === 'closure' && body?.closed === true)
    session.closures.push({ at, segments: [body.segmentId], revision: response?.revision ?? null, kind: 'Zamknięcie odcinka' });
  if (operation === 'review-decision' && body?.decision === 'approve') {
    const feature = response?.reviewFeatures?.find(item => item.id === body.reviewFeatureId);
    session.closures.push({ at, segments: feature?.segmentIds ?? [], revision: response?.revision ?? null,
      kind: 'Zatwierdzenie detekcji' });
  }
});

const latestClosureBefore = moment => [...session.closures].reverse().find(item => item.at <= moment) ?? null;

function observe(vm, info) {
  const now = Date.now();
  const baseline = !session.baselined;
  session.baselined = true;

  for (const alert of vm.incident?.alerts ?? []) {
    if (!session.alerts.has(alert.alertId))
      session.alerts.set(alert.alertId, { observedAt: ms(alert.observedAt), seenAt: now, baseline });
  }
  for (const request of vm.dispatch?.requests ?? []) {
    if (session.requests.has(request.requestId)) continue;
    const commanded = session.requestCommands.get(request.caseId);
    session.requests.set(request.requestId, baseline ? { at: null, source: 'baseline' }
      : commanded ? { at: commanded, source: 'command' } : { at: now, source: 'seen' });
  }

  const states = [];
  if (vm.draw?.id) states.push(['draw:' + vm.draw.id, vm.draw.state, 'Wylosowana trasa w węźle']);
  for (const assignment of vm.dispatch?.assignments ?? []) {
    if (['Completed', 'Cancelled'].includes(assignment.state)) continue;
    const routeState = assignment.routeCurrent === true ? 'Current' : assignment.routeStatus?.state ?? 'Unknown';
    states.push(['assignment:' + assignment.assignmentId, routeState, 'Trasa jednostki ' + assignment.unitId]);
  }
  for (const [key, current, subject] of states) {
    const previous = session.routeStates.get(key);
    const withdrawn = current === 'Withdrawn' || WITHDRAWN.includes(current);
    const wasWithdrawn = previous === 'Withdrawn' || WITHDRAWN.includes(previous);
    if (withdrawn && !wasWithdrawn && !baseline && previous !== undefined)
      session.withdrawals.push({ closure: latestClosureBefore(now), seenAt: now, subject });
    session.routeStates.set(key, current);
  }

  const closed = vm.scenario === 'valencia'
    ? (vm.closedRoads ?? []).map(road => road.id) : vm.snapshot?.closedSegments ?? [];
  for (const id of closed) if (!session.visibleSegments.has(id)) session.visibleSegments.set(id, baseline ? Infinity : now);
  for (const id of [...session.visibleSegments.keys()]) if (!closed.includes(id)) session.visibleSegments.delete(id);

  if (info.cityPathUpdated) {
    session.cityRecalcs.push({ closure: latestClosureBefore(now), seenAt: now,
      found: Boolean(state.cityPath?.route) });
  }
}

// ---------- Metric computation ----------

function tile(id, title, fields) {
  return { id, title, ...fields };
}

function unavailable(id, title, reason) {
  return tile(id, title, { value: UNAVAILABLE, source: null, detail: reason, unavailable: true });
}

function reportToView(vm) {
  const title = 'Zgłoszenie → widok sytuacji';
  if (vm.scenario === 'valencia')
    return unavailable(1, title, 'Walencja nie ma zgłoszeń w serwisie Management (dashboard pomija incydent).');
  if (!vm.incident) return unavailable(1, title, 'Brak odczytu zgłoszeń z Management.');
  const measured = [...session.alerts.entries()].filter(([, item]) => !item.baseline);
  if (!measured.length) {
    return tile(1, title, { value: '—', source: MIXED, pending: true,
      detail: vm.incident.alerts.length
        ? 'Zgłoszenia sprzed otwarcia panelu (' + vm.incident.alerts.length + ') nie są mierzone. Wyślij nowe zgłoszenie.'
        : 'Oczekuje na zgłoszenie zalania.' });
  }
  const [alertId, last] = measured[measured.length - 1];
  return tile(1, title, { value: formatDuration(last.seenAt - last.observedAt), source: MIXED,
    detail: alertId + ' · observedAt ' + clock(last.observedAt) + ' (Management) → pierwszy widok ' + clock(last.seenAt)
      + (measured.length > 1 ? ' · pomiarów: ' + measured.length : '') });
}

function assignTime(vm) {
  const title = 'Czas przydziału pomocy';
  if (vm.scenario === 'valencia')
    return unavailable(2, title, 'Walencja nie ma zadań w serwisie Dispatch (dashboard pomija dispatch).');
  if (!vm.dispatch) return unavailable(2, title, 'Brak odczytu zadań z Dispatch.');
  const rows = [];
  let unmeasured = 0;
  for (const assignment of vm.dispatch.assignments) {
    const assigned = (assignment.history ?? []).find(entry => entry.kind === 'Assigned');
    const request = session.requests.get(assignment.requestId);
    if (!assigned) continue;
    if (!request?.at) { unmeasured += 1; continue; }
    rows.push({ assignment, assignedAt: ms(assigned.at), request });
  }
  const pendingRequests = vm.dispatch.requests.filter(item => item.state === 'Open').length;
  if (!rows.length) {
    return tile(2, title, { value: '—', source: MIXED, pending: true,
      detail: [unmeasured ? 'Przydziały bez znanego czasu prośby: ' + unmeasured + ' (prośba sprzed otwarcia panelu).' : null,
        pendingRequests ? 'Prośby oczekujące na jednostkę: ' + pendingRequests + '.' : 'Oczekuje na prośbę i przydział.']
        .filter(Boolean).join(' ') });
  }
  const last = rows.reduce((a, b) => (b.assignedAt > a.assignedAt ? b : a));
  const start = last.request.source === 'command' ? 'potwierdzenie prośby w sesji' : 'pierwszy widok prośby w sesji';
  return tile(2, title, { value: formatDuration(last.assignedAt - last.request.at), source: MIXED,
    detail: last.assignment.requestId + ' · ' + start + ' ' + clock(last.request.at) + ' → history Assigned '
      + clock(last.assignedAt) + ' (Dispatch). Prośba nie ma znacznika czasu w DTO.' });
}

function closureToWithdrawal(vm) {
  const title = 'Zamknięcie → wycofanie trasy';
  if (vm.scenario === 'valencia')
    return unavailable(3, title, 'Walencja nie ma tras jednostek (Dispatch); stan losowanej trasy ewakuacji (task 03) pokazuje mapa i krok prezentacji. Zobacz też przeliczenie trasy miejskiej niżej.');
  const measured = session.withdrawals.filter(item => item.closure);
  const server = serverWithdrawal(vm);
  if (!measured.length) {
    return tile(3, title, { value: '—', source: SESSION, pending: true,
      detail: [session.withdrawals.length ? 'Wycofanie widoczne bez zamknięcia w tej sesji.' : 'Oczekuje na zamknięcie odcinka na aktywnej trasie.',
        server].filter(Boolean).join(' ') });
  }
  const last = measured[measured.length - 1];
  return tile(3, title, { value: formatDuration(last.seenAt - last.closure.at), source: SESSION,
    detail: [last.closure.kind + ' ' + clock(last.closure.at) + ' → ' + last.subject + ' wycofana w panelu '
      + clock(last.seenAt) + '.', server].filter(Boolean).join(' ') });
}

// Dispatch history[RouteWithdrawn].at against Situation closureEvidence[segment].decidedAt.
function serverWithdrawal(vm) {
  const evidence = vm.snapshot?.closureEvidence ?? {};
  for (const assignment of vm.dispatch?.assignments ?? []) {
    for (const entry of [...(assignment.history ?? [])].reverse()) {
      if (entry.kind !== 'RouteWithdrawn') continue;
      const decided = (entry.segments ?? []).map(id => evidence[id]?.decidedAt).filter(Boolean).map(ms);
      if (!decided.length) continue;
      return 'Z danych serwisu: RouteWithdrawn ' + clock(entry.at) + ' − decidedAt ' + clock(Math.min(...decided))
        + ' = ' + formatDuration(ms(entry.at) - Math.min(...decided)) + ' (wpis powstaje przy przeliczeniu).';
    }
  }
  return null;
}

function withoutExit(vm) {
  const title = 'Osoby bez potwierdzonego wyjścia';
  if (vm.scenario === 'valencia')
    return unavailable(4, title, 'Walencja nie ma spraw mieszkańców w serwisie Management.');
  if (!vm.incident) return unavailable(4, title, 'Brak odczytu spraw z Management.');
  const cases = vm.incident.cases;
  const open = cases.filter(item => item.progress !== 'ExitConfirmed');
  const finishedTransport = new Set((vm.dispatch?.assignments ?? [])
    .filter(item => ['PickupConfirmed', 'Completed'].includes(item.state)).map(item => item.caseId));
  const onlyTransport = open.filter(item => finishedTransport.has(item.caseId)).length;
  return tile(4, title, { value: count(open.length) + ' z ' + count(cases.length), source: SERVER, emphasis: open.length > 0,
    detail: 'Liczone są tylko jawne potwierdzenia wyjścia (INV-12).'
      + (onlyTransport ? ' Odbiór lub zakończenie zadania bez potwierdzenia wyjścia: ' + onlyTransport + '.' : '') });
}

function valenciaTiles(vm) {
  const hazards = vm.snapshot?.hazards ?? [];
  const ems = hazards.find(layer => layer.productId) ?? null;
  const approved = (vm.snapshot?.reviewFeatures ?? []).filter(item => item.status === 'Approved' && item.decidedAt);
  const last = approved.reduce((a, b) => (!a || ms(b.decidedAt) > ms(a.decidedAt) ? b : a), null);
  const tiles = [];

  if (ems && last) {
    tiles.push(tile('v1', 'Obserwacja EMS → decyzja', { value: formatDuration(ms(last.decidedAt) - ms(ems.observedAt)),
      source: SERVER, detail: ems.productId + ' observedAt ' + dateTime(ems.observedAt)
        + ' → decidedAt ' + clock(last.decidedAt) + '. Archiwum odtwarzane w ćwiczeniu: odstęp obejmuje czas od archiwum.' }));
  } else {
    tiles.push(tile('v1', 'Obserwacja EMS → decyzja', { value: '—', source: SERVER, pending: true,
      detail: ems ? ems.productId + ' · oczekuje na zatwierdzenie detekcji.' : 'Brak warstwy EMS z ID produktu.' }));
  }

  if (last) {
    tiles.push(tile('v2', 'Zobrazowanie → decyzja', { value: formatDuration(ms(last.decidedAt) - ms(last.observedAt)),
      source: SERVER, detail: last.imageProductId + ' observedAt ' + dateTime(last.observedAt)
        + ' → decidedAt ' + clock(last.decidedAt) + (last.mode === 'synthetic' ? ' · detekcja syntetyczna' : '') }));
    const shown = last.segmentIds.map(id => session.visibleSegments.get(id)).filter(value => value !== undefined);
    const visibleAt = shown.length === last.segmentIds.length && shown.length ? Math.max(...shown) : null;
    tiles.push(visibleAt === null || !Number.isFinite(visibleAt)
      ? tile('v3', 'Decyzja → zamknięcie w panelu', { value: '—', source: MIXED, pending: true,
        detail: visibleAt === Infinity ? 'Zamknięcie widoczne już przy otwarciu panelu — nie mierzono.'
          : 'Oczekuje na odczyt zamkniętych odcinków.' })
      : tile('v3', 'Decyzja → zamknięcie w panelu', { value: formatDuration(visibleAt - ms(last.decidedAt)), source: MIXED,
        detail: 'decidedAt ' + clock(last.decidedAt) + ' (Situation) → odcinki na mapie ' + clock(visibleAt)
          + ' · ' + count(last.segmentIds.length) + ' odc.' }));
  } else {
    tiles.push(tile('v2', 'Zobrazowanie → decyzja', { value: '—', source: SERVER, pending: true,
      detail: 'Brak zatwierdzonej detekcji z czasem decyzji.' }));
  }

  const recalc = [...session.cityRecalcs].reverse().find(item => item.closure);
  tiles.push(recalc
    ? tile('v4', 'Zamknięcie → przeliczenie trasy', { value: formatDuration(recalc.seenAt - recalc.closure.at), source: SESSION,
      detail: recalc.closure.kind + ' ' + clock(recalc.closure.at) + ' → trasa testowa przeliczona ' + clock(recalc.seenAt)
        + (recalc.found ? '.' : ' · wynik: brak trasy.') })
    : tile('v4', 'Zamknięcie → przeliczenie trasy', { value: '—', source: SESSION, pending: true,
      detail: 'Wskaż punkty trasy testowej, a następnie zatwierdź zamknięcie.' }));

  const zones = vm.network?.city?.zoneCounts;
  if (zones) {
    const total = Object.values(zones).reduce((sum, value) => sum + value, 0);
    tiles.push(tile('v5', 'Węzły w strefie zagrożenia', { value: count(zones.hazard ?? 0), source: SERVER,
      detail: 'z ' + count(total) + ' węzłów grafu · ' + count(zones.unknown ?? 0) + ' bez oceny (Routing, zoneCounts).' }));
  }
  const estimate = vm.snapshot?.populationEstimate;
  tiles.push(estimate
    ? tile('v6', 'Szacunek ludności w strefie', { value: count(estimate.value), source: SERVER,
      detail: estimate.source + ' ' + estimate.year + ' · ' + estimate.method })
    : unavailable('v6', 'Szacunek ludności w strefie', 'Warstwa EMS Walencji nie zawiera szacunku populacji.'));
  return tiles;
}

// ---------- Rendering ----------

function renderTile(item) {
  const node = element('article', null, 'metric' + (item.unavailable ? ' metric-unavailable' : '')
    + (item.pending ? ' metric-pending' : '') + (item.emphasis ? ' metric-emphasis' : ''));
  node.append(element('h3', item.title), element('strong', item.value, 'metric-value'));
  if (item.source) node.append(element('span', item.source, 'metric-source metric-source-'
    + (item.source === SERVER ? 'server' : item.source === SESSION ? 'session' : 'mixed')));
  if (item.detail) node.append(element('p', item.detail));
  return node;
}

function render(vm) {
  const panel = $('metrics-panel');
  const valencia = vm.scenario === 'valencia';
  const core = [reportToView(vm), assignTime(vm), closureToWithdrawal(vm), withoutExit(vm)];
  const extra = valencia ? valenciaTiles(vm) : [];
  const status = vm.readAvailable ? 'Pomiar sesji od ' + clock(session.startedAt)
    : 'Dane nieaktualne — pokazano ostatni odczyt. Pomiar sesji od ' + clock(session.startedAt);
  const next = JSON.stringify([core, extra, status]);
  if (next === signature) return;
  signature = next;
  panel.hidden = false;
  panel.classList.toggle('metrics-stale', !vm.readAvailable);
  $('metrics-status').textContent = status;
  const groups = [element('div', null, 'metrics-grid')];
  groups[0].append(...core.map(renderTile));
  if (extra.length) {
    const heading = element('h3', 'Walencja · pomiary z danych rzeczywistych', 'metrics-subtitle');
    const grid = element('div', null, 'metrics-grid');
    grid.append(...extra.map(renderTile));
    groups.push(heading, grid);
  }
  $('metrics-body').replaceChildren(...groups);
}

function build() {
  const panel = $('metrics-panel');
  if (!panel) return;
  const heading = element('div', null, 'card-heading');
  const title = element('div');
  const h2 = element('h2', 'Metryki ćwiczenia');
  h2.id = 'metrics-title';
  const status = element('p', 'Oczekiwanie na odczyt scenariusza.');
  status.id = 'metrics-status';
  title.append(h2, status);
  const reset = element('button', 'Wyzeruj pomiar sesji');
  reset.type = 'button';
  reset.id = 'metrics-reset';
  reset.addEventListener('click', () => {
    session = freshSession();
    signature = null;
    if (lastVm) {
      observe(lastVm, { cityPathUpdated: false });
      render(lastVm);
    }
  });
  heading.append(title, reset);
  const legend = element('p', 'Źródło: „' + SERVER + '” — znaczniki czasu z usług; „' + SESSION
    + '” — chwile zaobserwowane w tej przeglądarce. Brak danych nie jest zastępowany szacunkiem. Godziny w UTC.', 'metrics-legend');
  const body = element('div', null, 'metrics-body');
  body.id = 'metrics-body';
  panel.setAttribute('aria-labelledby', 'metrics-title');
  panel.removeAttribute('aria-label');
  panel.replaceChildren(heading, legend, body);

  subscribe((vm, info) => {
    if (info.reason === 'refresh') {
      observe(vm, info);
      lastVm = vm;
      render(vm);
    } else if (info.reason === 'invalidate' && lastVm) {
      render({ ...lastVm, readAvailable: false });
    }
  });
}

build();
