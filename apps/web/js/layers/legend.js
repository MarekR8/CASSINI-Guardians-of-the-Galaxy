// Legend generated from the layer registry: swatch, label, provenance (source · mode · observedAt · productId),
// SYMULACJA badge, exercise vs operator data, stale badge and a visibility checkbox per entry (kept in memory).
import { $, element } from '../dom.js';
import { redraw, setLegendRenderer } from '../map.js';
import { origin, simulated, visible, setVisible } from './common.js';

const GROUPS = ['Sytuacja', 'Sieć i trasy', 'Jednostki', 'Węzły', 'Tło mapy'];
const ORIGINS = {
  exercise: ['Warstwa ćwiczeniowa', 'badge-exercise'],
  imported: ['Dane operatora', 'badge-imported'],
  file: ['Plik lokalny', 'badge-file'],
};
let signature = null;

function badges(item) {
  const modes = item.modes ?? (item.mode ? [item.mode] : []);
  const list = [];
  if (modes.some(simulated)) list.push(['SYMULACJA', 'badge-simulation']);
  const origins = item.origin ? [item.origin] : [...new Set(modes.map(origin).filter(Boolean))];
  for (const key of origins) if (ORIGINS[key]) list.push(ORIGINS[key]);
  if (item.stale) list.push(['Dane nieaktualne', 'badge-stale']);
  return list;
}

const checked = item => item.control ? Boolean(item.checked) : visible(item.id);

function toggle(item, on) {
  if (item.control) {
    // Entries mirroring a map tool (zones) switch that tool, which redraws the map itself.
    const input = $(item.control);
    input.checked = on;
    input.dispatchEvent(new Event('change'));
    return;
  }
  setVisible(item.id, on);
  redraw();
}

function entry(item) {
  const node = element('div', null, ['legend-entry', item.className, item.stale && 'is-stale', !checked(item) && 'is-hidden']
    .filter(Boolean).join(' '));
  node.dataset.legend = item.id;
  node.dataset.layer = item.layer;
  const toggleLabel = element('label', null, 'legend-toggle');
  const input = element('input');
  input.type = 'checkbox';
  input.checked = checked(item);
  input.dataset.legendToggle = item.id;
  input.setAttribute('aria-label', 'Pokaż warstwę: ' + item.label);
  input.addEventListener('change', () => toggle(item, input.checked));
  toggleLabel.append(input, element('i', null, 'legend-swatch ' + item.swatch), element('span', item.label, 'legend-label'));
  node.append(toggleLabel);
  const list = badges(item);
  if (list.length) {
    const row = element('span', null, 'legend-badges');
    row.append(...list.map(([text, className]) => element('b', text, 'legend-badge ' + className)));
    node.append(row);
  }
  for (const line of [item.provenance ?? item.detail].flat().filter(Boolean))
    node.append(element('small', line, 'legend-provenance'));
  return node;
}

function render(container, items) {
  const next = JSON.stringify(items.map(item => ({ ...item, checked: checked(item) })));
  if (next === signature) return;
  signature = next;
  const focused = document.activeElement?.dataset?.legendToggle;
  container.classList.add('layer-legend');
  const groups = [...new Set([...GROUPS, ...items.map(item => item.group)])];
  const sections = [];
  for (const group of groups) {
    const members = items.filter(item => item.group === group);
    if (!members.length) continue;
    const section = element('section', null, 'legend-group');
    section.append(element('h3', group, 'legend-group-title'), ...members.map(entry));
    sections.push(section);
  }
  container.replaceChildren(...sections);
  if (focused) container.querySelector('[data-legend-toggle="' + CSS.escape(focused) + '"]')?.focus();
}

setLegendRenderer(render);
