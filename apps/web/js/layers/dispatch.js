// Field unit legs: approach to the pickup and outbound to the exit, styled by the dispatch route status.
import { label, segmentLabel } from '../format.js';
import {
  badgeMarker, casedLine, defineLayer, latLng, PANES, provenance, segmentPoints, sink, staleServices,
} from './common.js';

const SERVICES = ['dispatch', 'routing', 'situation'];
const base = { lineCap: 'round', lineJoin: 'round', weight: 4 };
const styles = {
  current: { ...base, className: 'layer-leg-current', color: '#fbd47e' },
  withdrawn: { ...base, className: 'layer-route-withdrawn', color: '#ff9f43', weight: 5, dashArray: '1 9' },
  review: { ...base, className: 'layer-leg-review', color: '#f2c14e', dashArray: '12 4 2 4' },
  unconfirmed: { ...base, className: 'layer-route-unconfirmed', color: '#9aa5a8', dashArray: '6 6' },
};
const entries = { current: 'legs', withdrawn: 'legs-withdrawn', review: 'legs-review', unconfirmed: 'legs-unconfirmed' };

function legStatus(vm, assignment, stale) {
  const verifiedRevision = assignment.routeStatus?.revision ?? assignment.route.revision;
  if (assignment.routeStatus?.state === 'Blocked') return 'withdrawn';
  if (stale) return 'unconfirmed';
  if (assignment.routeCurrent === true && verifiedRevision === vm.revision) return 'current';
  if (assignment.routeStatus?.state === 'ReviewRequired') return 'review';
  return 'unconfirmed';
}

const activeAssignments = vm => (vm.dispatch?.assignments ?? [])
  .filter(assignment => assignment.route && !['Completed', 'Cancelled'].includes(assignment.state));

// Recorded ORS geometry when available, otherwise the exercise graph segments the route was planned on.
function legs(vm, assignment) {
  const route = assignment.route;
  const fromSegments = ids => (ids ?? []).map(id => segmentPoints(vm.network, id)).filter(Boolean);
  const leg = (geometry, ids) => geometry?.length ? [geometry] : fromSegments(ids);
  return [
    { approach: true, parts: leg(route.approachGeometry, route.approachSegments) },
    { approach: false, parts: leg(route.outboundGeometry, route.outboundSegments) },
  ];
}

function update(vm, { layer }) {
  layer.clearLayers();
  const add = sink(layer);
  const stale = staleServices(vm, SERVICES).length > 0;
  for (const assignment of activeAssignments(vm)) {
    const status = legStatus(vm, assignment, stale);
    for (const { approach, parts } of legs(vm, assignment)) {
      if (approach && ['AtPickup', 'PickupConfirmed'].includes(assignment.state)) continue;
      const style = status === 'current' && approach ? { ...styles.current, dashArray: '7 5' } : styles[status];
      const pane = status === 'withdrawn' ? PANES.withdrawn : PANES.routes;
      for (const points of parts) add(entries[status], casedLine(points, style, pane));
    }
    if (status === 'withdrawn' || status === 'review') {
      const outbound = legs(vm, assignment)[1].parts;
      const points = outbound[Math.floor(outbound.length / 2)];
      if (points) add(entries[status], badgeMarker(latLng(points[Math.floor((points.length - 1) / 2)]),
        status === 'withdrawn' ? 'Trasa jednostki wycofana' : 'Do przeliczenia',
        status === 'withdrawn' ? 'badge-withdrawn' : 'badge-review'));
    }
  }
}

const labels = {
  current: 'Dojazd (przerywana) i wyjazd jednostki', withdrawn: 'Trasa jednostki wycofana',
  review: 'Trasa jednostki do przeliczenia', unconfirmed: 'Trasa jednostki niepotwierdzona',
};

function legend(vm) {
  if (!vm.dispatch) return null;
  const stale = staleServices(vm, SERVICES).length > 0;
  const groups = new Map();
  for (const assignment of activeAssignments(vm)) {
    const status = legStatus(vm, assignment, stale);
    const route = assignment.route;
    const source = route.provenance
      ? provenance(route.provenance)
      : provenance({ source: 'Routing · graf ćwiczeniowy', mode: 'synthetic' });
    const status_ = assignment.routeStatus;
    const reason = status === 'withdrawn' && status_?.segments?.length ? ' · zamknięte: ' + status_.segments.map(segmentLabel).join(', ') : '';
    const group = groups.get(status) ?? { lines: [], modes: new Set() };
    group.lines.push(assignment.unitId + ' → ' + label(route.exit) + ' · rewizja ' + route.revision + ' · ' + source + reason);
    group.modes.add(route.provenance?.mode ?? 'synthetic');
    groups.set(status, group);
  }
  return [...groups].map(([status, group]) => ({
    id: entries[status], label: labels[status], swatch: 'legend-leg-' + status, provenance: group.lines,
    modes: [...group.modes], ...(status === 'unconfirmed' ? { stale: true } : {}),
  }));
}

defineLayer('dispatch', { title: 'Trasy jednostek', group: 'Jednostki', order: 70, services: SERVICES, update, legend });
