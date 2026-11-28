// Map status chips: stale or unavailable data, withdrawn route and no route are separate, visibly different states.
import { element } from '../dom.js';
import { label, time } from '../format.js';
import { state } from '../state.js';
import { defineLayer, L, serviceName, staleServices } from './common.js';

const SERVICES = ['situation', 'routing', 'dispatch', 'management'];
let control = null;
let signature = null;

function chips(vm) {
  const list = [];
  const stale = staleServices(vm, vm.scenario === 'valencia' ? ['situation', 'routing'] : SERVICES);
  if (stale.length) {
    list.push({ className: 'status-stale', text: 'Dane nieaktualne · ' + stale.map(serviceName).join(', ') +
      (vm.fetchedAt ? ' · ostatni odczyt ' + time(vm.fetchedAt) + ' UTC' : '') + ' · mapa nie potwierdza dostępności' });
  }
  if (vm.draw?.state === 'Withdrawn') list.push({ className: 'status-withdrawn', text: 'Trasa wycofana po zmianie dostępności' });
  if (!stale.length && (vm.dispatch?.assignments ?? []).some(item => item.routeStatus?.state === 'Blocked' &&
    !['Completed', 'Cancelled'].includes(item.state)))
    list.push({ className: 'status-withdrawn', text: 'Trasa jednostki wycofana · wymaga przeliczenia' });
  if (vm.distribution && !vm.distribution.options.length)
    list.push({ className: 'status-no-route', text: 'Brak trasy z węzła ' + label(vm.distribution.node) });
  if (vm.scenario === 'valencia' && state.cityPath && vm.readAvailable && state.cityPath.revision === vm.revision)
    list.push(state.cityPath.route ? { className: 'status-current', text: 'Trasa testowa OSM dostępna w rewizji ' + vm.revision }
      : { className: 'status-no-route', text: 'Brak trasy w rewizji ' + vm.revision });
  // Valencia can show the OSM test route and a drawn evacuation route at the same time.
  if (!stale.length && vm.draw?.state === 'Active')
    list.push({ className: 'status-current', text: 'Trasa aktywna · ' + label(vm.draw.route.exit) });
  return list;
}

function update(vm, { map }) {
  if (!control) {
    control = L.control({ position: 'bottomleft' });
    control.onAdd = () => {
      const node = L.DomUtil.create('div', 'map-status');
      node.setAttribute('role', 'status');
      node.setAttribute('aria-live', 'polite');
      return node;
    };
    control.addTo(map);
  }
  const list = chips(vm);
  map.getContainer().classList.toggle('layers-stale', list.some(chip => chip.className === 'status-stale'));
  const next = JSON.stringify(list);
  if (next === signature) return;
  signature = next;
  control.getContainer().replaceChildren(...list.map(chip => element('span', chip.text, 'map-status-chip ' + chip.className)));
}

defineLayer('status', { title: 'Stan danych', group: 'Stan', order: 200, update });
