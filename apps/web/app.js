const $ = id => document.getElementById(id);
const satelliteScenes = fetch('/maps/satellite.json')
  .then(response => response.ok ? response.json() : null)
  .catch(() => null);
const companionConfig = fetch('/ui/companion')
  .then(response => response.ok ? response.json() : null)
  .catch(() => null);
const state = {
  network: null, situation: null, distribution: null, draw: null,
  pendingDraw: null, subject: null, busy: false, epoch: 0, polling: false,
  operationsSignature: null, sourcesSignature: null, reviewSignature: null,
  cityOverview: true, cityPicking: false, cityPoints: [], cityPath: null,
  satelliteScene: null, satelliteDetail: false,
};
const names = {
  'walk-start': 'Punkt zbiórki', pickup: 'Węzeł przy odbiorze',
  'east-junction': 'Przed mostem', 'exit-east': 'Wyjście wschodnie',
  'north-junction': 'Dojście północne', 'exit-north': 'Wyjście północne',
};
const statuses = {
  Open: 'Otwarta sprawa', Acknowledged: 'Potwierdzono odbiór', Moving: 'W drodze',
  ExitConfirmed: 'Wyjście potwierdzone', Assigned: 'Jednostka przydzielona',
  AtPickup: 'Na miejscu odbioru', PickupConfirmed: 'Odbiór osoby potwierdzony',
  Completed: 'Zakończono zadanie', Cancelled: 'Anulowano', Available: 'Gotowa', Booked: 'Zajęta',
};
const formatTime = new Intl.DateTimeFormat('pl-PL', {
  timeZone: 'UTC', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
});
const formatObservation = new Intl.DateTimeFormat('pl-PL', {
  timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'short',
});
const time = value => formatTime.format(new Date(value));
const minutes = seconds => Math.round(seconds / 60) + ' min';
const text = (id, value) => { $(id).textContent = value; };
const label = id => (state.network?.scenario === 'warsaw'
  ? state.network.nodes.find(node => node.id === id)?.label : names[id]) || id;
const segmentLabel = id => ({
  'bridge-2': 'Most Świętokrzyski', 'north-link': 'Dojście północne',
  'walk-to-pickup': 'Dojście do punktu przesiadkowego', 'east-link': 'Dojazd do mostu',
  'north-exit-link': 'Dojście do wyjścia', 'outside-connector': 'Objazd dla jednostki',
})[id] || id;
const warsawMapLabels = {
  'walk-start': ['Punkt zbiórki', 10, 22],
  pickup: ['Punkt przesiadkowy', 12, -12],
  'east-junction': ['Most Świętokrzyski', -12, -12],
  'exit-east': ['Wyjście · Powiśle', -10, -14],
  'exit-north': ['Wyjście · Praga', 10, -14],
};
const commandId = () => crypto.randomUUID();

async function api(service, path, data) {
  const response = await fetch('/api/' + service + '/' + path, data === undefined ? undefined : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data),
  });
  const body = await response.json().catch(() => null);
  if (!response.ok)
    throw Object.assign(new Error(body?.detail || body?.title || body?.code || 'HTTP ' + response.status),
      { status: response.status, code: body?.code });
  return body;
}

function element(tag, value, className) {
  const node = document.createElement(tag);
  if (value) node.textContent = value;
  if (className) node.className = className;
  return node;
}

function button(value, handler) {
  const node = element('button', value);
  node.type = 'button';
  node.addEventListener('click', () => run(handler));
  return node;
}

function notice(value, error = false) {
  text('notice', value);
  $('notice').classList.toggle('error', error);
}

async function run(action) {
  if (state.busy) return;
  state.busy = true;
  const epoch = ++state.epoch;
  syncControls();
  let message;
  try {
    message = await action();
    await refresh(epoch);
    notice(message || 'Scenariusz gotowy. Wybierz węzeł i wylosuj trasę.');
  } catch (error) {
    invalidateRead();
    notice('Nie udało się wykonać operacji. ' + error.message, true);
  } finally {
    state.busy = false;
    syncControls();
  }
}

function invalidateRead() {
  state.distribution = null;
  if (state.draw) state.draw = { id: state.draw.id, state: 'Unknown', route: null };
  renderDistribution();
}

async function refresh(epoch) {
  const [network, situation, incident, dispatch, scenes, companion] = await Promise.all([
    state.network ?? api('routing', 'queries/network'),
    api('situation', 'queries/snapshot'),
    api('management', 'queries/incident'),
    api('dispatch', 'queries/assignments'),
    satelliteScenes,
    companionConfig,
  ]);
  if (state.epoch !== epoch) return;
  const node = $('node').value;
  const previousCityPath = state.cityPath;
  const [distribution, draw, cityPath] = await Promise.all([
    api('routing', 'queries/distribution?node=' + encodeURIComponent(node)),
    state.draw ? api('routing', 'queries/draws/' + state.draw.id) : null,
    previousCityPath && (previousCityPath.revision !== situation.revision || !state.distribution)
      ? requestCityPath() : null,
  ]);
  if (state.epoch !== epoch) return;
  if (situation.revision !== distribution.revision || (draw && draw.revision !== distribution.revision) ||
      (cityPath && cityPath.revision !== distribution.revision))
    throw new Error('Sytuacja zmieniła się podczas odczytu.');

  state.network = network;
  state.situation = situation;
  state.distribution = distribution;
  if (draw) state.draw = { id: state.draw.id, ...draw };
  if (cityPath && state.cityPath === previousCityPath) showCityPath(cityPath);
  const warsaw = network.scenario === 'warsaw';
  const scenarioLink = $('scenario-switch');
  const destination = new URL('/', window.location.origin);
  scenarioLink.hidden = !companion?.port || String(companion.port) === destination.port;
  if (!scenarioLink.hidden) {
    destination.port = companion.port;
    scenarioLink.href = destination.href;
    scenarioLink.textContent = warsaw ? 'Przełącz na Kłodzko' : 'Przełącz na Warszawę';
  }
  const scene = scenes?.[warsaw ? 'warsaw' : 'sample'];
  state.satelliteScene = scene;
  text('incident', warsaw ? 'DEMO · POWÓDŹ · WARSZAWA' : 'DEMO · POWÓDŹ · ' + incident.incidentId);
  text('map-area', network.city
    ? 'Warszawa · ' + network.city.roadCount.toLocaleString('pl-PL') + ' odcinków OSM'
    : warsaw ? 'Warszawa · lokalne ćwiczenie nad Wisłą' : 'Kłodzko · obszar ćwiczeń');
  $('map-credit').hidden = !warsaw;
  $('city-tools').hidden = !warsaw || !(network.city || scene);
  $('city-routing').hidden = !network.city;
  if (scene) {
    $('satellite-source').href = scene.sourceUrl;
    text('satellite-source', 'Contains modified Copernicus Sentinel data ' + scene.observedAt.slice(0, 4));
    text('satellite-date', ' · Sentinel-2 L2A · obserwacja ' +
      formatObservation.format(new Date(scene.observedAt)) + ' UTC');
  }
  const archive = scene?.floodArchive;
  $('archive-credit').hidden = !archive;
  if (archive) {
    $('archive-source').href = archive.sourceUrl;
    text('archive-source', '© European Union, Copernicus EMS · ' + archive.productId);
    text('archive-date', ' · obserwacja ' + formatObservation.format(new Date(archive.observedAt)) + ' UTC');
  }
  $('review-panel').hidden = !warsaw;
  text('clock', time(distribution.at) + ' UTC');
  text('revision', 'Rewizja ' + situation.revision);
  renderDistribution();
  const operations = JSON.stringify([incident, dispatch]);
  if (operations !== state.operationsSignature) {
    renderOperations(incident, dispatch);
    state.operationsSignature = operations;
  }
  const hazards = situation.hazards || [];
  const sources = JSON.stringify(hazards);
  if (sources !== state.sourcesSignature || state.lastSourceRevision !== situation.revision) {
    renderSources(hazards);
    state.sourcesSignature = sources;
    state.lastSourceRevision = situation.revision;
  }
  const estimate = situation.populationEstimate;
  $('population-estimate').hidden = !estimate;
  if (estimate) {
    const zone = hazards.some(h => h.productId) ? 'strefie' : 'strefie ćwiczeniowej';
    text('population-estimate',
      'Szacunek ludności w ' + zone + ' · ' + new Intl.NumberFormat('pl-PL').format(estimate.value) +
      ' osób · ' + estimate.source + ' ' + estimate.year);
  }
  const reviews = situation.reviewFeatures || [];
  const reviewSignature = JSON.stringify(reviews);
  if (reviewSignature !== state.reviewSignature) {
    renderReviews(reviews);
    state.reviewSignature = reviewSignature;
  }
}

function syncControls() {
  const closed = state.situation?.closedSegments || [];
  $('node').disabled = state.busy || !state.situation;
  $('draw').disabled = state.busy || !state.distribution?.options.length || state.draw?.state === 'Active';
  $('bridge').disabled = state.busy || !state.situation || closed.includes('bridge-2');
  $('north').disabled = state.busy || !closed.includes('bridge-2') || closed.includes('north-link');
  $('reopen').disabled = state.busy || !closed.length;
  $('alert').disabled = state.busy || !state.situation;
  for (const control of $('reviews').querySelectorAll('button'))
    control.disabled = state.busy || !state.situation;
}

function renderDistribution() {
  const options = state.distribution?.options ?? [];
  const list = $('options');
  list.replaceChildren();
  if (!options.length) list.append(element('p', state.distribution ? 'Brak dostępnej trasy.' : 'Oczekiwanie na rozkład tras.'));
  for (const route of options) {
    const item = element('div', null, 'option');
    const head = element('div', null, 'option-head');
    const chance = Math.round(route.weight * 100);
    head.append(element('b', label(route.exit)), element('strong', chance + '%'));
    const meter = element('progress', null, 'probability');
    meter.max = 100;
    meter.value = chance;
    meter.setAttribute('aria-label', 'Szansa: ' + label(route.exit));
    item.append(head, element('p', time(route.departsAt) + ' – ' + time(route.arrivesAt) + ' UTC · marsz ' + minutes(route.seconds)), meter);
    list.append(item);
  }
  const result = $('draw-result');
  const route = state.draw?.route;
  result.className = 'draw-result';
  if (route && state.draw.state === 'Active') {
    result.classList.add('active');
    result.textContent = 'Wylosowano: ' + label(route.exit) + '. Przybycie ' + time(route.arrivesAt) + ' UTC.';
  } else if (state.draw?.state === 'Withdrawn') {
    result.classList.add('withdrawn');
    result.textContent = 'Trasa wycofana po zmianie dostępności.' + (options.length ? ' Wylosuj nową trasę.' : '');
  } else if (state.draw?.state === 'Expired') {
    result.textContent = 'Ważność trasy upłynęła.';
  } else if (state.draw?.state === 'Unknown' || state.pendingDraw) {
    result.textContent = 'Wynik losowania czeka na potwierdzenie.';
  } else {
    result.textContent = 'Tutaj pojawi się wynik losowania.';
  }
  renderMap(route && state.draw?.state === 'Active' ? route : null);
}

function svg(tag, attrs, value) {
  const item = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [key, attr] of Object.entries(attrs)) item.setAttribute(key, attr);
  if (value) item.textContent = value;
  return item;
}

function polygons(geometry) {
  if (geometry.type === 'Polygon') return [geometry.coordinates];
  if (geometry.type === 'MultiPolygon') return geometry.coordinates;
  if (geometry.type === 'GeometryCollection') return geometry.geometries.flatMap(polygons);
  return [];
}

function renderMap(route) {
  const map = $('map');
  if (!state.network) return;
  const warsaw = state.network.scenario === 'warsaw';
  const scene = state.satelliteScene;
  const city = state.network.city;
  const cityBounds = city?.bounds ?? (warsaw ? scene?.bounds : null);
  const overview = Boolean(cityBounds && state.cityOverview);
  const ortho = !overview && !state.satelliteDetail ? scene?.detail : null;
  const background = ortho ?? scene;
  $('satellite-credit').hidden = !scene || Boolean(ortho);
  $('ortho-credit').hidden = !ortho;
  if (ortho) $('ortho-source').href = ortho.sourceUrl;
  $('map-layer-tools').hidden = overview || !scene?.detail;
  text('map-layer', ortho ? 'Pokaż Sentinel-2' : 'Pokaż ortofotomapę');
  const bounds = overview ? cityBounds : scene?.viewBounds ?? state.network.bounds;
  $('city-overview').setAttribute('aria-pressed', overview);
  $('city-detail').setAttribute('aria-pressed', !overview);
  const [west, south, east, north] = bounds || [16.646, 50.431, 16.660, 50.442];
  const ratio = Math.cos((south + north) * Math.PI / 360);
  const scale = Math.min(680 / ((east - west) * ratio), 360 / (north - south));
  const project = (lon, lat) => [380 + (lon - (west + east) / 2) * ratio * scale,
    210 + ((south + north) / 2 - lat) * scale];
  state.unproject = (x, y) => [(x - 380) / (ratio * scale) + (west + east) / 2,
    (south + north) / 2 - (y - 210) / scale];
  const pointsOnMap = line => line.map(([lon, lat]) => project(lon, lat).join(',')).join(' ');
  const shape = polygon => polygon.map(ring => 'M ' + pointsOnMap(ring).split(' ').join(' L ') + ' Z').join(' ');
  const mapImage = ([left, bottom, right, top], href, opacity) => {
    const [x, y] = project(left, top);
    const [x2, y2] = project(right, bottom);
    return svg('image', { href, x, y, width: x2 - x, height: y2 - y, preserveAspectRatio: 'none', opacity });
  };
  const points = new Map(state.network.nodes.map(node => [node.id, project(node.longitude, node.latitude)]));
  const closed = new Set(state.situation.closedSegments);
  const selected = new Set(route?.segments || []);
  const defs = svg('defs', {});
  const grid = svg('pattern', { id: 'grid', width: 34, height: 34, patternUnits: 'userSpaceOnUse' });
  grid.append(svg('path', { d: 'M 34 0 H 0 V 34', fill: 'none', stroke: '#294049', 'stroke-width': 1 }));
  defs.append(grid);
  map.replaceChildren(defs, svg('rect', { width: 760, height: 420, fill: 'url(#grid)' }));
  if (background) {
    const image = mapImage(background.bounds, '/maps/' + background.image, .83);
    image.id = ortho ? 'orthophoto-background' : 'satellite-background';
    map.append(image);
  }
  if (overview && city) {
    map.append(mapImage(city.bounds, '/maps/warsaw.svg?v=' + encodeURIComponent(city.osmTimestamp), scene ? .48 : 1));
  } else if (!overview) {
    for (const line of (scene ? [] : state.network.waterways || [])) {
      map.append(svg('polyline', { points: pointsOnMap(line), fill: 'none', stroke: '#285469', 'stroke-width': 18 }));
    }
    for (const line of state.network.contextRoads || []) {
      map.append(svg('polyline', { points: pointsOnMap(line), fill: 'none',
        stroke: scene ? '#9aacac' : '#38525a', 'stroke-opacity': scene ? .55 : 1, 'stroke-width': 1.15 }));
    }
  }
  const hazards = state.situation.hazards || [];
  $('observed-legend').hidden = !hazards.some(h => h.productId);
  for (const hazard of hazards) {
    const observed = Boolean(hazard.productId);
    for (const polygon of polygons(hazard.geometry)) {
      map.append(svg('path', { d: shape(polygon), 'fill-rule': 'evenodd',
        fill: observed ? '#2372b0' : '#285b6d', 'fill-opacity': observed ? .45 : .6,
        stroke: observed ? '#79c4ff' : '#5c9fb1', 'stroke-dasharray': observed ? 'none' : '5 4',
        'stroke-width': 1.8 }));
    }
  }
  const archive = scene?.floodArchive;
  $('archive-legend').hidden = !archive;
  if (archive) {
    for (const polygon of polygons(archive.geometry)) {
      map.append(svg('path', { class: 'archive-flood', d: shape(polygon), 'fill-rule': 'evenodd',
        fill: '#eead83', 'fill-opacity': .5, stroke: '#ffe0cb', 'stroke-width': 1.5 }));
    }
  }
  const pending = (state.situation.reviewFeatures || []).filter(feature => feature.status === 'Pending');
  $('review-legend').hidden = !pending.length;
  for (const feature of pending) {
    for (const polygon of polygons(feature.geometry)) {
      map.append(svg('path', { d: shape(polygon), 'fill-rule': 'evenodd', fill: '#9c67c2', 'fill-opacity': .38,
        stroke: '#e0abff', 'stroke-width': 2.4, 'stroke-dasharray': '5 4' }));
    }
  }
  if (city) {
    for (const feature of (state.situation.reviewFeatures || []).filter(item =>
      item.status === 'Approved' && item.segmentIds.some(id => id.startsWith('osm-') && closed.has(id)))) {
      for (const polygon of polygons(feature.geometry)) {
        map.append(svg('path', { d: shape(polygon), 'fill-rule': 'evenodd', fill: '#e27169', 'fill-opacity': .35,
          stroke: '#ff9e8f', 'stroke-width': 2, 'stroke-dasharray': '4 3' }));
      }
    }
  }
  map.append(svg('path', { d: 'M 713 62 V 29 M 706 38 L 713 29 L 720 38', stroke: '#a4babf', fill: 'none', 'stroke-width': 2 }));
  map.append(svg('text', { x: 708, y: 82, class: 'map-caption' }, 'N'));
  for (const segment of state.network.segments) {
    if (!(segment.walkingSeconds > 0)) continue;
    const geometry = segment.geometry ? pointsOnMap(segment.geometry)
      : [points.get(segment.from), points.get(segment.to)].map(point => point.join(',')).join(' ');
    const inaccessible = closed.has(segment.id);
    const active = selected.has(segment.id);
    map.append(svg('polyline', {
      points: geometry, fill: 'none', stroke: inaccessible ? '#ff9e8f' : active ? '#8df0ce' : '#89a4a9',
      'stroke-width': inaccessible || active ? 6 : 3, 'stroke-linecap': 'round',
      'stroke-dasharray': inaccessible ? '10 7' : 'none',
    }));
  }
  for (const [id, [x, y]] of (overview ? [] : points)) {
    const isExit = state.network.exits.includes(id);
    const isStart = id === $('node').value;
    map.append(svg('circle', { cx: x, cy: y, r: isStart ? 9 : 6,
      fill: isExit ? '#fbd47e' : isStart ? '#8df0ce' : '#b7cbce', stroke: '#12232a', 'stroke-width': 2 }));
    const caption = warsaw ? warsawMapLabels[id] : [
      label(id), id === 'pickup' ? -10 : 10, id === 'pickup' || id === 'east-junction' ? 26 : -14,
    ];
    if (caption) {
      const [name, dx, dy] = caption;
      map.append(svg('text', { x: x + dx, y: y + dy, 'text-anchor': dx < 0 ? 'end' : 'start',
        class: isExit || isStart ? '' : 'minor-label' }, name));
    }
  }
  if (state.distribution && state.cityPath?.revision === state.situation.revision && state.cityPath.route?.geometry.length) {
    map.append(svg('polyline', { points: pointsOnMap(state.cityPath.route.geometry), fill: 'none',
      stroke: '#8df0ce', 'stroke-width': 4, 'stroke-linecap': 'round' }));
  }
  for (const point of state.cityPoints) {
    const [cx, cy] = project(...point);
    map.append(svg('circle', { cx, cy, r: 5, fill: '#fbd47e', stroke: '#12232a', 'stroke-width': 2 }));
  }
  if (state.cityPath && !state.distribution) {
    text('city-status', 'Trasa oczekuje na ponowną weryfikację.');
  }
}

function renderReviews(reviews) {
  const list = $('reviews');
  list.replaceChildren();
  if (!reviews.length) list.append(element('li', 'Brak detekcji dla scenariusza.'));
  for (const feature of reviews) {
    const item = element('li');
    const details = element('div');
    const confidence = feature.confidence == null ? '' : ' · ' + Math.round(feature.confidence * 100) + '% pewności';
    const status = { Pending: 'Do weryfikacji', Approved: 'Zatwierdzona', Rejected: 'Odrzucona' }[feature.status];
    const provenance = feature.mode === 'synthetic' ? 'Symulacja' : 'FlyPix AI · ' + (feature.mode === 'live' ? 'API' : 'zapis');
    const corridors = feature.segmentIds.filter(id => !id.startsWith('osm-')).map(segmentLabel);
    const roads = feature.segmentIds.length - corridors.length;
    if (roads) corridors.push(roads + ' odc. OSM');
    details.append(element('b', corridors.join(' · ') || 'Brak dopasowania do dróg'),
      element('p', status + ' · ' + provenance + confidence),
      element('p', feature.imageProductId + ' · ' + feature.observedAt));
    item.append(details);
    if (feature.status === 'Pending') {
      const decide = decision => api('situation', 'commands/review-decision', {
        commandId: commandId(), expectedRevision: state.situation.revision, reviewFeatureId: feature.id, decision,
      }).then(() => decision === 'approve' ? 'Zatwierdzono zamknięcie wykrytych odcinków.' : 'Odrzucono detekcję.');
      const controls = element('div', null, 'buttons');
      if (feature.segmentIds.length) controls.append(button('Zatwierdź zamknięcie', () => decide('approve')));
      controls.append(button('Odrzuć', () => decide('reject')));
      item.append(controls);
    }
    list.append(item);
  }
}

function redrawMap() {
  renderMap(state.draw?.state === 'Active' ? state.draw.route : null);
}

function requestCityPath() {
  const [from, to] = state.cityPoints.map(value => value.map(number => number.toFixed(6)).join(','));
  const query = new URLSearchParams({ from, to, mode: $('city-mode').value });
  return api('routing', 'queries/city-route?' + query);
}

function showCityPath(path) {
  state.cityPath = path;
  const found = path.route;
  text('city-status', found ? 'Trasa orientacyjna · ' + minutes(found.seconds) : 'Brak trasy w wybranym trybie.');
}

$('city-overview').addEventListener('click', () => { state.cityOverview = true; redrawMap(); });
$('city-detail').addEventListener('click', () => { state.cityOverview = false; redrawMap(); });
$('map-layer').addEventListener('click', () => {
  state.satelliteDetail = !state.satelliteDetail;
  redrawMap();
});
$('city-pick').addEventListener('click', () => {
  state.cityPicking = !state.cityPicking;
  state.cityPoints = [];
  state.cityPath = null;
  $('city-pick').setAttribute('aria-pressed', state.cityPicking);
  text('city-status', state.cityPicking ? 'Wskaż punkt startowy na mapie.' : '');
  redrawMap();
});
$('city-mode').addEventListener('change', () => {
  state.cityPicking = false;
  state.cityPoints = [];
  state.cityPath = null;
  $('city-pick').setAttribute('aria-pressed', 'false');
  text('city-status', 'Wskaż punkty trasy dla wybranego trybu.');
  redrawMap();
});
$('map').addEventListener('click', event => {
  if (!state.cityPicking || state.busy) return;
  const svgPoint = new DOMPoint(event.clientX, event.clientY).matrixTransform($('map').getScreenCTM().inverse());
  const point = state.unproject(svgPoint.x, svgPoint.y);
  const [w, s, e, n] = state.network.city.bounds;
  if (point[0] < w || point[0] > e || point[1] < s || point[1] > n) return;
  state.cityPoints.push(point);
  redrawMap();
  if (state.cityPoints.length === 1) {
    text('city-status', 'Wskaż cel na mapie.');
    return;
  }
  state.cityPicking = false;
  $('city-pick').setAttribute('aria-pressed', 'false');
  run(async () => {
    const path = await requestCityPath();
    if (state.cityPoints.length === 2) showCityPath(path);
    return 'Sprawdzono dojazd w bieżącej rewizji.';
  });
});

function renderOperations(incident, dispatch) {
  text('alert-count', 'Liczba zgłoszeń: ' + incident.alerts.length);
  $('cases').replaceChildren(...incident.cases.map(item => {
    const row = element('li');
    const info = element('div');
    info.append(element('b', item.name || item.caseId), element('p', (statuses[item.progress] || item.progress) + ' · ' + label(item.node)));
    row.append(info);
    const actions = element('div', null, 'buttons');
    if (item.mobility === 'Walking' && item.progress !== 'ExitConfirmed') {
      actions.append(button('Pokaż rozkład', () => {
        if (state.subject?.caseId !== item.caseId || $('node').value !== item.node) {
          if (![...$('node').options].some(option => option.value === item.node))
            $('node').add(new Option(label(item.node), item.node));
          $('node').value = item.node;
          state.draw = null;
          state.pendingDraw = null;
        }
        state.subject = item;
        text('recipient', 'Przejścia piesze · ' + item.name);
      }));
    }
    if (item.progress !== 'ExitConfirmed' && !dispatch.requests.some(r => r.caseId === item.caseId)) {
      actions.append(button('Zgłoś potrzebę pomocy', () => api('dispatch', 'commands/request', {
        commandId: commandId(), caseId: item.caseId, pickupNode: item.node,
      }).then(() => 'Zapisano potrzebę pomocy.')));
    }
    const step = { Open: ['acknowledgement', 'Potwierdź odbiór'], Acknowledged: ['moving', 'Zgłoś przejście'], Moving: ['exit-confirmed', 'Potwierdź wyjście'] }[item.progress];
    if (step) actions.append(button(step[1], () => api('management', 'commands/progress', {
      commandId: commandId(), caseId: item.caseId, step: step[0], expectedVersion: item.version,
    }).then(() => 'Zapisano status mieszkańca.')));
    if (actions.childElementCount) row.append(actions);
    return row;
  }));

  const unit = dispatch.units.find(item => item.unitId === 'unit-3');
  const occupied = dispatch.assignments.some(a => a.unitId === 'unit-3' && !['Completed', 'Cancelled'].includes(a.state));
  text('unit-status', unit ? unit.name + ' · ' + (occupied ? 'w zadaniu' : 'gotowa') : 'Brak jednostki');
  $('requests').replaceChildren(...dispatch.requests.map(item => {
    const row = element('li');
    const info = element('div');
    const assignment = dispatch.assignments.find(a => a.requestId === item.requestId && a.state !== 'Cancelled');
    info.append(element('b', 'Pomoc · ' + item.caseId), element('p',
      (statuses[assignment?.state] || assignment?.state || 'Oczekuje na jednostkę') + ' · ' + label(item.pickupNode)));
    row.append(info);
    const actions = element('div', null, 'buttons');
    if (!assignment && !occupied && unit) actions.append(button('Przydziel', () =>
      api('dispatch', 'commands/assign', { commandId: commandId(), requestId: item.requestId, unitId: unit.unitId,
        expectedVersion: item.version }).then(() => 'Zarezerwowano jednostkę.')));
    if (assignment?.route) {
      const route = assignment.route;
      const active = !['Completed', 'Cancelled'].includes(assignment.state);
      const routeText = assignment.routeCurrent === true
        ? label(route.exit) + ' · dojazd ' + minutes(route.approachSeconds) + ' · wyjazd ' + minutes(route.outboundSeconds)
        : active ? 'Trasa jednostki wymaga ponownej weryfikacji.' : null;
      if (routeText) info.append(element('p', routeText));
      const change = (op, extra) => api('dispatch', 'commands/' + op, { commandId: commandId(),
        assignmentId: assignment.assignmentId, expectedVersion: assignment.version, ...extra })
        .then(() => 'Zapisano stan zadania.');
      if (active && assignment.routeCurrent !== true) actions.append(button('Przelicz trasę jednostki', () => change('replan')));
      const step = { Assigned: ['acknowledge', 'Potwierdź zadanie'], Acknowledged: ['arrive', 'Zgłoś przyjazd'],
        AtPickup: ['pickup', 'Potwierdź odbiór'], PickupConfirmed: ['complete', 'Zakończ zadanie'] }[assignment.state];
      if (step && assignment.routeCurrent === true) actions.append(button(step[1], () => change('progress', { step: step[0] })));
    }
    if (actions.childElementCount) row.append(actions);
    return row;
  }));
}

function renderSources(hazards) {
  const imported = hazards.filter(h => h.productId);
  text('sources', imported.length
    ? imported.map(h => (h.source.includes('sentinel-1') ? 'Sentinel-1' : 'Copernicus EMS') +
      ' · ' + h.productId + ' · ' + h.observedAt +
      (h.provenance?.mode === 'recorded' ? ' · Dane zapisane' : '')).join(' | ')
    : 'Zasięg zalania pochodzi ze scenariusza ćwiczeniowego.');
}

const closure = (segmentId, closed, expectedRevision = state.situation.revision) =>
  api('situation', 'commands/closure', { commandId: commandId(), segmentId, closed, expectedRevision });

$('node').addEventListener('change', () => run(() => {
  state.draw = null;
  state.pendingDraw = null;
  state.subject = null;
  text('recipient', 'Przejścia piesze z wybranego węzła');
}));
$('bridge').addEventListener('click', () => run(async () => { await closure('bridge-2', true); return 'Most został zamknięty.'; }));
$('north').addEventListener('click', () => run(async () => { await closure('north-link', true); return 'Północne dojście zostało zamknięte.'; }));
$('reopen').addEventListener('click', () => run(async () => {
  let latest = state.situation;
  for (const id of latest.closedSegments) latest = await closure(id, false, latest.revision);
  state.draw = null;
  state.pendingDraw = null;
  return 'Przywrócono dostęp do przejść.';
}));
$('draw').addEventListener('click', () => run(async () => {
  const { revision, node, at } = state.distribution;
  const subjectId = state.subject?.caseId;
  if (state.pendingDraw?.revision !== revision)
    state.pendingDraw = { commandId: commandId(), node, revision, at, subjectId };
  try {
    const result = await api('routing', 'commands/draw', state.pendingDraw);
    state.draw = { id: state.pendingDraw.commandId, state: 'Active', ...result };
    state.pendingDraw = null;
  } catch (error) {
    if (error.status >= 400 && error.status < 500) state.pendingDraw = null;
    throw error;
  }
  return 'Węzeł przydzielił trasę z aktualnego rozkładu.';
}));
$('alert').addEventListener('click', () => run(() =>
  api('management', 'commands/alert', { commandId: commandId() }).then(() => 'Zapisano zgłoszenie zalania.')));
await run(() => {});
setInterval(async () => {
  if (state.busy || state.polling) return;
  state.polling = true;
  const epoch = state.epoch;
  const wasUnavailable = !state.distribution;
  try {
    await refresh(epoch);
    if (state.epoch === epoch && wasUnavailable && state.distribution)
      notice('Przywrócono aktualny widok scenariusza.');
  } catch {
    if (state.epoch === epoch) {
      invalidateRead();
      notice('Widok czeka na odpowiedź lokalnych usług. Odświeżenie nastąpi automatycznie.', true);
    }
  } finally {
    state.polling = false;
    if (state.epoch === epoch) syncControls();
  }
}, 7000);
