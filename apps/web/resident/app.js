const $ = id => document.getElementById(id);
const labels = {
  Open: 'Oczekuje na przyjęcie instrukcji',
  Acknowledged: 'Instrukcje przyjęte',
  Moving: 'Ewakuacja rozpoczęta',
  ExitConfirmed: 'System przyjął zgłoszenie opuszczenia strefy.',
  Assigned: 'Przydzielono jednostkę', AtPickup: 'Jednostka dotarła do punktu odbioru',
  PickupConfirmed: 'Odbiór potwierdzony', Completed: 'Transport zakończony', Cancelled: 'Zgłoszenie anulowane',
};
const steps = {
  Open: ['acknowledgement', 'Przyjmij instrukcje'],
  Acknowledged: ['moving', 'Zgłoś rozpoczęcie ewakuacji'],
  Moving: ['exit-confirmed', 'Zgłoś opuszczenie strefy'],
};
const state = { network: null, resident: null, nodes: [], busy: false, reading: false, online: false };
let connectionNotice = false;
const profile = $('profile');
const point = $('node');
const controls = [$('draw'), $('locate'), $('help'), $('progress')];
const byId = id => state.nodes.find(node => node.id === id);
const label = id => byId(id)?.label ?? id;
const time = value => new Intl.DateTimeFormat('pl-PL', { timeZone: 'UTC', hour: '2-digit', minute: '2-digit' })
  .format(new Date(value));

async function api(path, data) {
  const response = await fetch('/api/' + path, data ? {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data),
  } : undefined);
  const body = await response.json().catch(() => null);
  if (!response.ok) throw Object.assign(new Error(body?.detail ?? 'Usługa jest chwilowo niedostępna.'), {
    status: response.status, code: body?.code,
  });
  return body;
}

function storage(key) {
  return 'cassini.resident.' + state.network.scenario + '.' + profile.value + '.' + key;
}

async function command(path, payload, key, onSend) {
  const pendingKey = storage('pending.' + key);
  const saved = JSON.parse(sessionStorage.getItem(pendingKey) ?? 'null');
  const pending = saved ?? { ...payload, commandId: crypto.randomUUID() };
  sessionStorage.setItem(pendingKey, JSON.stringify(pending));
  onSend?.(pending);
  try {
    const result = await api(path, pending);
    sessionStorage.removeItem(pendingKey);
    return result;
  } catch (error) {
    if (error.status >= 400 && error.status < 500) sessionStorage.removeItem(pendingKey);
    throw error;
  }
}

function notice(message = '') {
  $('notice').textContent = message;
  $('notice').hidden = !message;
}

function syncControls() {
  const locked = state.busy || state.reading || !state.online || !state.resident;
  const supported = state.network?.scenario !== 'valencia';
  for (const control of controls) control.disabled = locked;
  profile.disabled = state.busy;
  point.disabled = locked || !supported;
  $('locate').disabled = point.disabled;
  $('draw').disabled = locked || !supported || state.resident.mobility !== 'Walking' ||
    state.resident.progress === 'ExitConfirmed';
  $('help').disabled = locked || !supported || state.resident.progress === 'ExitConfirmed';
  $('progress').disabled = locked || !supported || !state.alert || !steps[state.resident.progress];
}

async function refresh() {
  if (state.reading) return;
  state.reading = true;
  syncControls();
  const caseId = profile.value;
  try {
    state.network ??= await api('routing/queries/network');
    if (profile.value !== caseId) return;
    if (!state.nodes.length) {
      state.nodes = state.network.nodes;
      point.replaceChildren(...state.nodes.map(node => new Option(node.label, node.id)));
    }
    const id = sessionStorage.getItem(storage('draw'));
    const [incident, dispatch, situation, draw] = await Promise.all([
      api('management/queries/incident'), api('dispatch/queries/assignments'), api('situation/queries/snapshot'),
      id && state.network.scenario !== 'valencia' ? api('routing/queries/draws/' + id).catch(error => {
        if (error.status === 404) return { state: 'Unknown' };
        throw error;
      }) : null,
    ]);
    if (profile.value !== caseId) return;
    const initialize = !state.resident;
    state.resident = incident.cases.find(item => item.caseId === caseId);
    if (!state.resident) throw new Error('Wybrany profil jest niedostępny.');
    if (initialize && byId(state.resident.node)) point.value = state.resident.node;
    state.alert = incident.alerts.at(-1);
    state.closed = new Set(situation.closedSegments);
    state.draw = draw?.route?.segments.some(segment => state.closed.has(segment))
      ? { state: 'Withdrawn' } : draw;
    state.request = dispatch.requests.filter(item => item.caseId === caseId).at(-1);
    state.assignment = dispatch.assignments.filter(item => item.requestId === state.request?.requestId).at(-1);
    state.online = true;
    if (connectionNotice) {
      connectionNotice = false;
      notice();
    }
    render();
  } catch (error) {
    if (profile.value === caseId) {
      state.online = false;
      state.draw = null;
      render();
      connectionNotice = true;
      notice('Nie udało się odświeżyć danych. Spróbujemy połączyć się ponownie.');
    }
  } finally {
    state.reading = false;
    syncControls();
    if (profile.value !== caseId) void refresh();
  }
}

function render() {
  $('alert-title').textContent = state.online
    ? state.alert ? 'Aktywny alert powodziowy' : 'Brak aktywnego alertu'
    : 'Komunikaty chwilowo niedostępne';
  $('alert-text').textContent = state.online && state.alert
    ? 'Wybierz punkt startu. Jeśli potrzebujesz transportu, zgłoś się po pomoc. Alert ćwiczeniowy · ' +
      time(state.alert.observedAt) + ' UTC.' : '';
  const route = state.online ? state.draw?.route : null;
  $('draw').textContent = ['Withdrawn', 'Expired'].includes(state.draw?.state)
    ? 'Wyznacz nową trasę' : 'Wyznacz trasę';
  $('route-status').textContent = !state.online ? 'Status trasy niedostępny.'
    : state.network.scenario === 'valencia' ? 'Przydział tras mieszkańcom działa w scenariuszu Kłodzka i Warszawy.'
    : route ? label(route.exit) + ' · start ' + time(route.departsAt) + ' UTC'
    : state.draw?.state === 'Withdrawn' ? 'Trasa wycofana. Wyznacz nową.'
    : state.draw?.state === 'Expired' ? 'Trasa wygasła. Wyznacz nową.'
    : state.noRouteNode === point.value ? 'Brak dostępnej trasy z tego punktu.'
    : state.resident.mobility === 'Assisted' ? 'W tym profilu możesz zgłosić potrzebę transportu.'
    : 'Wskaż punkt startu, aby otrzymać trasę.';
  const request = state.request;
  const assignment = state.assignment;
  const helpState = request?.state === 'Open' ? 'Open' : assignment?.state ?? request?.state;
  $('help-status').textContent = !state.online ? 'Stan zgłoszenia niedostępny.'
    : request ? 'Punkt odbioru: ' + label(request.pickupNode) + '. ' +
      (helpState === 'Open' ? 'Zgłoszenie przyjęte. Oczekujesz na jednostkę.'
        : helpState === 'Acknowledged' ? 'Jednostka przyjęła zadanie.' : labels[helpState] ?? 'Zgłoszenie przyjęte.')
    : 'Punkt odbioru możesz wybrać powyżej.';
  $('progress-status').textContent = state.online ? labels[state.resident.progress] : 'Status zgłoszenia niedostępny.';
  $('progress').textContent = steps[state.resident?.progress]?.[1] ?? 'Zgłoś postęp';
  drawMap(route);
}

function drawMap(route) {
  const canvas = $('map');
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (!state.nodes.length) return;
  const coordinates = state.nodes.map(node => [node.longitude, node.latitude]);
  const longitudes = coordinates.map(([longitude]) => longitude);
  const latitudes = coordinates.map(([, latitude]) => latitude);
  const west = Math.min(...longitudes), east = Math.max(...longitudes);
  const south = Math.min(...latitudes), north = Math.max(...latitudes);
  const ratio = Math.cos((south + north) * Math.PI / 360);
  const scale = Math.min(800 / Math.max(.0001, (east - west) * ratio), 380 / Math.max(.0001, north - south));
  const project = ([longitude, latitude]) => [
    480 + (longitude - (west + east) / 2) * ratio * scale,
    255 - (latitude - (south + north) / 2) * scale,
  ];
  for (const segment of state.network.segments) {
    const ends = [byId(segment.from), byId(segment.to)];
    if (ends.some(node => !node)) continue;
    const active = route?.segments.includes(segment.id);
    const closed = state.closed?.has(segment.id);
    ctx.beginPath();
    ctx.lineJoin = ctx.lineCap = 'round';
    const line = segment.geometry ?? ends.map(node => [node.longitude, node.latitude]);
    line.forEach((pair, index) => index ? ctx.lineTo(...project(pair)) : ctx.moveTo(...project(pair)));
    ctx.strokeStyle = active ? '#6b8a23' : closed ? '#b04c37' : '#a5b7a9';
    ctx.lineWidth = active ? 10 : 4;
    ctx.stroke();
  }
  const start = byId(route?.nodes[0] ?? point.value);
  const exit = byId(route?.exit);
  for (const [node, color, title] of [[start, '#236446', 'Start'], [exit, '#976015', 'Wyjście']]) {
    if (!node) continue;
    const [x, y] = project([node.longitude, node.latitude]);
    ctx.beginPath();
    ctx.arc(x, y, 12, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
    ctx.font = 'bold 23px system-ui';
    ctx.textAlign = 'center';
    ctx.fillText(title, x, y - 18);
  }
  canvas.setAttribute('aria-label', route ? 'Trasa od ' + label(route.nodes[0]) + ' do ' + label(route.exit)
    : 'Mapa schematyczna ćwiczenia');
}

async function run(action) {
  if (state.busy || state.reading || !state.online) return;
  state.busy = true;
  notice();
  syncControls();
  try { await action(); }
  catch (error) {
    notice(error.message === 'Failed to fetch' ? 'Brak połączenia. Ponów tę samą czynność po połączeniu.' : error.message);
  } finally {
    state.busy = false;
    await refresh();
  }
}

profile.addEventListener('change', () => {
  state.resident = null;
  state.draw = null;
  state.online = false;
  state.noRouteNode = null;
  $('position').textContent = '';
  notice();
  syncControls();
  render();
  void refresh();
});
point.addEventListener('change', () => {
  $('position').textContent = 'Wybrano: ' + label(point.value) + '.';
  render();
});
$('locate').addEventListener('click', () => {
  if (!navigator.geolocation) return notice('Wybierz punkt ręcznie. Przeglądarka nie udostępnia lokalizacji.');
  const caseId = profile.value;
  navigator.geolocation.getCurrentPosition(({ coords }) => {
    if (profile.value !== caseId) return;
    const distance = node => 111195 * Math.hypot(node.latitude - coords.latitude,
      (node.longitude - coords.longitude) * Math.cos(coords.latitude * Math.PI / 180));
    const nearest = state.nodes.reduce((best, node) => distance(node) < distance(best) ? node : best);
    if (coords.accuracy > 150 || distance(nearest) > 250)
      return notice('Pozycja jest zbyt niedokładna lub poza obszarem ćwiczenia. Wybierz punkt ręcznie.');
    point.value = nearest.id;
    $('position').textContent = 'Najbliższy punkt: ' + nearest.label + '. Sprawdź wybór przed wysłaniem.';
    render();
  }, () => {
    if (profile.value === caseId) notice('Nie udało się pobrać lokalizacji. Wybierz punkt ręcznie.');
  }, { enableHighAccuracy: true, timeout: 8000, maximumAge: 10000 });
});
$('draw').addEventListener('click', () => run(async () => {
  const key = storage('draw');
  const dist = await api('routing/queries/distribution?node=' + encodeURIComponent(point.value));
  if (!dist.options.length) {
    sessionStorage.removeItem(key);
    sessionStorage.removeItem(storage('pending.route'));
    state.noRouteNode = point.value;
    notice('Brak dostępnej trasy z wybranego punktu.');
    return;
  }
  state.noRouteNode = null;
  try {
    await command('routing/commands/draw', { node: point.value, revision: dist.revision, subjectId: profile.value },
      'route', pending => sessionStorage.setItem(key, pending.commandId));
  } catch (error) {
    if (error.status >= 400 && error.status < 500) sessionStorage.removeItem(key);
    throw error;
  }
}));
$('help').addEventListener('click', () => run(async () => {
  const caseId = profile.value;
  if (state.resident.node !== point.value) {
    const updated = await command('management/commands/progress', {
      caseId, step: 'position', node: point.value, expectedVersion: state.resident.version,
    }, 'position');
    state.resident = updated;
  }
  await command('dispatch/commands/request', { caseId, pickupNode: state.resident.node }, 'help');
  notice('Zgłoszenie pomocy zostało przyjęte.');
}));
$('progress').addEventListener('click', () => run(async () => {
  const [step] = steps[state.resident.progress];
  await command('management/commands/progress', {
    caseId: profile.value, step, expectedVersion: state.resident.version,
  }, step);
  if (step === 'exit-confirmed') notice('System przyjął zgłoszenie opuszczenia strefy.');
}));

void refresh();
setInterval(() => { if (!state.busy) void refresh(); }, 5000);
