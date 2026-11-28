// Walking network segments of the exercise graph and the context roads / waterways drawn under them.
import { defineLayer, PANES, polyline, segmentPoints, sink, unchangedValencia } from './common.js';

function update(vm, { layer, info }) {
  if (unchangedValencia(vm, info)) return;
  layer.clearLayers();
  const add = sink(layer);
  const network = vm.network;
  const scene = vm.satellite;
  if (!network.city) {
    for (const points of (scene ? [] : network.waterways || []))
      add('context', polyline(points, { className: 'layer-water', color: '#285469', weight: 18 }, PANES.context));
    for (const points of network.contextRoads || []) {
      add('context', polyline(points, { className: 'map-context', color: scene ? '#9aacac' : '#38525a',
        opacity: scene ? .55 : 1, weight: 1.15 }, PANES.context));
    }
  }
  for (const segment of network.segments) {
    if (!(segment.walkingSeconds > 0)) continue;
    const points = segmentPoints(network, segment);
    if (points) add('segments', polyline(points, { className: 'layer-segment', color: '#89a4a9', weight: 1.5,
      lineCap: 'round', lineJoin: 'round' }, PANES.segments));
  }
}

function legend(vm) {
  const network = vm.network;
  const walkable = network.segments.filter(segment => segment.walkingSeconds > 0).length;
  const context = (network.contextRoads?.length ?? 0) > 0 && !network.city;
  return [
    walkable && { id: 'segments', label: 'Odcinek sieci przejść · ' + walkable, swatch: 'legend-segment',
      className: 'exercise-legend', mode: network.city ? null : 'synthetic',
      provenance: network.city ? 'Graf OpenStreetMap · Routing' : 'Graf ćwiczeniowy · Routing' },
    context && { id: 'context', label: 'Drogi w tle', swatch: 'legend-context', origin: 'file',
      provenance: 'Lokalny podkład wektorowy' },
  ];
}

defineLayer('network', { title: 'Sieć dróg', group: 'Sieć i trasy', order: 10, update, legend });
