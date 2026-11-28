// Confirmed closures: closed exercise segments, approved detection areas and Valencia closed OSM roads.
import { segmentLabel } from '../format.js';
import {
  casedLine, defineLayer, observed, PANES, polygon, polygons, provenance, segmentPoints, sink,
  unchangedValencia,
} from './common.js';

const SERVICES = ['situation'];
const style = { className: 'layer-closed', color: '#ff5a4e', weight: 4, dashArray: '10 7', lineCap: 'round',
  lineJoin: 'round' };

function update(vm, { layer, info }) {
  if (unchangedValencia(vm, info)) return;
  layer.clearLayers();
  const add = sink(layer);
  // Closures stay active until an authorised reopening (INV-10), so the last known closure keeps its red style
  // while data is stale; the stale state is shown by the status chip and the legend badge instead.
  const stale = false;
  const situation = vm.snapshot;
  const network = vm.network;
  const closed = new Set(situation?.closedSegments || []);
  if (vm.scenario === 'valencia') {
    for (const road of vm.closedRoads || []) add('closed', casedLine(road.geometry, style, PANES.closures, stale));
    return;
  }
  if (network.city) {
    for (const feature of (situation?.reviewFeatures || []).filter(item =>
      item.status === 'Approved' && item.segmentIds.some(id => id.startsWith('osm-') && closed.has(id)))) {
      for (const rings of polygons(feature.geometry)) {
        add('closed', polygon(rings, { className: 'layer-closed-area', fillColor: '#e27169', fillOpacity: .35,
          color: '#ff9e8f', weight: 2, dashArray: '4 3' }, PANES.overlays, stale));
      }
    }
  }
  for (const segment of network.segments) {
    if (!(segment.walkingSeconds > 0) || !closed.has(segment.id)) continue;
    const points = segmentPoints(network, segment);
    if (points) add('closed', casedLine(points, style, PANES.closures, stale));
  }
}

const sourceNames = {
  operator: 'Decyzja operatora', 'exercise-verified': 'Detekcja ćwiczeniowa zatwierdzona',
  'flypix-verified': 'FlyPix zatwierdzony przez koordynatora',
};

function legend(vm) {
  const situation = vm.snapshot;
  const closed = situation?.closedSegments || [];
  const valencia = vm.scenario === 'valencia';
  const count = valencia ? (vm.closedRoads || []).length : closed.length;
  const reviews = new Map((situation?.reviewFeatures || []).map(feature => [feature.id, feature]));
  const groups = new Map();
  for (const id of closed) {
    const evidence = situation.closureEvidence?.[id];
    const feature = evidence?.reviewFeatureId ? reviews.get(evidence.reviewFeatureId) : null;
    const mode = evidence?.source === 'operator' ? 'manual' : feature?.mode;
    const text = evidence ? provenance({
      source: sourceNames[evidence.source] ?? evidence.source, mode, observedAt: evidence.observedAt,
      productId: feature?.imageProductId, extra: 'decyzja ' + observed(evidence.decidedAt),
    }) : 'Brak zapisanego dowodu zamknięcia';
    const group = groups.get(text) ?? { mode, ids: [] };
    group.ids.push(id);
    groups.set(text, group);
  }
  const lines = [...groups].map(([text, group]) =>
    (valencia || group.ids.length > 3 ? group.ids.length + ' odc.' : group.ids.map(segmentLabel).join(', ')) + ' · ' + text);
  return { id: 'closed', label: 'Zamknięty odcinek' + (count ? ' · ' + count : ''), swatch: 'legend-closed',
    modes: [...new Set([...groups.values()].map(group => group.mode).filter(Boolean))],
    provenance: lines.length ? lines : 'Brak potwierdzonych zamknięć w bieżącej rewizji' };
}

defineLayer('closures', { title: 'Zamknięcia', group: 'Sytuacja', order: 40, services: SERVICES, update, legend });
