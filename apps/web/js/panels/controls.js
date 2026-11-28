// Enables and disables controls for the current read and the operation in progress.
import { $ } from '../dom.js';
import { state } from '../state.js';

export function syncControls() {
  const closed = state.situation?.closedSegments || [];
  $('node').disabled = state.busy || !state.situation;
  $('draw').disabled = state.busy || !state.distribution?.options.length || state.draw?.state === 'Active';
  $('bridge').disabled = state.busy || !state.situation || closed.includes('bridge-2');
  $('north').disabled = state.busy || !closed.includes('bridge-2') || closed.includes('north-link');
  $('reopen').disabled = state.busy || !closed.length;
  $('alert').disabled = state.busy || !state.situation;
  for (const control of $('reviews').querySelectorAll('button'))
    control.disabled = state.busy || !state.situation;
  for (const control of $('requests').querySelectorAll('button, select'))
    control.disabled = state.busy || !state.readAvailable;
}
