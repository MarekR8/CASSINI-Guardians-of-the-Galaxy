// A tiny synchronous event bus. A failing listener is logged and does not stop the others.
const refreshListeners = [];
const commandListeners = [];

function emit(listeners, ...args) {
  for (const listener of listeners) {
    try {
      listener(...args);
    } catch (error) {
      console.error(error);
    }
  }
}

// fn(viewModel, info) runs after every successful refresh, after a read is invalidated
// and after a local redraw request. See state.js for the shapes.
export function subscribe(fn) {
  refreshListeners.push(fn);
  return () => refreshListeners.splice(refreshListeners.indexOf(fn), 1);
}

// fn({ operation, service, body, ok, status, response, at }) runs after each command result.
export function onCommand(fn) {
  commandListeners.push(fn);
  return () => commandListeners.splice(commandListeners.indexOf(fn), 1);
}

export const emitRefresh = (viewModel, info) => emit(refreshListeners, viewModel, info);
export const emitCommand = result => emit(commandListeners, result);
