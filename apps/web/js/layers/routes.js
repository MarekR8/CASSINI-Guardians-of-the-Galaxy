// Evacuation routes: the drawn route (current / withdrawn / unconfirmed), "no route" at the node and the Valencia city route.
// A withdrawn route stays on the map in its own style (INV-11); stale data never looks like a confirmed route (INV-10).
import { label, minutes, time } from '../format.js';
import { routeGeometry, state } from '../state.js';
import {
  badgeMarker, casedLine, defineLayer, latLng, L, nodePositions, PANES, segmentPoints, sink, staleServices,
  unchangedValencia,
} from './common.js';

const SERVICES = ['routing', 'situation'];
const styles = {
  current: { className: 'layer-route-current', color: '#8df0ce', weight: 4, lineCap: 'round', lineJoin: 'round' },
  withdrawn: { className: 'layer-route-withdrawn', color: '#ff9f43', weight: 5, lineCap: 'round', lineJoin: 'round',
    dashArray: '1 9' },
  unconfirmed: { className: 'layer-route-unconfirmed', color: '#9aa5a8', weight: 4, lineCap: 'round',
    lineJoin: 'round', dashArray: '6 6' },
};
// The routing service returns a withdrawn draw without its route, so the last active route is kept in memory.
let lastActive = null;

function drawState(vm) {
  const draw = vm.draw;
  if (!draw) {
    lastActive = null;
    return null;
  }
  if (draw.state === 'Active' && draw.route) {
    lastActive = { id: draw.id, route: draw.route, revision: draw.revision ?? vm.revision };
    return { kind: 'current', route: draw.route, revision: lastActive.revision };
  }
  const remembered = lastActive?.id === draw.id ? lastActive : null;
  if (draw.state === 'Withdrawn') {
    // DrawStatus.revision is the current situation revision, so keep the first revision at which this page saw
    // the withdrawal; a later closure must not move it.
    if (remembered && remembered.withdrawnAt == null) remembered.withdrawnAt = draw.revision ?? vm.revision;
    return { kind: 'withdrawn', route: remembered?.route, revision: remembered?.revision,
      withdrawnAt: remembered?.withdrawnAt ?? draw.revision };
  }
  if (draw.state === 'Unknown') return { kind: 'unconfirmed', route: remembered?.route, revision: remembered?.revision };
  return { kind: draw.state.toLowerCase(), route: null };
}

// State of the Valencia OSM test route picked on the map.
function cityState(vm) {
  const path = state.cityPath;
  if (!path) return null;
  if (!vm.readAvailable || path.revision !== vm.revision)
    return { kind: 'unconfirmed', route: path.route, revision: path.revision };
  return { kind: path.route?.geometry?.length ? 'current' : 'none', route: path.route, revision: path.revision };
}

function middle(points) {
  return points[Math.floor((points.length - 1) / 2)];
}

function update(vm, { layer, info }) {
  if (unchangedValencia(vm, info)) return;
  layer.clearLayers();
  const add = sink(layer);
  const stale = staleServices(vm, SERVICES).length > 0;
  const network = vm.network;
  const drawn = drawState(vm);
  if (drawn?.route) {
    let segments = drawn.route.segments.map(id => segmentPoints(network, id)).filter(Boolean);
    if (!segments.length && routeGeometry.get(vm.draw?.id)?.length > 1) segments = [routeGeometry.get(vm.draw.id)];
    const entry = drawn.kind === 'current' ? 'path' : drawn.kind === 'withdrawn' ? 'withdrawn' : 'unconfirmed';
    const pane = drawn.kind === 'withdrawn' ? PANES.withdrawn : PANES.routes;
    for (const points of segments)
      add(entry, casedLine(points, styles[drawn.kind] ?? styles.unconfirmed, pane, stale && drawn.kind === 'current'));
    if (drawn.kind !== 'current' && segments.length) {
      add(entry, badgeMarker(latLng(middle(segments[Math.floor(segments.length / 2)])),
        drawn.kind === 'withdrawn' ? 'Trasa wycofana' : 'Niepotwierdzona',
        drawn.kind === 'withdrawn' ? 'badge-withdrawn' : 'badge-stale'));
    }
  }
  const distribution = vm.distribution;
  if (distribution && !distribution.options.length) {
    const point = nodePositions(network).get(distribution.node);
    if (point) add('no-route', [
      L.circleMarker(latLng(point), { pane: PANES.markers, interactive: false, className: 'layer-no-route', radius: 15,
        fill: false, color: '#ff5a4e', weight: 3, dashArray: '3 4' }),
      badgeMarker(latLng(point), 'Brak trasy', 'badge-no-route'),
    ]);
  }
  const city = cityState(vm);
  if (city?.route?.geometry?.length && city.kind !== 'none') {
    add(city.kind === 'current' ? 'city-path' : 'unconfirmed',
      casedLine(city.route.geometry, city.kind === 'current' ? styles.current : styles.unconfirmed, PANES.routes));
    if (city.kind === 'unconfirmed') add('unconfirmed', badgeMarker(latLng(middle(city.route.geometry)),
      'Oczekuje na weryfikację', 'badge-stale'));
  }
  for (const point of state.cityPoints) {
    const none = city?.kind === 'none';
    add(none ? 'no-route' : 'city-path', L.circleMarker(latLng(point), { pane: PANES.markers, interactive: false,
      className: none ? 'layer-no-route' : 'layer-city-point', radius: 5, fill: true, fillOpacity: 1,
      fillColor: none ? '#ff5a4e' : '#fbd47e', color: '#12232a', weight: 2 }));
  }
  if (city?.kind === 'none' && state.cityPoints.length === 2)
    add('no-route', badgeMarker(latLng(state.cityPoints[1]), 'Brak trasy', 'badge-no-route'));
}

function legend(vm) {
  const valencia = vm.scenario === 'valencia';
  const drawn = drawState(vm);
  const city = cityState(vm);
  const graph = vm.network.city ? 'Routing · graf OSM' : 'Routing · graf ćwiczeniowy';
  const graphMode = vm.network.city ? null : 'synthetic';
  const items = [];
  if (valencia) {
    items.push({ id: 'city-path', label: 'Trasa testowa OSM', swatch: 'legend-path',
      provenance: city?.kind === 'current'
        ? 'Routing · graf OSM · rewizja ' + city.revision + ' · ' + minutes(city.route.seconds)
        : 'Wskaż dwa punkty na mapie, aby sprawdzić przejście w bieżącej rewizji' });
    if (drawn?.kind === 'current') items.push({ id: 'path', label: 'Wylosowana trasa ewakuacji', swatch: 'legend-path',
      provenance: 'Routing · logit-sue (task 03) · rewizja ' + drawn.revision + ' · ' + label(drawn.route.exit) +
        ' · schron syntetyczny' });
  } else {
    const route = drawn?.kind === 'current' ? drawn.route : null;
    items.push({ id: 'path', label: 'Wylosowana trasa', swatch: 'legend-path', mode: graphMode,
      provenance: route ? graph + ' · rewizja ' + drawn.revision + ' · ' + label(route.exit) + ' · przybycie ' +
        time(route.arrivesAt) + ' UTC' : graph + ' · brak aktywnej trasy' });
  }
  if (drawn?.kind === 'withdrawn') {
    items.push({ id: 'withdrawn', label: 'Trasa wycofana', swatch: 'legend-withdrawn', mode: graphMode,
      provenance: drawn.route ? graph + ' · ostatnio aktywna w rewizji ' + drawn.revision + ' · wycofana w rewizji ' + drawn.withdrawnAt
        : 'Wycofana · stan w rewizji ' + drawn.withdrawnAt + ' · przebieg spoza tej sesji' });
  }
  if (drawn?.kind === 'unconfirmed' || city?.kind === 'unconfirmed') {
    items.push({ id: 'unconfirmed', label: 'Trasa niepotwierdzona', swatch: 'legend-unconfirmed', stale: true,
      provenance: 'Ostatni znany przebieg · oczekuje na ponowną weryfikację po odczycie' });
  }
  const noRoute = (vm.distribution && !vm.distribution.options.length) || city?.kind === 'none';
  if (noRoute) {
    items.push({ id: 'no-route', label: 'Brak trasy', swatch: 'legend-no-route', mode: graphMode,
      provenance: (city?.kind === 'none' ? 'Routing · graf OSM · rewizja ' + city.revision
        : graph + ' · węzeł ' + label(vm.distribution.node) + ' · rewizja ' + vm.distribution.revision) +
        ' · brak dopuszczalnego wyjścia' });
  }
  return items;
}

defineLayer('routes', { title: 'Trasy ewakuacji', group: 'Sieć i trasy', order: 60, services: SERVICES, update, legend });
