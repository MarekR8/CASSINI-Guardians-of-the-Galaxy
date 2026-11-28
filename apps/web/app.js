import { createMapViewport } from './map-viewport.js';

const $ = id => document.getElementById(id);
const satelliteScenes = fetch('/maps/satellite.json')
  .then(response => response.ok ? response.json() : null)
  .catch(() => null);
const state = {
  network: null, situation: null, incident: null, dispatch: null, distribution: null, draw: null, readAvailable: false,
  cityImagery: null,
  pendingDraw: null, subject: null, busy: false, epoch: 0, polling: false,
  operationsSignature: null, sourcesSignature: null,
  dispatchSelections: new Map(), dispatchErrors: new Map(),
  satelliteScene: null, satelliteDetail: false,
};
const mapViewport = createMapViewport($('map'), {
  zoomIn: $('map-zoom-in'), zoomOut: $('map-zoom-out'), reset: $('map-zoom-reset'),
  maxZoom: () => state.network?.city ? 32 : 5,
});
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
const label = id => names[id] || id;
const segmentLabel = id => ({
  'bridge-2': 'Most Świętokrzyski', 'north-link': 'Dojście północne',
  'walk-to-pickup': 'Dojście do punktu przesiadkowego', 'east-link': 'Dojazd do mostu',
  'north-exit-link': 'Dojście do wyjścia', 'outside-connector': 'Objazd dla jednostki',
})[id] || id;
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
    notice(message || (state.network?.scenario === 'valencia'
      ? 'Zalanie z października 2024 r. · archiwalna obserwacja Copernicus EMS'
      : 'Scenariusz gotowy. Wybierz węzeł i wylosuj trasę.'));
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
  state.readAvailable = false;
  if (state.draw) state.draw = { id: state.draw.id, state: 'Unknown', route: null };
  if (state.network?.scenario === 'valencia') redrawMap();
  else renderDistribution();
  if (state.incident && state.dispatch) {
    const assignments = state.dispatch.assignments.map(assignment => assignment.active
      ? { ...assignment, routeCurrent: null,
        routeStatus: { state: 'Unverified', code: 'SituationUnavailable' } }
      : assignment);
    renderOperations(state.incident, { ...state.dispatch, assignments });
    state.operationsSignature = null;
  }
}

async function refresh(epoch) {
  const network = state.network ?? await api('routing', 'queries/network');
  const valencia = network.scenario === 'valencia';
  const [situation, incident, dispatch, scenes, cityImagery] = await Promise.all([
    api('situation', 'queries/snapshot'),
    valencia ? null : api('management', 'queries/incident'),
    valencia ? null : api('dispatch', 'queries/assignments'),
    satelliteScenes,
    valencia ? state.cityImagery ?? fetch('/maps/city-imagery.json').then(response => {
      if (!response.ok) throw new Error('Brak lokalnych metadanych scen Walencji.');
      return response.json();
    }) : null,
  ]);
  if (state.epoch !== epoch) return;
  const node = $('node').value;
  const revisionChanged = state.situation?.revision !== situation.revision;
  const [distribution, draw] = await Promise.all([
    valencia ? null : api('routing', 'queries/distribution?node=' + encodeURIComponent(node)),
    state.draw ? api('routing', 'queries/draws/' + state.draw.id) : null,
  ]);
  if (state.epoch !== epoch) return;
  if ([distribution, draw].some(view => view && view.revision !== situation.revision))
    throw new Error('Sytuacja zmieniła się podczas odczytu.');

  const restored = !state.readAvailable;
  state.readAvailable = true;
  state.network = network;
  state.situation = situation;
  state.incident = incident;
  state.dispatch = dispatch;
  state.cityImagery = cityImagery;
  document.body.classList.toggle('valencia', valencia);
  for (const id of ['exercise-controls', 'exercise-routing', 'exercise-operations', 'scenario-time'])
    $(id).hidden = valencia;
  $('valencia-tools').hidden = !valencia;
  if (valencia) $('map').setAttribute('aria-label', 'Mapa zalania i dróg w obszarze zdjęć Sentinel-1.');
  state.distribution = distribution;
  if (draw) state.draw = { id: state.draw.id, ...draw };
  const scene = valencia ? null : scenes?.[network.scenario];
  state.satelliteScene = scene;
  text('incident', valencia ? 'DEMO · POWÓDŹ · WALENCJA'
    : 'DEMO · POWÓDŹ · ' + incident.incidentId);
  text('map-area', valencia ? 'Walencja · obszar Sentinel-1'
    : 'Kłodzko · obszar ćwiczeń');
  $('map-credit').hidden = !valencia;
  if (scene) {
    $('satellite-source').href = scene.sourceUrl;
    text('satellite-source', 'Contains modified Copernicus Sentinel data ' + scene.observedAt.slice(0, 4));
    text('satellite-date', ' · Sentinel-2 L2A · obserwacja ' +
      formatObservation.format(new Date(scene.observedAt)) + ' UTC');
  }
  const archive = scene?.floodArchive;
  $('archive-credit').hidden = !archive;
  $('archive-tools').hidden = !archive;
  if (archive) {
    const source = archive.label ?? 'EMS';
    $('archive-source').href = archive.sourceUrl;
    text('archive-source', '© European Union, Copernicus EMS · ' + (archive.label ? source + ' · ' : '') + archive.productId);
    text('archive-label', 'Zalanie archiwalne · ' + source);
    text('archive-date', ' · obserwacja ' + formatObservation.format(new Date(archive.observedAt)) + ' UTC' +
      (archive.resolutionMeters ? ' · ' + archive.resolutionMeters + ' m' : ''));
  }
  text('revision', 'Rewizja ' + situation.revision);
  if (valencia) {
    if (revisionChanged || restored) redrawMap();
  } else {
    text('clock', time(distribution.at) + ' UTC');
    renderDistribution();
    const operations = JSON.stringify([incident, dispatch]);
    if (operations !== state.operationsSignature) {
      renderOperations(incident, dispatch);
      state.operationsSignature = operations;
    }
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
}

function syncControls() {
  const closed = state.situation?.closedSegments || [];
  $('node').disabled = state.busy || !state.situation;
  $('draw').disabled = state.busy || !state.distribution?.options.length || state.draw?.state === 'Active';
  $('bridge').disabled = state.busy || !state.situation || closed.includes('bridge-2');
  $('north').disabled = state.busy || !closed.includes('bridge-2') || closed.includes('north-link');
  $('reopen').disabled = state.busy || !closed.length;
  $('alert').disabled = state.busy || !state.situation;
  for (const control of $('requests').querySelectorAll('button, select'))
    control.disabled = state.busy || !state.readAvailable;
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

function mapProjection([west, south, east, north]) {
  const ratio = Math.cos((south + north) * Math.PI / 360);
  const scale = Math.min(680 / ((east - west) * ratio), 360 / (north - south));
  return (lon, lat) => [380 + (lon - (west + east) / 2) * ratio * scale,
    210 + ((south + north) / 2 - lat) * scale];
}

function polygonPath(polygon, project) {
  return polygon.map(ring => 'M ' + ring.map(([lon, lat]) => project(lon, lat).join(',')).join(' L ') + ' Z').join(' ');
}

const integrationButtons = $('integration-preview').querySelectorAll('[data-integration]');
const integrationSummaries = {
  'integration-copernicus': result => `${result.productId} · ${formatObservation.format(new Date(result.observedAt))} UTC`,
  'integration-flypix': result => `${result.geoJson.features.length} detekcji do weryfikacji · raster ${result.imageProductId}`,
  'city-data': result => result.zones.map(zone => `${zone.zoneId} · liczba osób: ${zone.people}`).join(', '),
  'city-map': result => `${result.datasetId} · ${result.geoJson.features.length} obszarów w GeoJSON`,
};
for (const control of integrationButtons) {
  control.addEventListener('click', async () => {
    for (const button of integrationButtons) button.disabled = true;
    const map = $('integration-map');
    const response = $('integration-response');
    map.setAttribute('hidden', '');
    map.replaceChildren();
    response.hidden = true;
    text('integration-status', 'Pobieranie przykładu…');
    try {
      const operation = control.dataset.integration;
      const result = await api('situation', 'queries/' + operation);
      if (result.geoJson) {
        const project = mapProjection(result.bounds);
        for (const feature of result.geoJson.features) {
          for (const polygon of polygons(feature.geometry)) {
            map.append(svg('path', { d: polygonPath(polygon, project), fill: '#285b6d',
              stroke: '#8df0ce', 'stroke-width': 3, 'fill-rule': 'evenodd' }));
          }
        }
        map.removeAttribute('hidden');
      }
      text('integration-json', JSON.stringify(result, null, 2));
      response.hidden = false;
      text('integration-status', 'Dane syntetyczne · ' + integrationSummaries[operation](result));
    } catch {
      text('integration-status', 'Nie udało się pobrać podglądu integracji.');
    } finally {
      for (const button of integrationButtons) button.disabled = false;
    }
  });
}

function updateMapDetail() {
  const map = $('map');
  const { x, y, width, height } = map.viewBox.baseVal;
  const city = map.classList.contains('map-city');
  const detailImage = $('orthophoto-background');
  const closeView = width <= 760 / 5;
  let detailVisible = Boolean(detailImage && !state.satelliteDetail);
  if (city && detailVisible) {
    const { x: left, y: top, width: spanX, height: spanY } = detailImage.getBBox();
    detailVisible = closeView && x < left + spanX && x + width > left &&
      y < top + spanY && y + height > top;
  }
  if (detailImage) detailImage.classList.toggle('map-hidden', !detailVisible);
  map.classList.toggle('map-detailed', !city || closeView);
  $('ortho-credit').hidden = !detailVisible;
  $('satellite-credit').hidden = !state.satelliteScene || (detailVisible && !city);
  $('map-layer-tools').hidden = !detailImage || (city && !detailVisible && !state.satelliteDetail);
  text('map-layer', state.satelliteDetail ? 'Pokaż ortofotomapę' : 'Pokaż Sentinel-2');
}

$('map').addEventListener('map-view-change', updateMapDetail);

function renderMap(route) {
  const map = $('map');
  if (!state.network) return;
  const valencia = state.network.scenario === 'valencia';
  const scene = state.satelliteScene;
  const city = state.network.city;
  const bounds = valencia ? state.cityImagery.scenes.event.bounds : scene?.viewBounds;
  map.classList.toggle('map-city', valencia);
  const [west, south, east, north] = bounds || [16.646, 50.431, 16.660, 50.442];
  const project = mapProjection([west, south, east, north]);
  const pointsOnMap = line => line.map(([lon, lat]) => project(lon, lat).join(',')).join(' ');
  const mapImage = ([left, bottom, right, top], href, opacity) => {
    const [x, y] = project(left, top);
    const [x2, y2] = project(right, bottom);
    return svg('image', { href, x, y, width: x2 - x, height: y2 - y, preserveAspectRatio: 'none', opacity });
  };
  const defs = svg('defs', {});
  const grid = svg('pattern', { id: 'grid', width: 34, height: 34, patternUnits: 'userSpaceOnUse' });
  grid.append(svg('path', { d: 'M 34 0 H 0 V 34', fill: 'none', stroke: '#294049', 'stroke-width': 1 }));
  defs.append(grid);
  const canvas = valencia ? svg('g', { 'clip-path': 'url(#valencia-extent)' }) : map;
  map.replaceChildren(defs);
  if (valencia) {
    const [x, y] = project(west, north);
    const [right, bottom] = project(east, south);
    const extent = { x, y, width: right - x, height: bottom - y };
    const clip = svg('clipPath', { id: 'valencia-extent' });
    clip.append(svg('rect', extent));
    defs.append(clip);
    map.append(canvas);
    mapViewport.setExtent(extent);
  }
  canvas.append(svg('rect', { width: 760, height: 420, fill: 'url(#grid)' }));
  if (scene) {
    const image = mapImage(scene.bounds, '/maps/' + scene.image, .83);
    image.id = 'satellite-background';
    map.append(image);
    if (scene.detail) {
      const orthophoto = mapImage(scene.detail.bounds, '/maps/' + scene.detail.image, .95);
      orthophoto.id = 'orthophoto-background';
      map.append(orthophoto);
      $('ortho-source').href = scene.detail.sourceUrl;
    }
  }
  if (valencia) {
    const selected = $('valencia-image').value;
    const radar = state.cityImagery.scenes[selected];
    if (radar) {
      const image = mapImage(radar.bounds, '/maps/sentinel-' + selected + '.jpg', .82);
      image.id = 'valencia-radar';
      canvas.append(image);
    }
  }
  if (city) {
    const roads = mapImage(city.bounds, '/maps/city.svg?v=' + encodeURIComponent(city.osmTimestamp), scene ? .48 : 1);
    roads.classList.add('map-city-roads');
    canvas.append(roads);
  }
  const layers = state.situation.hazards || [];
  const hazards = valencia ? layers.filter(hazard => hazard.source === 'copernicus-ems-archive') : layers;
  $('observed-legend').hidden = !hazards.some(h => h.productId);
  for (const hazard of hazards) {
    const observed = Boolean(hazard.productId);
    for (const polygon of polygons(hazard.geometry)) {
      canvas.append(svg('path', { d: polygonPath(polygon, project), 'fill-rule': 'evenodd',
        fill: observed ? (valencia ? '#bb392f' : '#2372b0') : '#285b6d',
        'fill-opacity': observed ? (valencia ? .36 : .45) : .6,
        stroke: observed ? (valencia ? '#ff7663' : '#79c4ff') : '#5c9fb1', 'stroke-dasharray': observed ? 'none' : '5 4',
        'stroke-width': 1.8 }));
    }
  }
  if (valencia) {
    updateMapDetail();
    return;
  }

  const points = new Map(state.network.nodes.map(node => [node.id, project(node.longitude, node.latitude)]));
  const closed = new Set(state.situation.closedSegments);
  const selected = new Set(route?.segments || []);
  const archive = scene?.floodArchive;
  $('archive-legend').hidden = !archive;
  if (archive) {
    const group = svg('g', { id: 'archive-flood-layer' });
    for (const polygon of polygons(archive.geometry)) {
      group.append(svg('path', { class: 'archive-flood', d: polygonPath(polygon, project), 'fill-rule': 'evenodd',
        fill: '#eead83', 'fill-opacity': .5, stroke: '#ffe0cb', 'stroke-width': 1.5 }));
    }
    map.append(group);
  }
  map.append(svg('path', { d: 'M 713 62 V 29 M 706 38 L 713 29 L 720 38', stroke: '#a4babf', fill: 'none', 'stroke-width': 2 }));
  map.append(svg('text', { x: 708, y: 82, class: 'map-caption' }, 'N'));
  const routes = [];
  for (const segment of state.network.segments) {
    if (!(segment.walkingSeconds > 0)) continue;
    const geometry = segment.geometry ? pointsOnMap(segment.geometry)
      : [points.get(segment.from), points.get(segment.to)].map(point => point.join(',')).join(' ');
    const inaccessible = closed.has(segment.id);
    const active = selected.has(segment.id);
    const line = {
      points: geometry, fill: 'none', stroke: inaccessible ? '#ff9e8f' : active ? '#8df0ce' : '#89a4a9',
      'stroke-width': inaccessible || active ? 4 : 1.5, 'stroke-linecap': 'round', 'stroke-linejoin': 'round',
      'stroke-dasharray': inaccessible ? '10 7' : 'none',
    };
    if (inaccessible || active) routes.push(line);
    else map.append(svg('polyline', line));
  }
  for (const assignment of state.dispatch?.assignments ?? []) {
    const verifiedRevision = assignment.routeStatus?.revision ?? assignment.route.revision;
    if (!state.distribution || assignment.routeCurrent !== true || verifiedRevision !== state.situation.revision)
      continue;
    for (const [geometry, dashed] of [[assignment.route.approachGeometry, true], [assignment.route.outboundGeometry, false]]) {
      if (!geometry?.length) continue;
      if (dashed && ['AtPickup', 'PickupConfirmed'].includes(assignment.state)) continue;
      routes.push({ points: pointsOnMap(geometry), fill: 'none', stroke: '#fbd47e', 'stroke-width': 4,
        'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'stroke-dasharray': dashed ? '7 5' : 'none' });
    }
  }
  for (const line of routes) {
    map.append(svg('polyline', { ...line, stroke: '#102026', 'stroke-width': 7 }));
    map.append(svg('polyline', { ...line, class: 'map-route' }));
  }
  for (const [id, [x, y]] of points) {
    const isExit = state.network.exits.includes(id);
    const isStart = id === $('node').value;
    const marker = svg('g', { class: 'map-node', transform: 'translate(' + x + ' ' + y + ')' });
    const symbol = svg('g', { class: 'map-symbol' });
    marker.append(symbol);
    symbol.append(svg('circle', { r: isStart ? 9 : 6,
      fill: isExit ? '#fbd47e' : isStart ? '#8df0ce' : '#b7cbce', stroke: '#12232a', 'stroke-width': 2 }));
    const caption = [
      label(id), id === 'pickup' ? -10 : 10, id === 'pickup' || id === 'east-junction' ? 26 : -14,
    ];
    if (caption) {
      const [name, dx, dy] = caption;
      symbol.append(svg('text', { x: dx, y: dy, 'text-anchor': dx < 0 ? 'end' : 'start',
        class: isExit || isStart ? '' : 'minor-label' }, name));
    }
    map.append(marker);
  }
  updateMapDetail();
}

function redrawMap() {
  renderMap(state.draw?.state === 'Active' ? state.draw.route : null);
}

$('valencia-image').addEventListener('change', redrawMap);
$('archive-focus').addEventListener('click', () => {
  const layer = $('archive-flood-layer');
  if (layer) mapViewport.fit(layer.getBBox());
});
$('map-layer').addEventListener('click', () => {
  state.satelliteDetail = !state.satelliteDetail;
  updateMapDetail();
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

  const available = dispatch.units.filter(unit => unit.state === 'Available');
  text('unit-status', 'Dostępne jednostki: ' + available.length + ' z ' + dispatch.units.length);
  $('requests').replaceChildren(...dispatch.requests.map(item => {
    const row = element('li');
    const info = element('div');
    const assignment = dispatch.assignments.find(a => a.requestId === item.requestId && a.state !== 'Cancelled');
    info.append(element('b', 'Pomoc · ' + item.caseId), element('p',
      (statuses[assignment?.state] || assignment?.state || 'Oczekuje na jednostkę') + ' · ' + label(item.pickupNode)));
    row.append(info);
    const actions = element('div', null, 'buttons');
    const change = (op, fields, message) => dispatchAction(item.requestId,
      () => api('dispatch', 'commands/' + op, { commandId: commandId(), ...fields }), message);
    const problem = state.dispatchErrors.get(item.requestId);
    if (problem) info.append(element('p', problem, 'dispatch-problem'));
    if (!assignment && available.length) {
      const select = element('select');
      select.setAttribute('aria-label', 'Jednostka dla ' + item.caseId);
      for (const unit of available) {
        const option = element('option', unit.name + ' · ' + unitPositionLabel(unit));
        option.value = unit.unitId;
        select.append(option);
      }
      const saved = state.dispatchSelections.get(item.requestId);
      if (available.some(unit => unit.unitId === saved)) select.value = saved;
      select.addEventListener('change', () => state.dispatchSelections.set(item.requestId, select.value));
      actions.append(select, button('Przydziel', () => {
        const unit = available.find(unit => unit.unitId === select.value);
        return withConfirmedUnitPosition(unit, confirmed => change('assign', {
          requestId: item.requestId, unitId: unit.unitId, expectedVersion: item.version, ...confirmed,
        }, 'Zarezerwowano jednostkę.'));
      }));
    } else if (!assignment) {
      info.append(element('p', 'Wszystkie jednostki są zajęte.'));
    }
    if (assignment?.route) {
      const unit = dispatch.units.find(unit => unit.unitId === assignment.unitId);
      if (unit) info.append(element('p', unit.name + ' · ' + unitPositionLabel(unit)));
      const route = assignment.route;
      const active = !['Completed', 'Cancelled'].includes(assignment.state);
      const routeText = assignment.routeCurrent === true
        ? label(route.exit) + ' · dojazd ' + minutes(route.approachSeconds) + ' · wyjazd ' + minutes(route.outboundSeconds)
        : active ? dispatchRouteLabel(assignment.routeStatus) : null;
      if (routeText) info.append(element('p', routeText));
      if (route.provenance) info.append(element('p', route.provenance.mode === 'synthetic'
        ? 'Trasa jednostki · symulacja' : 'Trasa jednostki · OpenRouteService'
          + (route.provenance.mode === 'recorded' ? ' · zapis' : '')));
      const changeAssignment = (op, extra) => change(op, {
        assignmentId: assignment.assignmentId, expectedVersion: assignment.version, ...extra,
      }, 'Zapisano stan zadania.');
      if (active && unit && assignment.routeCurrent !== true &&
        !['SituationUnavailable', 'RoutingUnavailable', 'SituationRevisionUnavailable'].includes(assignment.routeStatus?.code))
        actions.append(button('Przelicz trasę jednostki', () =>
          withConfirmedUnitPosition(unit, confirmed => changeAssignment('replan', confirmed))));
      const step = { Assigned: ['acknowledge', 'Potwierdź zadanie'], Acknowledged: ['arrive', 'Zgłoś przyjazd'],
        AtPickup: ['pickup', 'Potwierdź odbiór'], PickupConfirmed: ['complete', 'Zakończ zadanie'] }[assignment.state];
      if (step && assignment.routeCurrent === true)
        actions.append(button(step[1], () => changeAssignment('progress', { step: step[0] })));
    }
    const history = dispatch.assignments.filter(entry => entry.requestId === item.requestId)
      .flatMap(entry => (entry.history ?? []).map(decision => ({ ...decision, unitId: entry.unitId })));
    if (history.length) info.append(dispatchHistory(item.requestId, history));
    if (actions.childElementCount) row.append(actions);
    return row;
  }));
}

const dispatchMessages = {
  NoEligibleRoute: 'Brak dostępnego objazdu. Sprawdź zamknięcia lub wybierz inną jednostkę.',
  FixtureMissing: 'Brakuje nagrania ORS dla tego przebiegu.',
  PositionUnverified: 'Potwierdź punkt startu jednostki.',
  SituationUnavailable: 'Brak połączenia z serwisem sytuacji. Trasa oczekuje na weryfikację.',
  SituationRevisionUnavailable: 'Brakuje aktualnej rewizji sytuacji.',
  RoutingUnavailable: 'Serwis tras jest niedostępny.',
  RouteEvidenceUnavailable: 'Brakuje danych do ponownej weryfikacji trasy.',
  RouteRequiresReview: 'Warunki przejazdu się zmieniły. Przelicz trasę jednostki.',
  SituationChanged: 'Sytuacja zmieniła się podczas sprawdzania trasy. Spróbuj ponownie.',
  UnitUnavailable: 'Ta jednostka została już przydzielona.',
  BookingConflict: 'Jednostka lub zgłoszenie ma już aktywną rezerwację.',
};

function dispatchRouteLabel(status) {
  const roads = status?.segments?.map(label).join(', ');
  if (status?.state === 'Blocked') {
    const closedAt = status.observedAt ? ' Zamknięcie: ' + new Date(status.observedAt).toLocaleString('pl-PL') + '.' : '';
    return 'Trasa wycofana. Zamknięte odcinki: ' + roads + '.' + closedAt;
  }
  if (status?.state === 'ReviewRequired') return 'Dostępność odcinka się zmieniła. Przelicz trasę: ' + roads + '.';
  return dispatchMessages[status?.code] ?? 'Trasa jednostki oczekuje na weryfikację.';
}

const dispatchHistoryOpen = new Set();
function dispatchHistory(requestId, history) {
  const details = element('details');
  details.open = dispatchHistoryOpen.has(requestId);
  details.addEventListener('toggle', () => {
    if (details.open) dispatchHistoryOpen.add(requestId); else dispatchHistoryOpen.delete(requestId);
  });
  const labels = { Assigned: 'Przydział', PositionConfirmed: 'Ręczne potwierdzenie pozycji',
    RouteWithdrawn: 'Wycofanie trasy', Replanned: 'Przeliczenie trasy' };
  const list = element('ul');
  for (const item of [...history].reverse()) {
    const reason = item.segments?.map(label).join(', ');
    list.append(element('li', [new Date(item.at).toLocaleString('pl-PL'), item.unitId,
      labels[item.kind] ?? statuses[item.kind] ?? item.kind, 'rewizja ' + item.revision, reason].filter(Boolean).join(' · ')));
  }
  details.append(element('summary', 'Historia zadania'), list);
  return details;
}

async function dispatchAction(requestId, action, message) {
  try {
    await action();
    state.dispatchErrors.delete(requestId);
    return message;
  } catch (error) {
    const reason = dispatchMessages[error.code] ?? error.message;
    state.dispatchErrors.set(requestId, reason);
    return reason;
  } finally {
    state.operationsSignature = null;
  }
}

function unitPositionLabel(unit) {
  const evidence = unit.positionEvidence;
  if (evidence?.mode === 'manual') return 'pozycja potwierdzona ręcznie';
  if (evidence?.mode === 'synthetic' && evidence.egnosApplied === true && unit.positionVerified)
    return 'EGNOS (symulacja) · HPL ' + evidence.hpl + ' m';
  if (evidence?.mode === 'synthetic' && evidence.hpl != null)
    return 'HPL ' + evidence.hpl + ' m (symulacja) · potwierdź pozycję';
  return 'pozycja wymaga potwierdzenia';
}

function withConfirmedUnitPosition(unit, action) {
  if (unit.positionVerified) return action({});
  if (window.confirm('Potwierdź pozycję jednostki: ' + label(unit.node) + '.'))
    return action({ confirmedStartNode: unit.node });
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
  const wasUnavailable = !state.readAvailable;
  try {
    await refresh(epoch);
    if (state.epoch === epoch && wasUnavailable && state.readAvailable)
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
