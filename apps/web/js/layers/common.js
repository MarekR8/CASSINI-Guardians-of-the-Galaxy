// Shared helpers for the map layers: geometry, styles, per-entry visibility, staleness and provenance text.
import { element } from '../dom.js';
import { formatObservation } from '../format.js';
import { getMap, PANES as CORE_PANES, registerLayer } from '../map.js';
import { state } from '../state.js';

export const L = window.L;

// Extra panes between the network segments (260) and the current routes (270) of map.js:
// a withdrawn route stays visible under the closure that caused it, and both stay under the current route.
export const PANES = { ...CORE_PANES, withdrawn: 'cassini-withdrawn', closures: 'cassini-closures' };
const map = getMap();
if (map) {
  map.createPane(PANES.withdrawn).style.zIndex = '263';
  map.createPane(PANES.closures).style.zIndex = '266';
}

export const latLng = ([lon, lat]) => [lat, lon];
export const line = points => points.map(latLng);

export function polygons(geometry) {
  if (!geometry) return [];
  if (geometry.type === 'Polygon') return [geometry.coordinates];
  if (geometry.type === 'MultiPolygon') return geometry.coordinates;
  if (geometry.type === 'GeometryCollection') return geometry.geometries.flatMap(polygons);
  return [];
}

const vector = { interactive: false, fill: false, lineCap: 'butt', lineJoin: 'miter' };
const withClass = (style, extra) => ({ ...style, className: [style.className, extra].filter(Boolean).join(' ') });

// Stale data keeps its geometry but loses its colour: CSS class layer-stale greys and hatches it.
export function polygon(rings, style, pane, stale = false) {
  return L.polygon(rings.map(line), withClass({ ...vector, fill: true, fillRule: 'evenodd', pane, ...style },
    stale && 'layer-stale'));
}

export function polyline(points, style, pane, stale = false) {
  return L.polyline(line(points), withClass({ ...vector, pane, ...style }, stale && 'layer-stale'));
}

// A road or route drawn on a dark casing so it stays readable over imagery.
export function casedLine(points, style, pane, stale = false) {
  return [
    polyline(points, { ...style, className: 'map-casing', color: '#102026', weight: (style.weight ?? 4) + 3,
      opacity: .85 }, pane, stale),
    polyline(points, { ...style, className: ['map-route', style.className].filter(Boolean).join(' ') }, pane, stale),
  ];
}

export function caption(position, name, dx, dy, className) {
  const node = element('span', name, 'map-label' + (className ? ' ' + className : '') + (dx < 0 ? ' map-label-end' : ''));
  node.style.left = dx + 'px';
  node.style.top = dy + 'px';
  return L.marker(position, {
    pane: PANES.labels, interactive: false, keyboard: false,
    icon: L.divIcon({ html: node, className: 'map-label-anchor', iconSize: [0, 0] }),
  });
}

export function badgeMarker(position, name, className, pane = PANES.markers) {
  const node = element('span', name, 'map-badge ' + className);
  return L.marker(position, {
    pane, interactive: false, keyboard: false,
    icon: L.divIcon({ html: node, className: 'map-badge-anchor', iconSize: [0, 0] }),
  });
}

// Legend entries can be switched off. The choice is kept in memory for this page only.
const hidden = new Set();
export const visible = id => !hidden.has(id);
export function setVisible(id, on) {
  if (on) hidden.delete(id); else hidden.add(id);
}

// Add a Leaflet layer (or an array of them) to the layer group unless its legend entry is switched off.
export const sink = group => (entry, items) => {
  if (!visible(entry)) return;
  for (const item of [items].flat()) group.addLayer(item);
};

const serviceNames = { management: 'zgłoszenia', situation: 'sytuacja', routing: 'trasy', dispatch: 'dyspozytor' };
export const serviceName = service => serviceNames[service] ?? service;

// Services whose last read is missing or out of date. Without a consistent read every service counts as stale.
export function staleServices(vm, services) {
  return services.filter(service => !vm.readAvailable || vm.stale?.[service]);
}

// Valencia redraws its heavy vector layers only when something they show has changed.
export function unchangedValencia(vm, info) {
  return vm.scenario === 'valencia' && info.reason === 'refresh' &&
    !(info.revisionChanged || info.restored || info.cityPathUpdated || (state.cityPath && state.busy));
}

const modeNames = { mock: 'symulacja', synthetic: 'symulacja', recorded: 'zapis', live: 'na żywo', manual: 'ręcznie' };
export const modeName = mode => modeNames[mode] ?? mode;
export const simulated = mode => mode === 'mock' || mode === 'synthetic' || mode === 'simulated';

// Test or exercise layers are labelled apart from data imported by the operator (recorded, live or manual).
export function origin(mode) {
  if (simulated(mode)) return 'exercise';
  if (['recorded', 'live', 'manual'].includes(mode)) return 'imported';
  return null;
}

export const observed = value => value ? formatObservation.format(new Date(value)) + ' UTC' : null;

// "source · mode · observedAt · productId", skipping what the DTO does not provide.
export function provenance({ source, mode, observedAt, productId, extra } = {}) {
  return [source, mode && modeName(mode), observedAt && observed(observedAt), productId, extra]
    .filter(Boolean).join(' · ');
}

// registerLayer wrapper: every legend entry carries the layer's stale state and the services it depends on.
export function defineLayer(id, { services = [], legend, ...definition }) {
  return registerLayer(id, {
    ...definition,
    legend: vm => {
      const stale = staleServices(vm, services);
      return [legend?.(vm)].flat().filter(Boolean).map(item => ({
        stale: stale.length > 0, staleServices: stale, ...item,
      }));
    },
  });
}

// Coordinates of a network segment: its own geometry or the straight line between its nodes.
const nodeCache = new WeakMap();
export function nodePositions(network) {
  if (!nodeCache.has(network)) nodeCache.set(network, new Map(network.nodes.map(node => [node.id, [node.longitude, node.latitude]])));
  return nodeCache.get(network);
}

export function segmentPoints(network, segmentOrId) {
  const segment = typeof segmentOrId === 'string' ? network.segments.find(item => item.id === segmentOrId) : segmentOrId;
  if (!segment) return null;
  if (segment.geometry?.length) return segment.geometry;
  const nodes = nodePositions(network);
  const from = nodes.get(segment.from);
  const to = nodes.get(segment.to);
  return from && to ? [from, to] : null;
}

// Hatch pattern for detections awaiting review (css/layers.css: fill: url(#layer-hatch-review)).
// Built with DOM calls instead of markup because the CSP forbids inline style attributes.
(function hatchPatterns() {
  const ns = 'http://www.w3.org/2000/svg';
  const create = (tag, attributes) => {
    const node = document.createElementNS(ns, tag);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    return node;
  };
  const svg = create('svg', { width: '0', height: '0', 'aria-hidden': 'true', focusable: 'false' });
  svg.style.position = 'absolute';
  const pattern = create('pattern', { id: 'layer-hatch-review', width: '8', height: '8',
    patternUnits: 'userSpaceOnUse', patternTransform: 'rotate(45)' });
  pattern.append(create('rect', { width: '8', height: '8', fill: '#9c67c2', 'fill-opacity': '.32' }),
    create('rect', { width: '2.5', height: '8', fill: '#e0abff', 'fill-opacity': '.8' }));
  const defs = create('defs', {});
  defs.append(pattern);
  svg.append(defs);
  document.body.append(svg);
})();
