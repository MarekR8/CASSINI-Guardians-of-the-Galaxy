// Field units at their reported node with the position evidence from Dispatch (EGNOS / HPL / manual).
// Only drawn where the scenario has dispatch data (not in Valencia).
import { element } from '../dom.js';
import { label } from '../format.js';
import { defineLayer, L, latLng, nodePositions, PANES, provenance, sink, staleServices } from './common.js';

const SERVICES = ['dispatch'];

// EGNOS is shown only when the evidence confirms the receiver applied it.
function evidenceKind(unit) {
  const evidence = unit.positionEvidence;
  if (evidence?.mode === 'manual') return 'manual';
  if (evidence?.egnosApplied === true && unit.positionVerified) return 'egnos';
  return 'unverified';
}

const entries = { egnos: 'units-egnos', manual: 'units-manual', unverified: 'units-unverified' };
const labels = {
  egnos: 'Jednostka · pozycja EGNOS', manual: 'Jednostka · pozycja potwierdzona ręcznie',
  unverified: 'Jednostka · pozycja do potwierdzenia',
};

function shortName(unit) {
  const number = unit.name?.match(/\d+/)?.[0] ?? unit.unitId.match(/\d+/)?.[0];
  return number ? 'J' + number : unit.unitId;
}

function update(vm, { layer }) {
  layer.clearLayers();
  const add = sink(layer);
  const stale = staleServices(vm, SERVICES).length > 0;
  const nodes = nodePositions(vm.network);
  const stacked = new Map();
  for (const unit of vm.dispatch?.units ?? []) {
    const point = nodes.get(unit.node);
    if (!point) continue;
    const index = stacked.get(unit.node) ?? 0;
    stacked.set(unit.node, index + 1);
    const kind = evidenceKind(unit);
    const node = element('span', shortName(unit), 'map-unit unit-' + kind + (unit.state === 'Available' ? '' : ' unit-busy') +
      (stale ? ' layer-stale' : ''));
    node.title = unit.name + ' · ' + label(unit.node);
    add(entries[kind], L.marker(latLng(point), {
      pane: PANES.markers, interactive: false, keyboard: false,
      icon: L.divIcon({ html: node, className: 'map-unit-anchor', iconSize: [26, 20], iconAnchor: [-10 - index * 30, 30] }),
    }));
  }
}

function describe(unit) {
  const evidence = unit.positionEvidence;
  const details = [unit.name + ' · ' + label(unit.node)];
  if (evidence) {
    details.push(provenance({ source: evidence.source, mode: evidence.mode,
      extra: [evidence.egnosApplied === true ? 'EGNOS zastosowany' : evidence.egnosApplied === false ? 'bez EGNOS' : null,
        evidence.hpl != null ? 'HPL ' + evidence.hpl + ' m' : null].filter(Boolean).join(' · ') || null }));
  } else details.push('brak dowodu pozycji');
  return details.join(' · ');
}

function legend(vm) {
  const groups = new Map();
  for (const unit of vm.dispatch?.units ?? []) {
    const kind = evidenceKind(unit);
    const group = groups.get(kind) ?? { lines: [], modes: new Set() };
    group.lines.push(describe(unit));
    if (unit.positionEvidence?.mode) group.modes.add(unit.positionEvidence.mode);
    groups.set(kind, group);
  }
  return [...groups].map(([kind, group]) => ({
    id: entries[kind], label: labels[kind], swatch: 'legend-unit-' + kind, provenance: group.lines, modes: [...group.modes],
  }));
}

defineLayer('units', { title: 'Jednostki', group: 'Jednostki', order: 80, services: SERVICES, update, legend });
