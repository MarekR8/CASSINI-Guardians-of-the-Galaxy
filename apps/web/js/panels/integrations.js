// Integration preview (#integration-preview): Situation's mock Copernicus EMS, FlyPix AI and city data sources.
// The answers are synthetic; the panel only reads them and never changes the situation.
import { api } from '../api.js';
import { $, text } from '../dom.js';
import { formatObservation } from '../format.js';

const SVG = 'http://www.w3.org/2000/svg';

const summaries = {
  'integration-copernicus': result => `${result.productId} · ${formatObservation.format(new Date(result.observedAt))} UTC`,
  'integration-flypix': result => `${result.geoJson.features.length} detekcji do weryfikacji · raster ${result.imageProductId}`,
  'city-data': result => result.zones.map(zone => `${zone.zoneId} · liczba osób: ${zone.people}`).join(', '),
  'city-map': result => `${result.datasetId} · ${result.geoJson.features.length} obszarów w GeoJSON`,
};

// Local copy: importing layers/common.js here would run its pane set-up before the map exists.
function polygons(geometry) {
  if (geometry?.type === 'Polygon') return [geometry.coordinates];
  if (geometry?.type === 'MultiPolygon') return geometry.coordinates;
  return [];
}

function svg(tag, attributes) {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
  return node;
}

// Fits [west, south, east, north] into the 760 × 420 preview with the cos(latitude) aspect of the map.
function projection([west, south, east, north]) {
  const ratio = Math.cos((south + north) * Math.PI / 360);
  const scale = Math.min(680 / ((east - west) * ratio), 360 / (north - south));
  return (lon, lat) => [380 + (lon - (west + east) / 2) * ratio * scale, 210 + ((south + north) / 2 - lat) * scale];
}

const polygonPath = (rings, project) =>
  rings.map(ring => 'M ' + ring.map(([lon, lat]) => project(lon, lat).join(',')).join(' L ') + ' Z').join(' ');

async function preview(control, buttons) {
  for (const button of buttons) button.disabled = true;
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
      const project = projection(result.bounds);
      for (const feature of result.geoJson.features) {
        for (const rings of polygons(feature.geometry)) {
          map.append(svg('path', { d: polygonPath(rings, project), fill: '#285b6d',
            stroke: '#8df0ce', 'stroke-width': 3, 'fill-rule': 'evenodd' }));
        }
      }
      map.removeAttribute('hidden');
    }
    text('integration-json', JSON.stringify(result, null, 2));
    response.hidden = false;
    text('integration-status', 'Dane syntetyczne · ' + summaries[operation](result));
  } catch {
    text('integration-status', 'Nie udało się pobrać podglądu integracji.');
  } finally {
    for (const button of buttons) button.disabled = false;
  }
}

export function initIntegrations() {
  const buttons = [...$('integration-preview').querySelectorAll('[data-integration]')];
  for (const control of buttons) control.addEventListener('click', () => preview(control, buttons));
}
