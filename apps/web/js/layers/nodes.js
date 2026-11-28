// Network nodes and exits, plus residents without a confirmed exit aggregated per node.
// INV-14: no individual resident position or name is drawn, only a count at the node.
import { $ } from '../dom.js';
import { confirmedOutside, label } from '../format.js';
import { badgeMarker, defineLayer, L, latLng, nodePositions, PANES, sink, staleServices } from './common.js';

const vector = { interactive: false, lineCap: 'butt', lineJoin: 'miter' };

const waiting = vm => {
  const counts = new Map();
  for (const item of vm.incident?.cases ?? []) {
    if (confirmedOutside(item)) continue;
    counts.set(item.node, (counts.get(item.node) ?? 0) + 1);
  }
  return counts;
};

function update(vm, { layer }) {
  layer.clearLayers();
  const add = sink(layer);
  const network = vm.network;
  const startNode = $('node').value;
  for (const [id, point] of nodePositions(network)) {
    const isExit = network.exits.includes(id);
    const isStart = id === startNode;
    add(isExit ? 'exit' : isStart ? 'start' : 'node', L.circleMarker(latLng(point), {
      ...vector, pane: PANES.markers, className: 'map-node' + (isExit ? ' layer-exit' : ''), radius: isStart ? 9 : 6,
      fill: true, fillOpacity: 1, fillColor: isExit ? '#fbd47e' : isStart ? '#8df0ce' : '#b7cbce',
      color: '#12232a', weight: 2 }));
  }
  const stale = staleServices(vm, ['management']).length > 0;
  for (const [id, count] of waiting(vm)) {
    const point = nodePositions(network).get(id);
    if (point) add('cases', badgeMarker(latLng(point), count + ' os.', 'badge-cases map-node' + (stale ? ' layer-stale' : '')));
  }
}

// Server counters (task 6): exits confirmed with a position fix; simulated fixes are counted apart.
function outsideLine(incident) {
  const counters = incident?.counters;
  if (!counters) return null;
  return 'Potwierdzone poza strefą: ' + counters.confirmedOutside +
    (counters.confirmedOutsideSimulated ? ' (pozycja SYMULACJA: ' + counters.confirmedOutsideSimulated + ')' : '');
}

function legend(vm) {
  const network = vm.network;
  if (!network.nodes.length) return null;
  const counts = waiting(vm);
  const total = [...counts.values()].reduce((sum, value) => sum + value, 0);
  const exercise = network.city ? null : 'synthetic';
  return [
    network.exits.length && { id: 'exit', label: 'Wyjście', swatch: 'legend-exit', className: 'exercise-legend',
      mode: exercise, provenance: network.exits.map(label).join(', ') },
    { id: 'start', label: 'Węzeł startowy', swatch: 'legend-start', className: 'exercise-legend', mode: exercise,
      provenance: label($('node').value) },
    { id: 'node', label: 'Węzeł sieci', swatch: 'legend-node', className: 'exercise-legend', mode: exercise,
      provenance: 'Graf ćwiczeniowy · Routing' },
    vm.incident && { id: 'cases', label: 'Osoby bez potwierdzonego wyjścia · ' + total, swatch: 'legend-cases',
      className: 'exercise-legend', mode: 'synthetic', stale: staleServices(vm, ['management']).length > 0,
      provenance: ['Management · liczba na węzeł, bez pozycji osób', outsideLine(vm.incident)] },
  ];
}

defineLayer('nodes', { title: 'Węzły i wyjścia', group: 'Węzły', order: 90, update, legend });
