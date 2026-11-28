// Archive flood extent shipped with the local scene (Copernicus EMS or GFM). Static file, not live situation data.
import { defineLayer, PANES, polygon, polygons, provenance, sink } from './common.js';

function update(vm, { layer }) {
  layer.clearLayers();
  const add = sink(layer);
  const archive = vm.satellite?.floodArchive;
  for (const rings of polygons(archive?.geometry)) {
    add('archive', polygon(rings, { className: 'archive-flood layer-archive', fillColor: '#eead83', fillOpacity: .5,
      color: '#ffe0cb', weight: 1.5 }, PANES.overlays));
  }
}

function legend(vm) {
  const archive = vm.satellite?.floodArchive;
  if (!archive) return null;
  return { id: 'archive', label: 'Zalanie archiwalne · ' + (archive.label ?? 'Copernicus EMS'), swatch: 'legend-archive',
    origin: 'file', mode: 'recorded',
    provenance: provenance({ source: archive.label ?? 'Copernicus EMS', observedAt: archive.observedAt,
      productId: archive.productId, extra: archive.resolutionMeters ? archive.resolutionMeters + ' m' : null }) };
}

defineLayer('archive', { title: 'Zalanie archiwalne', group: 'Sytuacja', order: 15, update, legend });
