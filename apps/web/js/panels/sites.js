// Assembly points and shelters (#site-catalog) from the optional sheltering overlay. The panel itself
// (../../site-catalog.js) checks /features; this module only gives it the incident and scenario time of the
// last consistent read, or null when there is none (Valencia, lost read, or a viewer who cannot read cases).
import { api } from '../api.js';
import { $ } from '../dom.js';
import { createSiteCatalog } from '../../site-catalog.js';
import { subscribe } from '../state.js';

export function initSites() {
  const catalog = createSiteCatalog($('site-catalog'), api);
  let current = null;
  subscribe((vm, info) => {
    if (info.reason === 'redraw') return;
    const next = info.reason === 'refresh' && vm.scenario !== 'valencia' && vm.incident && vm.distribution
      ? { incidentId: vm.incident.incidentId, at: vm.distribution.at } : null;
    // Every refresh re-reads an open panel, as before; a closed one only records the context.
    if (next || current) catalog.update(next);
    current = next;
  });
}
