// Route distribution for the selected node and the draw command.
import { api, commandId } from '../api.js';
import { $, element, text } from '../dom.js';
import { label, minutes, time } from '../format.js';
import { run, state, subscribe } from '../state.js';

export function renderDistribution() {
  const options = state.distribution?.options ?? [];
  const list = $('options');
  list.replaceChildren();
  if (!options.length) list.append(element('p', state.distribution ? 'Brak dostępnej trasy.' : 'Oczekiwanie na rozkład tras.'));
  for (const route of options) {
    const item = element('div', null, 'option');
    const head = element('div', null, 'option-head');
    const chance = Math.round(route.weight * 100);
    head.append(element('b', label(route.exit)), element('strong', chance + '%'));
    const meter = element('progress', null, 'probability');
    meter.max = 100;
    meter.value = chance;
    meter.setAttribute('aria-label', 'Szansa: ' + label(route.exit));
    item.append(head, element('p', time(route.departsAt) + ' – ' + time(route.arrivesAt) + ' UTC · marsz ' + minutes(route.seconds)), meter);
    list.append(item);
  }
  const result = $('draw-result');
  const route = state.draw?.route;
  result.className = 'draw-result';
  if (route && state.draw.state === 'Active') {
    result.classList.add('active');
    result.textContent = 'Wylosowano: ' + label(route.exit) + '. Przybycie ' + time(route.arrivesAt) + ' UTC.';
  } else if (state.draw?.state === 'Withdrawn') {
    result.classList.add('withdrawn');
    result.textContent = 'Trasa wycofana po zmianie dostępności.' + (options.length ? ' Wylosuj nową trasę.' : '');
  } else if (state.draw?.state === 'Expired') {
    result.textContent = 'Ważność trasy upłynęła.';
  } else if (state.draw?.state === 'Unknown' || state.pendingDraw) {
    result.textContent = 'Wynik losowania czeka na potwierdzenie.';
  } else {
    result.textContent = 'Tutaj pojawi się wynik losowania.';
  }
}

async function draw() {
  const { revision, node, at } = state.distribution;
  const subjectId = state.subject?.caseId;
  if (state.pendingDraw?.revision !== revision)
    state.pendingDraw = { commandId: commandId(), node, revision, at, subjectId };
  try {
    const result = await api('routing', 'commands/draw', state.pendingDraw);
    state.draw = { id: state.pendingDraw.commandId, state: 'Active', ...result };
    state.pendingDraw = null;
  } catch (error) {
    if (error.status >= 400 && error.status < 500) state.pendingDraw = null;
    throw error;
  }
  return 'Węzeł przydzielił trasę z aktualnego rozkładu.';
}

export function initDistribution() {
  subscribe((vm, info) => {
    if (info.reason !== 'redraw' && vm.scenario !== 'valencia') renderDistribution();
  });
  $('node').addEventListener('change', () => run(() => {
    state.draw = null;
    state.pendingDraw = null;
    state.subject = null;
    text('recipient', 'Przejścia piesze z wybranego węzła');
  }));
  $('draw').addEventListener('click', () => run(draw));
}
