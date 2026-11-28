// S4: presentation mode. Walks through the demo script ("Demo script" in docs/tasks/README.md) with a "Dalej" button.
// Each step calls the existing commands through run() and api(), so refresh, the panels and onCommand stay consistent.
// Test hooks: #presentation-toggle, #presentation-next, #presentation-replay and [data-step-id][data-step-status]
// with status pending | running | done | blocked | error.
import { api, commandId } from './api.js';
import { $, element } from './dom.js';
import { fitTo } from './map.js';
import { run, state, subscribe, viewModel } from './state.js';
import { sampleSteps } from './presentation/steps-sample.js';
import { valenciaSteps } from './presentation/steps-valencia.js';

const CONFLICTS = new Set(['RevisionConflict', 'SituationChanged', 'VersionConflict']);
const ID_CONFLICTS = new Set(['CommandConflict', 'CommandIdConflict']);
const LABELS = { pending: 'Do wykonania', running: 'W toku', done: 'Wykonano', blocked: 'Oczekuje', error: 'Błąd' };
const SCENARIOS = { valencia: 'Walencja', sample: 'Kłodzko (ćwiczenie syntetyczne)' };

// Command bodies per step and key, kept for this page. A retry reuses the commandId; the body is rebuilt
// with a fresh revision or version only after the service rejected it, so a lost response replays safely.
const commands = new Map();
const results = new Map();
let steps = null;
let scenario = null;
let cursor = 0;
let active = false;
let running = false;
let lastRun = null;
let panel = null;

function stepContext(step) {
  const key = name => step.id + ':' + name;
  return {
    read: (service, path) => api(service, 'queries/' + path),
    has: name => Boolean(commands.get(key(name))?.body),
    commandIdOf: name => commands.get(key(name))?.commandId,
    async command(name, service, operation, build, options) {
      let entry = commands.get(key(name));
      if (!entry) {
        entry = { commandId: 'pres-' + step.id + '-' + name + '-' + commandId().slice(0, 8), body: null,
          rebuild: true, done: false, attempts: 0, service, operation };
        commands.set(key(name), entry);
      }
      if (!entry.body || entry.rebuild) {
        entry.previousBody = entry.body;
        entry.body = { commandId: entry.commandId, ...await build() };
        entry.rebuild = false;
      }
      entry.attempts += 1;
      try {
        const response = await api(service, 'commands/' + operation, entry.body, options);
        Object.assign(entry, { done: true, error: null });
        return response;
      } catch (error) {
        // The same commandId with a different body: an earlier attempt was applied after all (its response was
        // lost). Replay that earlier body unchanged so the service returns its stored result.
        if (ID_CONFLICTS.has(error.code) && entry.previousBody) {
          entry.body = entry.previousBody;
          entry.previousBody = null;
          entry.attempts += 1;
          const response = await api(service, 'commands/' + operation, entry.body, options);
          Object.assign(entry, { done: true, error: null });
          return response;
        }
        // Any other 4xx means the service did not apply the command, so the next try rebuilds the body with a
        // fresh revision or version. A lost response or 5xx keeps the identical body for a safe replay.
        if (!entry.done && error.status >= 400 && error.status < 500 && !ID_CONFLICTS.has(error.code)) entry.rebuild = true;
        entry.error = error.code ?? error.message;
        throw error;
      }
    },
  };
}

const result = id => results.get(id) ?? { status: 'pending' };
const setResult = (id, value) => results.set(id, { ...result(id), ...value });

function commandLines(step) {
  return [...commands.entries()].filter(([key]) => key.startsWith(step.id + ':')).map(([, entry]) =>
    entry.service + '/' + entry.operation + ' · ' + entry.commandId + ' · wysłano ' + entry.attempts + '×' +
    (entry.error ? ' · ostatnio: ' + entry.error : ''));
}

async function waitIdle() {
  for (let i = 0; i < 150 && state.busy; i++) await new Promise(resolve => setTimeout(resolve, 100));
  return !state.busy;
}

function focus(target) {
  const bounds = typeof target === 'function' ? target() : target;
  if (bounds) fitTo(bounds);
  $('map').scrollIntoView({ block: 'start' });
}

async function execute(step, replay = false) {
  running = true;
  const previous = result(step.id);
  setResult(step.id, { status: 'running' });
  render();
  try {
    if (!await waitIdle()) {
      setResult(step.id, { ...previous, note: 'Panel wykonuje inną operację. Spróbuj ponownie.' });
      return false;
    }
    let outcome = null;
    await run(async () => {
      try {
        const value = await step.run(stepContext(step));
        outcome = { ok: true, value };
        return 'Prezentacja · ' + step.title + ': ' + value.message;
      } catch (error) {
        outcome = { ok: false, error };
        throw error;
      }
    });
    if (!outcome) {
      setResult(step.id, { ...previous, note: 'Panel wykonuje inną operację. Spróbuj ponownie.' });
      return false;
    }
    if (outcome.ok) {
      const { message, details = [], after, focus: target, tone = null } = outcome.value;
      setResult(step.id, { status: 'done', message, details, tone, note: replay
        ? 'Ponowiono z tymi samymi commandId — usługi zwróciły zapisany wynik, bez nowych zmian.' : null });
      after?.();
      focus(target);
      lastRun = step.id;
      return true;
    }
    const { error } = outcome;
    let note = null;
    if (CONFLICTS.has(error.code)) {
      // run() already invalidated the view; read it again before the retry.
      await run(async () => 'Dane zmieniły się w trakcie kroku (' + error.code + '). Odświeżono widok — ponów krok.');
      note = 'Dane zmieniły się od odczytu (' + error.code + '). Widok odświeżono; „Ponów krok” użyje tego samego commandId ' +
        'z aktualną rewizją.';
    }
    if (replay && previous.status === 'done') {
      setResult(step.id, { ...previous, note: 'Ponowienie odrzucone (' + (error.code ?? error.message) + '). ' +
        'Pierwotny wynik kroku pozostaje bez zmian.' });
    } else {
      setResult(step.id, { status: 'error', message: error.message, details: [], note, tone: null });
    }
    return false;
  } finally {
    running = false;
    render();
  }
}

async function probe(step) {
  if (!step.probe) return;
  const reason = await step.probe();
  if (reason) setResult(step.id, { status: 'blocked', message: reason });
  else if (result(step.id).status === 'blocked') setResult(step.id, { status: 'pending', message: null });
}

async function next() {
  if (running || !steps) return;
  const step = steps[cursor];
  if (!step) return;
  if (step.probe && result(step.id).status === 'blocked') {
    running = true;
    render();
    try { await probe(step); } finally { running = false; }
  }
  const status = result(step.id).status;
  if (status === 'pending' || status === 'error') {
    if (await execute(step)) cursor = Math.min(cursor + 1, steps.length);
  } else {
    cursor = Math.min(cursor + 1, steps.length);
  }
  render();
  steps[cursor]?.probe && result(steps[cursor].id).status === 'blocked' && probe(steps[cursor]).then(render);
}

function select(index) {
  if (running) return;
  cursor = index;
  render();
  const step = steps[index];
  if (step.probe && result(step.id).status === 'blocked') probe(step).then(render);
}

function initSteps(vm) {
  scenario = vm.scenario === 'valencia' ? 'valencia' : 'sample';
  steps = scenario === 'valencia' ? valenciaSteps() : sampleSteps();
  for (const step of steps) results.set(step.id, step.blocked
    ? { status: 'blocked', message: step.blocked } : { status: 'pending' });
}

function stepItem(step, index) {
  const { status, message } = result(step.id);
  const item = element('li', null, 'presentation-step status-' + status + (index === cursor ? ' current' : ''));
  item.dataset.stepId = step.id;
  item.dataset.stepStatus = status;
  const pick = element('button', null, 'presentation-step-title');
  pick.type = 'button';
  pick.disabled = running;
  pick.append(element('span', String(index), 'presentation-step-number'), element('span', step.title));
  pick.addEventListener('click', () => select(index));
  item.append(pick, element('span', LABELS[status], 'presentation-status'));
  if (status === 'blocked' && message) item.append(element('p', message, 'presentation-reason'));
  if (index === cursor) item.setAttribute('aria-current', 'step');
  return item;
}

// The result of the step that ran last stays visible while the next step is announced.
function lastResultCard() {
  const step = steps.find(item => item.id === lastRun);
  if (!step || step === steps[cursor]) return null;
  const { status, message, details = [], note, tone } = result(step.id);
  const card = element('div', null, 'presentation-result status-' + status + (tone ? ' tone-' + tone : ''));
  card.append(element('p', 'Wynik · ' + step.title, 'presentation-result-title'));
  if (message) card.append(element('p', message, 'presentation-message status-' + status + (tone ? ' tone-' + tone : '')));
  if (note) card.append(element('p', note, 'presentation-note'));
  const lines = [...details, ...commandLines(step)];
  if (lines.length) {
    const list = element('ul', null, 'presentation-details');
    for (const line of lines) list.append(element('li', line));
    card.append(list);
  }
  return card;
}

function currentCard() {
  const card = element('div', null, 'presentation-current');
  const step = steps[cursor];
  if (!step) {
    card.append(element('h3', 'Koniec scenariusza'),
      element('p', 'Wszystkie kroki zostały przejrzane. Wybierz krok z listy, aby wrócić.', 'presentation-summary'));
    return card;
  }
  const { status, message, details = [], note, tone } = result(step.id);
  card.append(element('p', 'Krok ' + cursor + ' z ' + (steps.length - 1) + ' · ' + LABELS[status], 'eyebrow'),
    element('h3', step.title), element('p', step.summary, 'presentation-summary'));
  if (message) card.append(element('p', message, 'presentation-message status-' + status + (tone ? ' tone-' + tone : '')));
  if (note) card.append(element('p', note, 'presentation-note'));
  const lines = [...details, ...commandLines(step)];
  if (lines.length) {
    const list = element('ul', null, 'presentation-details');
    for (const line of lines) list.append(element('li', line));
    card.append(list);
  }
  return card;
}

function controls() {
  const box = element('div', null, 'buttons presentation-controls');
  const step = steps[cursor];
  const status = step ? result(step.id).status : null;
  const nextButton = element('button', status === 'error' ? 'Ponów krok' : status === 'pending' ? 'Dalej · wykonaj krok' : 'Dalej',
    'primary');
  nextButton.id = 'presentation-next';
  nextButton.type = 'button';
  nextButton.disabled = running || !step;
  nextButton.addEventListener('click', next);
  const replayStep = steps.find(item => item.id === lastRun);
  const replay = element('button', replayStep ? 'Ponów „' + replayStep.title + '” (te same commandId)' : 'Ponów ostatni krok');
  replay.id = 'presentation-replay';
  replay.type = 'button';
  replay.disabled = running || !replayStep;
  replay.addEventListener('click', () => replayStep && execute(replayStep, true));
  box.append(nextButton, replay);
  return box;
}

function render() {
  if (!panel) return;
  panel.hidden = false;
  document.body.classList.toggle('presentation-active', active);
  const head = element('div', null, 'card-heading presentation-head');
  const title = element('div');
  title.append(element('h2', 'Tryb prezentacji'), element('p', steps
    ? 'Scenariusz: ' + SCENARIOS[scenario] + ' · ' + (steps.length - 1) + ' kroków skryptu demo'
    : 'Oczekiwanie na odczyt scenariusza…'));
  const toggle = element('button', active ? 'Zamknij prezentację' : 'Tryb prezentacji');
  toggle.id = 'presentation-toggle';
  toggle.type = 'button';
  toggle.disabled = !steps;
  toggle.setAttribute('aria-expanded', String(active));
  toggle.setAttribute('aria-controls', 'presentation-body');
  toggle.addEventListener('click', () => {
    active = !active;
    render();
    if (active) $('map').scrollIntoView({ block: 'start' });
    if (active && steps[cursor]?.probe) probe(steps[cursor]).then(render);
  });
  head.append(title, toggle);
  const body = element('div', null, 'presentation-body');
  body.id = 'presentation-body';
  body.hidden = !active || !steps;
  if (steps) {
    const list = element('ol', null, 'presentation-steps');
    list.append(...steps.map(stepItem));
    body.append(...[lastResultCard()].filter(Boolean), currentCard(), controls(), list,
      element('p', 'Dane ćwiczenia są syntetyczne. Detekcje importowane przez prezentację są oznaczone jako SYMULACJA. ' +
        'Kroki „Oczekuje” nie wysyłają komend i nie pokazują potwierdzeń.', 'presentation-footnote'));
  }
  panel.replaceChildren(head, body);
}

function init() {
  panel = $('presentation-panel');
  if (!panel) return;
  panel.classList.add('presentation');
  render();
  subscribe((vm, info) => {
    if (steps || info.reason !== 'refresh' || !vm.scenario) return;
    initSteps(vm);
    render();
  });
}

init();

// Read-only handle for debugging and the end-to-end test.
window.cassiniPresentation = Object.freeze({
  steps: () => steps?.map(step => ({ id: step.id, ...result(step.id) })) ?? [],
  commands: () => Object.fromEntries([...commands].map(([key, entry]) => [key, { commandId: entry.commandId,
    attempts: entry.attempts, body: entry.body }])),
  viewModel,
});
