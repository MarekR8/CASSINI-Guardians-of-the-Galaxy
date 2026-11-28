// Hazard layers from the situation snapshot: observed products (EMS / GFM / EMSR773, with productId) vs exercise layers.
import { defineLayer, PANES, polygon, polygons, provenance, simulated, sink, staleServices, unchangedValencia } from './common.js';

const SERVICES = ['situation'];
const hazardMode = hazard => hazard.provenance?.mode ?? (hazard.productId ? 'recorded' : 'mock');
const isObserved = hazard => !simulated(hazardMode(hazard));
// Valencia shows only the Copernicus EMS archive observation (EMSR773), as in the task 01/02 viewer.
const shown = vm => {
  const hazards = vm.snapshot?.hazards || [];
  return vm.scenario === 'valencia' ? hazards.filter(hazard => hazard.source === 'copernicus-ems-archive') : hazards;
};

const styles = {
  observed: { className: 'layer-observed', fillColor: '#2f7fc4', fillOpacity: .42, color: '#8fd0ff', weight: 1.8 },
  exercise: { className: 'layer-exercise', fillColor: '#285b6d', fillOpacity: .6, color: '#5c9fb1', weight: 1.8,
    dashArray: '5 4' },
};

function update(vm, { layer, info }) {
  if (unchangedValencia(vm, info)) return;
  layer.clearLayers();
  const add = sink(layer);
  const stale = staleServices(vm, SERVICES).length > 0;
  for (const hazard of shown(vm)) {
    const kind = isObserved(hazard) ? 'observed' : 'exercise';
    for (const rings of polygons(hazard.geometry))
      add(kind === 'observed' ? 'observed' : 'exercise-hazard', polygon(rings, styles[kind], PANES.hazards, stale));
  }
}

const sourceNames = { synthetic: 'Scenariusz ćwiczeniowy', 'copernicus-ems-archive': 'Copernicus EMS (archiwum)' };
const describe = hazard => provenance({
  source: sourceNames[hazard.provenance?.source ?? hazard.source] ?? hazard.provenance?.source ?? hazard.source, mode: hazardMode(hazard),
  observedAt: hazard.provenance?.observedAt ?? hazard.observedAt, productId: hazard.provenance?.productId ?? hazard.productId,
});

function legend(vm) {
  const hazards = shown(vm);
  const observed = hazards.filter(isObserved);
  const exercise = hazards.filter(hazard => !isObserved(hazard));
  const modes = list => [...new Set(list.map(hazardMode))];
  return [
    observed.length && { id: 'observed', label: 'Zalanie z obserwacji', swatch: 'legend-observed',
      provenance: observed.map(describe), modes: modes(observed) },
    exercise.length && { id: 'exercise-hazard', label: 'Zalanie ćwiczeniowe', swatch: 'legend-hazard',
      className: 'exercise-legend', provenance: exercise.map(describe), modes: modes(exercise) },
  ];
}

defineLayer('hazards', { title: 'Zagrożenia', group: 'Sytuacja', order: 20, services: SERVICES, update, legend });
