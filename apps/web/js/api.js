import { emitCommand } from './bus.js';

export const commandId = () => crypto.randomUUID();

const optionalJson = url => fetch(url)
  .then(response => response.ok ? response.json() : null)
  .catch(() => null);

export const satelliteScenes = optionalJson('/maps/satellite.json');

export function cityImagery() {
  return fetch('/maps/city-imagery.json').then(response => {
    if (!response.ok) throw new Error('Brak lokalnych metadanych scen Walencji.');
    return response.json();
  });
}

// S8: optional Authorization header (Keycloak profile `full`); fn returns a header value or null.
let authHeader = () => null;
export function setAuthHeaderProvider(fn) { authHeader = fn; }
// Current Authorization value (Keycloak) for modules that build their own requests, or null.
export const authorization = () => authHeader();

// GET /api/{service}/{path} without data, POST with JSON data. A POST result is reported to onCommand.
// options.headers adds request headers, e.g. X-Cassini-Link: field for a command sent over the field link (task 6).
export async function api(service, path, data, options = {}) {
  const command = data !== undefined;
  const authorization = await authHeader();
  const headers = { ...options.headers, ...(authorization ? { Authorization: authorization } : {}) };
  const response = await fetch('/api/' + service + '/' + path, command ? {
    method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify(data),
  } : { headers }).catch(error => {
    if (command) emitCommand({ operation: operationName(path), service, body: data, ok: false,
      status: 0, response: null, at: new Date().toISOString() });
    throw error;
  });
  const body = await response.json().catch(() => null);
  // 202 Queued from the link simulator is not a delivery: the owner has not applied the command yet.
  const queued = response.status === 202 && body?.status === 'Queued';
  if (command) emitCommand({ operation: operationName(path), service, body: data, ok: response.ok && !queued, queued,
    status: response.status, response: body, at: new Date().toISOString() });
  if (!response.ok)
    throw Object.assign(new Error(body?.detail || body?.title || body?.code || 'HTTP ' + response.status),
      { status: response.status, code: body?.code });
  return body;
}

const operationName = path => path.replace(/^commands\//, '');
