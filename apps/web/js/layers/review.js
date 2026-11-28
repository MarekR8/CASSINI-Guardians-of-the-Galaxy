// FlyPix (or synthetic) detections waiting for a coordinator decision. Never drawn as a closure until approved.
import { defineLayer, PANES, polygon, polygons, provenance, sink, staleServices, unchangedValencia } from './common.js';

const SERVICES = ['situation'];
const sourceNames = { exercise: 'Detekcja ćwiczeniowa', flypix: 'FlyPix AI' };
const pending = vm => (vm.snapshot?.reviewFeatures || []).filter(feature => feature.status === 'Pending');

function update(vm, { layer, info }) {
  if (unchangedValencia(vm, info)) return;
  layer.clearLayers();
  const add = sink(layer);
  const stale = staleServices(vm, SERVICES).length > 0;
  for (const feature of pending(vm)) {
    for (const rings of polygons(feature.geometry)) {
      add('review', polygon(rings, { className: 'layer-review', fillColor: '#9c67c2', fillOpacity: .38,
        color: '#e0abff', weight: 2.4, dashArray: '5 4' }, PANES.overlays, stale));
    }
  }
}

function legend(vm) {
  const features = pending(vm);
  if (!features.length) return null;
  const lines = new Map();
  for (const feature of features) {
    const text = provenance({ source: sourceNames[feature.source] ?? feature.source, mode: feature.mode, observedAt: feature.observedAt,
      productId: feature.imageProductId, extra: feature.modelId });
    lines.set(text, (lines.get(text) ?? 0) + 1);
  }
  return { id: 'review', label: 'Detekcja do weryfikacji · ' + features.length, swatch: 'legend-review',
    modes: [...new Set(features.map(feature => feature.mode))],
    provenance: [...lines].map(([text, count]) => (count > 1 ? count + ' × ' : '') + text) };
}

defineLayer('review', { title: 'Detekcje FlyPix', group: 'Sytuacja', order: 30, services: SERVICES, update, legend });
