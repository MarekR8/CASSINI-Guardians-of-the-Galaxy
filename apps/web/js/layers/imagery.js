// Background imagery: Sentinel-2 / orthophoto, Valencia Sentinel-1 radar, OSM roads (city.svg) and node zones (city-nodes.svg).
import { $, text } from '../dom.js';
import { getMap, zoomFactor } from '../map.js';
import { state } from '../state.js';
import { defineLayer, L, PANES, provenance, unchangedValencia, visible } from './common.js';

const CLOSE_VIEW = 5;
const images = new Map();
let detailListener = false;

const imageBounds = ([west, south, east, north]) => [[south, west], [north, east]];

// Keep image overlays between refreshes so large local images are not reloaded on every poll.
function syncImages(group, wanted) {
  const keys = new Set(wanted.map(image => image.key));
  for (const [key, overlay] of images) {
    if (!keys.has(key)) {
      group.removeLayer(overlay);
      images.delete(key);
    }
  }
  for (const image of wanted) {
    let overlay = images.get(image.key);
    if (!overlay) {
      overlay = L.imageOverlay(image.url, imageBounds(image.bounds), {
        pane: image.pane ?? PANES.imagery, opacity: image.opacity, zIndex: image.zIndex,
        className: image.className ?? '', interactive: false,
      });
      overlay.role = image.role;
      images.set(image.key, overlay);
      group.addLayer(overlay);
    } else overlay.setOpacity(image.opacity);
  }
}

const imageWithRole = role => [...images.values()].find(overlay => overlay.role === role);

function updateMapDetail() {
  const map = getMap();
  const container = map.getContainer();
  const city = container.classList.contains('map-city');
  const detailImage = imageWithRole('orthophoto');
  const closeView = zoomFactor() >= CLOSE_VIEW - 1e-6;
  let detailVisible = Boolean(detailImage && !state.satelliteDetail);
  if (city && detailVisible) detailVisible = closeView && map.getBounds().overlaps(detailImage.getBounds());
  detailImage?.getElement()?.classList.toggle('map-hidden', !detailVisible);
  container.classList.toggle('map-detailed', !city || closeView);
  $('ortho-credit').hidden = !detailVisible;
  $('satellite-credit').hidden = !state.satelliteScene || (detailVisible && !city);
  $('map-layer-tools').hidden = !detailImage || (city && !detailVisible && !state.satelliteDetail);
  text('map-layer', state.satelliteDetail ? 'Pokaż ortofotomapę' : 'Pokaż Sentinel-2');
}

function update(vm, { map, layer, info }) {
  if (!detailListener) {
    map.on('zoomend moveend', updateMapDetail);
    detailListener = true;
  }
  if (unchangedValencia(vm, info)) return;
  const network = vm.network;
  const valencia = vm.scenario === 'valencia';
  const scene = vm.satellite;
  const city = network.city;
  map.getContainer().classList.toggle('map-city', Boolean(city));

  const wanted = [];
  if (scene) {
    if (visible('satellite')) wanted.push({ key: 'satellite:' + scene.image, role: 'satellite',
      url: '/maps/' + scene.image, bounds: scene.bounds, opacity: .83, zIndex: 1 });
    if (scene.detail && visible('orthophoto')) wanted.push({ key: 'ortho:' + scene.detail.image, role: 'orthophoto',
      url: '/maps/' + scene.detail.image, bounds: scene.detail.bounds, opacity: .95, zIndex: 2 });
  }
  if (valencia && visible('radar')) {
    const selected = $('valencia-image').value;
    const radar = vm.cityImagery?.scenes[selected];
    if (radar) wanted.push({ key: 'radar:' + selected, role: 'radar', url: '/maps/sentinel-' + selected + '.jpg',
      bounds: radar.bounds, opacity: .82, zIndex: 3 });
  }
  if (city && visible('roads')) {
    wanted.push({ key: 'roads:' + city.osmTimestamp, role: 'roads',
      url: '/maps/city.svg?v=' + encodeURIComponent(city.osmTimestamp), bounds: city.bounds,
      opacity: scene ? .48 : 1, zIndex: 4, className: 'map-city-roads' });
  }
  if (valencia && $('valencia-nodes').checked)
    wanted.push({ key: 'zones', role: 'zones', url: '/maps/city-nodes.svg', bounds: city.bounds, opacity: .9,
      pane: PANES.zones });
  syncImages(layer, wanted);
  updateMapDetail();
}

const radarNames = { before: 'przed zalaniem', event: 'po zdarzeniu' };

function legend(vm) {
  const scene = vm.satellite;
  const city = vm.network?.city;
  const valencia = vm.scenario === 'valencia';
  const selected = valencia ? $('valencia-image').value : '';
  const radar = valencia ? vm.cityImagery?.scenes[selected] : null;
  const file = { origin: 'file', group: 'Tło mapy' };
  return [
    scene && { ...file, id: 'satellite', label: 'Sentinel-2 L2A', swatch: 'legend-imagery',
      provenance: provenance({ source: 'Copernicus Sentinel-2', observedAt: scene.observedAt,
        productId: scene.products?.join(', ') }) },
    scene?.detail && { ...file, id: 'orthophoto', label: 'Ortofotomapa (zbliżenie)', swatch: 'legend-ortho',
      provenance: 'Geoportal / GUGiK' },
    radar && { ...file, id: 'radar', label: 'Radar Sentinel-1 VV · ' + (radarNames[selected] ?? selected),
      swatch: 'legend-radar', provenance: provenance({ source: 'Copernicus Sentinel-1', observedAt: radar.date,
        productId: radar.file }) },
    city && { ...file, id: 'roads', label: 'Drogi OSM', swatch: 'legend-roads',
      provenance: provenance({ source: 'OpenStreetMap · ODbL', extra: city.osmTimestamp ? 'stan ' + city.osmTimestamp : null }) },
    // The zone checkbox above the map is the source of truth; the legend entry mirrors it.
    valencia && { ...file, id: 'zones', label: 'Strefy węzłów', swatch: 'legend-zones', control: 'valencia-nodes',
      checked: $('valencia-nodes').checked,
      provenance: 'Klasyfikacja węzłów OSM względem obserwacji zalania · ' + (city?.roadCount ?? 0).toLocaleString('pl-PL') + ' odcinków' },
  ];
}

defineLayer('imagery', { title: 'Zobrazowania', group: 'Tło mapy', order: 0, update, legend });
