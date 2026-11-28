import { api, cityImagery, satelliteScenes } from './api.js';
import { emitRefresh } from './bus.js';
import { $ } from './dom.js';

export { subscribe, onCommand } from './bus.js';

const SERVICES = ['management', 'situation', 'routing', 'dispatch'];
const POLL_INTERVAL = 7000;

// Mutable application state. Panels own their UI fields; read data through viewModel().
export const state = {
  network: null, situation: null, incident: null, dispatch: null, distribution: null, draw: null, readAvailable: false,
  cityImagery: null, closedRoads: [], satelliteScene: null,
  pendingDraw: null, subject: null, busy: false, epoch: 0, polling: false,
  cityPicking: false, cityPoints: [], cityPath: null, satelliteDetail: false,
  stale: Object.fromEntries(SERVICES.map(service => [service, true])), fetchedAt: null,
};

// Geometry of drawn Valencia evacuation routes by draw commandId. Routing returns OSM node IDs only, so the
// presentation looks the nodes up (queries/city-node) and the routes layer draws the line from here.
export const routeGeometry = new Map();

const hooks = { notice: () => {}, syncControls: () => {}, defaultMessage: () => '' };

export function setHooks(next) {
  Object.assign(hooks, next);
}

// Read-only view of the last consistent read.
export function viewModel() {
  return Object.freeze({
    scenario: state.network?.scenario ?? null,
    network: state.network,
    snapshot: state.situation,
    incident: state.incident,
    dispatch: state.dispatch,
    distribution: state.distribution,
    draw: state.draw,
    cityImagery: state.cityImagery,
    closedRoads: state.closedRoads,
    satellite: state.satelliteScene,
    revision: state.situation?.revision ?? null,
    readAvailable: state.readAvailable,
    stale: Object.freeze({ ...state.stale }),
    fetchedAt: state.fetchedAt,
  });
}

// info.reason: 'refresh' (successful read), 'invalidate' (read lost or command failed), 'redraw' (local UI change).
export function notify(reason, details = {}) {
  emitRefresh(viewModel(), { reason, restored: false, revisionChanged: false, cityPathUpdated: false, ...details });
}

// Services this viewer may read. With Keycloak a resident reads only situation and routing (INV-14); the panels
// treat a skipped read like the Valencia scenario does: no cases and no unit tasks, not stale data.
const readable = new Set(SERVICES);
export function setReadableServices(services) {
  readable.clear();
  for (const service of services) readable.add(service);
}

let failures = new Set();
function read(service, path) {
  return api(service, path).then(body => {
    failures.delete(service);
    return body;
  }, error => {
    failures.add(service);
    throw error;
  });
}

export function requestCityPath() {
  const [from, to] = state.cityPoints.map(value => value.map(number => number.toFixed(6)).join(','));
  const query = new URLSearchParams({ from, to, mode: $('city-mode').value });
  return read('routing', 'queries/city-route?' + query);
}

export async function refresh(epoch) {
  failures = new Set();
  const network = state.network ?? await read('routing', 'queries/network');
  const valencia = network.scenario === 'valencia';
  const [situation, incident, dispatch, scenes, imagery] = await Promise.all([
    read('situation', 'queries/snapshot'),
    valencia || !readable.has('management') ? null : read('management', 'queries/incident'),
    valencia || !readable.has('dispatch') ? null : read('dispatch', 'queries/assignments'),
    satelliteScenes,
    valencia ? state.cityImagery ?? cityImagery() : null,
  ]);
  if (state.epoch !== epoch) return;
  const node = $('node').value;
  const previousCityPath = state.cityPath;
  const revisionChanged = state.situation?.revision !== situation.revision;
  const [distribution, draw, cityPath, closureLayer] = await Promise.all([
    valencia ? null : read('routing', 'queries/distribution?node=' + encodeURIComponent(node)),
    state.draw ? read('routing', 'queries/draws/' + state.draw.id) : null,
    previousCityPath && (previousCityPath.revision !== situation.revision || !state.readAvailable)
      ? requestCityPath() : null,
    valencia && revisionChanged ? read('situation', 'queries/closed-roads') : null,
  ]);
  if (state.epoch !== epoch) return;
  if ([distribution, draw, cityPath, closureLayer].some(view => view && view.revision !== situation.revision))
    throw new Error('Sytuacja zmieniła się podczas odczytu.');

  const restored = !state.readAvailable;
  state.readAvailable = true;
  state.network = network;
  state.situation = situation;
  state.incident = incident;
  state.dispatch = dispatch;
  state.cityImagery = imagery;
  if (closureLayer) state.closedRoads = closureLayer.roads;
  state.distribution = distribution;
  if (draw) state.draw = { id: state.draw.id, ...draw };
  const cityPathUpdated = Boolean(cityPath && state.cityPath === previousCityPath);
  if (cityPathUpdated) state.cityPath = cityPath;
  state.satelliteScene = valencia ? null : scenes?.[network.scenario] ?? null;
  for (const service of SERVICES) state.stale[service] = false;
  state.fetchedAt = new Date().toISOString();
  notify('refresh', { restored, revisionChanged, cityPathUpdated });
}

export function invalidateRead() {
  state.distribution = null;
  state.readAvailable = false;
  if (state.draw) state.draw = { id: state.draw.id, state: 'Unknown', route: null };
  for (const service of SERVICES) state.stale[service] = failures.size ? failures.has(service) || state.stale[service] : true;
  notify('invalidate');
}

export async function run(action) {
  if (state.busy) return;
  state.busy = true;
  const epoch = ++state.epoch;
  hooks.syncControls();
  let message;
  try {
    message = await action();
    await refresh(epoch);
    hooks.notice(message || hooks.defaultMessage());
  } catch (error) {
    invalidateRead();
    hooks.notice('Nie udało się wykonać operacji. ' + error.message, true);
  } finally {
    state.busy = false;
    hooks.syncControls();
  }
}

export function startPolling() {
  setInterval(async () => {
    if (state.busy || state.polling) return;
    state.polling = true;
    const epoch = state.epoch;
    const wasUnavailable = !state.readAvailable;
    try {
      await refresh(epoch);
      if (state.epoch === epoch && wasUnavailable && state.readAvailable)
        hooks.notice('Przywrócono aktualny widok scenariusza.');
    } catch {
      if (state.epoch === epoch) {
        invalidateRead();
        hooks.notice('Widok czeka na odpowiedź lokalnych usług. Odświeżenie nastąpi automatycznie.', true);
      }
    } finally {
      state.polling = false;
      if (state.epoch === epoch) hooks.syncControls();
    }
  }, POLL_INTERVAL);
}
