// Leaflet map, layer registry and legend hook. Leaflet 1.9.4 is vendored and loaded as a classic script (global L).
import { $, element } from './dom.js';
import { state, subscribe, viewModel } from './state.js';

const L = window.L;
const FALLBACK_BOUNDS = [16.646, 50.431, 16.660, 50.442];
const EPSILON = 1e-6;
const FRAME_MARGIN = .03;

// Panes below Leaflet's default overlayPane (400). Layers pass { pane: PANES.x } to keep the drawing order.
export const PANES = {
  imagery: 'cassini-imagery', context: 'cassini-context', hazards: 'cassini-hazards',
  zones: 'cassini-zones', overlays: 'cassini-overlays', segments: 'cassini-segments',
  routes: 'cassini-routes', markers: 'cassini-markers', labels: 'cassini-labels',
};

const layers = new Map();
const clickListeners = [];
let map = null;
let frame = null;
let baseZoom = 0;
let zoomFactorLimit = 5;
let lastViewModel = null;
let lastInfo = { reason: 'redraw' };
let legendRenderer = renderLegendList;

// Equirectangular projection scaled by cos(latitude), the same geometry as the former SVG map
// and the local JPG/SVG overlays, so overlays and vectors stay aligned at every zoom.
function equirectangular(latitude) {
  const ratio = Math.cos(latitude * Math.PI / 180);
  return L.extend({}, L.CRS.Earth, {
    code: 'CASSINI:EQC',
    projection: L.Projection.LonLat,
    transformation: new L.Transformation(ratio / 360, .5, -1 / 360, .5),
  });
}

export const toBounds = bounds => bounds instanceof L.LatLngBounds ? bounds
  : L.latLngBounds([bounds[1], bounds[0]], [bounds[3], bounds[2]]);

export function geometryBounds(geometry) {
  const box = [Infinity, Infinity, -Infinity, -Infinity];
  const visit = value => {
    if (typeof value[0] === 'number') {
      box[0] = Math.min(box[0], value[0]); box[1] = Math.min(box[1], value[1]);
      box[2] = Math.max(box[2], value[0]); box[3] = Math.max(box[3], value[1]);
    } else value.forEach(visit);
  };
  if (geometry?.type === 'GeometryCollection') geometry.geometries.forEach(item => visit(item.coordinates));
  else if (geometry?.coordinates) visit(geometry.coordinates);
  return Number.isFinite(box[0]) ? box : null;
}

export function getMap() {
  return map;
}

// Base zoom is the zoom at which the whole scenario frame fits; zoomFactor() is 1 there.
export const zoomFactor = () => map ? 2 ** (map.getZoom() - baseZoom) : 1;

export function registerLayer(id, definition) {
  if (layers.has(id)) throw new Error('Warstwa ' + id + ' jest już zarejestrowana.');
  const layer = { id, title: id, group: 'Inne', order: 100, ...definition, group_: L.layerGroup() };
  layers.set(id, layer);
  if (map) {
    layer.group_.addTo(map);
    if (lastViewModel?.network) updateLayer(layer, lastViewModel, lastInfo);
  }
  return layer.group_;
}

export function setLegendRenderer(fn) {
  legendRenderer = fn;
  renderLegend();
}

export function onMapClick(fn) {
  clickListeners.push(fn);
}

// Leaflet ignores a new view while a zoom animation runs, so apply it when that animation ends.
function afterZoomAnimation(action) {
  if (map._animatingZoom) map.once('zoomend', action);
  else action();
}

export function fitTo(bounds) {
  if (!map || !bounds) return;
  const target = toBounds(bounds).pad(.125);
  afterZoomAnimation(() => map.fitBounds(target));
}

export function resetView() {
  if (map && frame) afterZoomAnimation(() => map.fitBounds(frame));
}

// Re-run every layer with the last view model, e.g. after a local UI change.
export function redraw(details = {}) {
  if (lastViewModel) render(viewModel(), { reason: 'redraw', ...details });
}

// Valencia is limited to the shared Sentinel-1 scene (the task 03 window); the sample to the Sentinel-2 view.
function frameBounds(vm) {
  if (vm.scenario === 'valencia')
    return vm.cityImagery?.scenes?.event?.bounds ?? vm.network?.city?.bounds ?? FALLBACK_BOUNDS;
  return vm.satellite?.viewBounds ?? vm.network?.city?.bounds ?? FALLBACK_BOUNDS;
}

function updateLimits() {
  if (!frame) return;
  baseZoom = map.getBoundsZoom(frame);
  map.setMinZoom(baseZoom);
  map.setMaxZoom(baseZoom + Math.log2(zoomFactorLimit));
  const size = map.getSize();
  const center = map.project(frame.getCenter(), baseZoom);
  const half = size.divideBy(2);
  // A small margin keeps features on the frame edge (e.g. a route endpoint) reachable when zoomed in.
  map.setMaxBounds(L.latLngBounds(map.unproject(center.subtract(half), baseZoom),
    map.unproject(center.add(half), baseZoom)).extend(frame.pad(FRAME_MARGIN)));
  syncZoomButtons();
}

function syncZoomButtons() {
  const zoom = map.getZoom();
  $('map-zoom-in').disabled = zoom >= map.getMaxZoom() - EPSILON;
  $('map-zoom-out').disabled = $('map-zoom-reset').disabled = zoom <= map.getMinZoom() + EPSILON;
}

function initView(vm) {
  const [west, south, east, north] = frameBounds(vm);
  const key = [west, south, east, north].join(',');
  if (frame && frame.key === key) return;
  map.options.crs = equirectangular((south + north) / 2);
  zoomFactorLimit = vm.network.city ? 32 : 5;
  map.invalidateSize(false);
  frame = toBounds([west, south, east, north]);
  frame.key = key;
  map.setMaxBounds(null);
  map.setMinZoom(0);
  map.setMaxZoom(40);
  map.fitBounds(frame, { animate: false });
  updateLimits();
  map.fitBounds(frame, { animate: false });
}

function updateLayer(layer, vm, info) {
  try {
    layer.update?.(vm, { map, L, layer: layer.group_, panes: PANES, info, state, zoomFactor });
  } catch (error) {
    console.error(error);
  }
}

function render(vm, info) {
  lastViewModel = vm;
  lastInfo = info;
  if (!vm.network) return;
  initView(vm);
  for (const layer of sortedLayers()) updateLayer(layer, vm, info);
  renderLegend();
}

const sortedLayers = () => [...layers.values()].sort((a, b) => a.order - b.order);

// Legend items: { id, label, swatch (CSS class), detail? } or an array of them, or null.
function renderLegend() {
  const container = $('map-legend');
  if (!container || !lastViewModel?.network) return;
  const items = [];
  for (const layer of sortedLayers()) {
    let entries;
    try {
      entries = layer.legend?.(lastViewModel);
    } catch (error) {
      console.error(error);
    }
    for (const item of [entries].flat().filter(Boolean)) items.push({ layer: layer.id, group: layer.group, ...item });
  }
  legendRenderer(container, items, lastViewModel);
}

function renderLegendList(container, items) {
  container.replaceChildren(...items.map(item => {
    const entry = element('span', null, item.className);
    if (item.id) entry.dataset.legend = item.id;
    entry.append(element('i', null, item.swatch), document.createTextNode(' ' + item.label));
    if (item.detail) entry.append(element('small', item.detail));
    return entry;
  }));
}

function northArrow() {
  const control = L.control({ position: 'topright' });
  control.onAdd = () => {
    const node = L.DomUtil.create('div', 'map-north');
    node.setAttribute('aria-hidden', 'true');
    node.append(element('span', '↑'), element('span', 'N'));
    return node;
  };
  return control;
}

export function initMap() {
  if (!L) throw new Error('Brak lokalnej biblioteki Leaflet.');
  const container = $('map');
  map = L.map(container, {
    crs: equirectangular(50), zoomControl: false, zoomSnap: .25, zoomDelta: .5, wheelPxPerZoomLevel: 90,
    maxBoundsViscosity: 1, attributionControl: true, center: [50.4365, 16.653], zoom: 15,
  });
  map.attributionControl.setPrefix('<a href="https://leafletjs.com" target="_blank" rel="noopener noreferrer">Leaflet</a>');
  Object.values(PANES).forEach((name, index) => {
    map.createPane(name).style.zIndex = String(210 + index * 10);
  });
  northArrow().addTo(map);
  for (const layer of layers.values()) layer.group_.addTo(map);
  $('map-zoom-in').addEventListener('click', () => map.zoomIn());
  $('map-zoom-out').addEventListener('click', () => map.zoomOut());
  $('map-zoom-reset').addEventListener('click', resetView);
  container.addEventListener('keydown', event => {
    if (event.key !== 'Home') return;
    resetView();
    event.preventDefault();
  });
  map.on('zoomend', syncZoomButtons);
  map.on('click', event => {
    for (const listener of clickListeners) listener({ lon: event.latlng.lng, lat: event.latlng.lat, event });
  });
  new ResizeObserver(() => {
    if (!frame) return;
    map.invalidateSize(false);
    updateLimits();
  }).observe(container);
  subscribe(render);
  return map;
}
