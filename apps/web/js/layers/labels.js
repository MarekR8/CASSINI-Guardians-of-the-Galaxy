// Place and node labels.
import { $ } from '../dom.js';
import { label } from '../format.js';
import { caption, defineLayer, latLng, nodePositions, sink, unchangedValencia } from './common.js';

const valenciaPlaces = [
  ['València', -0.374, 39.48], ['Torrent', -0.466, 39.452], ['Picanya', -0.443, 39.437],
  ['Paiporta', -0.414, 39.422], ['Catarroja', -0.399, 39.398], ['Albufera', -0.353, 39.322],
];

function update(vm, { layer, info }) {
  if (unchangedValencia(vm, info)) return;
  layer.clearLayers();
  const add = sink(layer);
  const network = vm.network;
  if (vm.scenario === 'valencia' && !$('valencia-image').value) {
    for (const [name, lon, lat] of valenciaPlaces) add('labels', caption([lat, lon], name, 0, 0, 'map-place-caption'));
  }
  const startNode = $('node').value;
  for (const [id, point] of nodePositions(network)) {
    const important = network.exits.includes(id) || id === startNode;
    const name = [label(id), id === 'pickup' ? -10 : 10, id === 'pickup' || id === 'east-junction' ? 26 : -14];
    add('labels', caption(latLng(point), name[0], name[1], name[2], 'map-node' + (important ? '' : ' minor-label')));
  }
}

const legend = vm => (vm.network.nodes.length || vm.scenario === 'valencia') &&
  { id: 'labels', label: 'Etykiety', swatch: 'legend-labels', origin: 'file',
    provenance: vm.scenario === 'valencia' ? 'Nazwy miejscowości (widok OSM bez radaru)' : 'Nazwy węzłów ćwiczenia' };

defineLayer('labels', { title: 'Etykiety', group: 'Tło mapy', order: 100, update, legend });
