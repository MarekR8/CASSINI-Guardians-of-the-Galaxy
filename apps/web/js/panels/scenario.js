// Exercise road closures and the flood alert.
import { api, commandId } from '../api.js';
import { $ } from '../dom.js';
import { run, state } from '../state.js';

const closure = (segmentId, closed, expectedRevision = state.situation.revision) =>
  api('situation', 'commands/closure', { commandId: commandId(), segmentId, closed, expectedRevision });

export function initScenario() {
  $('bridge').addEventListener('click', () => run(async () => { await closure('bridge-2', true); return 'Most został zamknięty.'; }));
  $('north').addEventListener('click', () => run(async () => { await closure('north-link', true); return 'Północne dojście zostało zamknięte.'; }));
  $('reopen').addEventListener('click', () => run(async () => {
    let latest = state.situation;
    for (const id of latest.closedSegments) latest = await closure(id, false, latest.revision);
    state.draw = null;
    state.pendingDraw = null;
    return 'Przywrócono dostęp do przejść.';
  }));
  $('alert').addEventListener('click', () => run(() =>
    api('management', 'commands/alert', { commandId: commandId() }).then(() => 'Zapisano zgłoszenie zalania.')));
}
