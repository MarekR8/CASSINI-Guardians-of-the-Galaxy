// Cases, assistance requests and field units.
import { api, commandId } from '../api.js';
import { $, element, text } from '../dom.js';
import { label, minutes, statuses } from '../format.js';
import { state, subscribe } from '../state.js';
import { button } from './button.js';

const dispatchSelections = new Map();
const dispatchErrors = new Map();
// Unit-candidate rankings per request, valid for the situation revision and request version they were read at.
const dispatchRankings = new Map();
let operationsSignature = null;

function renderOperations(incident, dispatch) {
  text('alert-count', 'Liczba zgłoszeń: ' + incident.alerts.length);
  $('cases').replaceChildren(...incident.cases.map(item => {
    const row = element('li');
    const info = element('div');
    info.append(element('b', item.name || item.caseId), element('p', (statuses[item.progress] || item.progress) + ' · ' + label(item.node)));
    row.append(info);
    const actions = element('div', null, 'buttons');
    if (item.mobility === 'Walking' && item.progress !== 'ExitConfirmed') {
      actions.append(button('Pokaż rozkład', () => {
        if (state.subject?.caseId !== item.caseId || $('node').value !== item.node) {
          if (![...$('node').options].some(option => option.value === item.node))
            $('node').add(new Option(label(item.node), item.node));
          $('node').value = item.node;
          state.draw = null;
          state.pendingDraw = null;
        }
        state.subject = item;
        text('recipient', 'Przejścia piesze · ' + item.name);
      }));
    }
    if (item.progress !== 'ExitConfirmed' && !dispatch.requests.some(r => r.caseId === item.caseId)) {
      actions.append(button('Zgłoś potrzebę pomocy', () => api('dispatch', 'commands/request', {
        commandId: commandId(), caseId: item.caseId, pickupNode: item.node,
      }).then(() => 'Zapisano potrzebę pomocy.')));
    }
    const step = { Open: ['acknowledgement', 'Potwierdź odbiór'], Acknowledged: ['moving', 'Zgłoś przejście'], Moving: ['exit-confirmed', 'Potwierdź wyjście'] }[item.progress];
    if (step) actions.append(button(step[1], () => api('management', 'commands/progress', {
      commandId: commandId(), caseId: item.caseId, step: step[0], expectedVersion: item.version,
    }).then(() => 'Zapisano status mieszkańca.')));
    if (actions.childElementCount) row.append(actions);
    return row;
  }));

  const available = dispatch.units.filter(unit => unit.state === 'Available');
  text('unit-status', 'Dostępne jednostki: ' + available.length + ' z ' + dispatch.units.length);
  $('requests').replaceChildren(...dispatch.requests.map(item => {
    const row = element('li');
    const info = element('div');
    const assignment = dispatch.assignments.find(a => a.requestId === item.requestId && a.state !== 'Cancelled');
    info.append(element('b', 'Pomoc · ' + item.caseId), element('p',
      (statuses[assignment?.state] || assignment?.state || 'Oczekuje na jednostkę') + ' · ' + label(item.pickupNode)));
    row.append(info);
    const actions = element('div', null, 'buttons');
    const change = (op, fields, message) => dispatchAction(item.requestId,
      () => api('dispatch', 'commands/' + op, { commandId: commandId(), ...fields }), message);
    const problem = dispatchErrors.get(item.requestId);
    if (problem) info.append(element('p', problem, 'dispatch-problem'));
    if (!assignment && available.length) {
      const select = element('select');
      select.setAttribute('aria-label', 'Jednostka dla ' + item.caseId);
      const result = dispatchRankings.get(item.requestId);
      const ranking = result?.requestVersion === item.version ? result.candidates : [];
      const candidates = new Map(ranking.map(candidate => [candidate.unitId, candidate]));
      const order = new Map(ranking.map((candidate, index) => [candidate.unitId, index]));
      const ranked = [...available].sort((a, b) => (order.get(a.unitId) ?? order.size) -
        (order.get(b.unitId) ?? order.size));
      for (const unit of ranked) {
        const candidate = candidates.get(unit.unitId);
        let estimate;
        if (candidate?.node === unit.node && candidate.status !== 'PositionUnverified') {
          estimate = candidate.status === 'Eligible' ? 'dojazd ' + minutes(candidate.approachSeconds)
            : dispatchMessages[candidate.status];
        }
        const option = element('option', [unit.name, unitPositionLabel(unit), estimate].filter(Boolean).join(' · '));
        option.value = unit.unitId;
        select.append(option);
      }
      const saved = dispatchSelections.get(item.requestId);
      if (available.some(unit => unit.unitId === saved)) select.value = saved;
      select.addEventListener('change', () => dispatchSelections.set(item.requestId, select.value));
      actions.append(button('Porównaj czasy dojazdu', () => dispatchAction(item.requestId, async () => {
        dispatchRankings.delete(item.requestId);
        const ranked = await api('dispatch', 'queries/unit-candidates?requestId=' + encodeURIComponent(item.requestId));
        if (ranked.situationRevision !== state.situation?.revision)
          throw Object.assign(new Error(dispatchMessages.SituationChanged), { code: 'SituationChanged' });
        dispatchSelections.delete(item.requestId);
        dispatchRankings.set(item.requestId, ranked);
      })), select, button('Przydziel', () => {
        const unit = available.find(unit => unit.unitId === select.value);
        return withConfirmedUnitPosition(unit, confirmed => change('assign', {
          requestId: item.requestId, unitId: unit.unitId, expectedVersion: item.version, ...confirmed,
        }, 'Zarezerwowano jednostkę.'));
      }));
    } else if (!assignment) {
      info.append(element('p', 'Wszystkie jednostki są zajęte.'));
    }
    if (assignment?.route) {
      const unit = dispatch.units.find(unit => unit.unitId === assignment.unitId);
      if (unit) info.append(element('p', unit.name + ' · ' + unitPositionLabel(unit)));
      const route = assignment.route;
      const active = !['Completed', 'Cancelled'].includes(assignment.state);
      const routeText = assignment.routeCurrent === true
        ? label(route.exit) + ' · dojazd ' + minutes(route.approachSeconds) + ' · wyjazd ' + minutes(route.outboundSeconds)
        : active ? dispatchRouteLabel(assignment.routeStatus) : null;
      if (routeText) info.append(element('p', routeText));
      if (route.provenance) info.append(element('p', route.provenance.mode === 'synthetic'
        ? 'Trasa jednostki · symulacja' : 'Trasa jednostki · OpenRouteService'
          + (route.provenance.mode === 'recorded' ? ' · zapis' : '')));
      const changeAssignment = (op, extra) => change(op, {
        assignmentId: assignment.assignmentId, expectedVersion: assignment.version, ...extra,
      }, 'Zapisano stan zadania.');
      if (active && unit && assignment.routeCurrent !== true &&
        !['SituationUnavailable', 'RoutingUnavailable', 'SituationRevisionUnavailable'].includes(assignment.routeStatus?.code))
        actions.append(button('Przelicz trasę jednostki', () =>
          withConfirmedUnitPosition(unit, confirmed => changeAssignment('replan', confirmed))));
      const step = { Assigned: ['acknowledge', 'Potwierdź zadanie'], Acknowledged: ['arrive', 'Zgłoś przyjazd'],
        AtPickup: ['pickup', 'Potwierdź odbiór'], PickupConfirmed: ['complete', 'Zakończ zadanie'] }[assignment.state];
      if (step && assignment.routeCurrent === true)
        actions.append(button(step[1], () => changeAssignment('progress', { step: step[0] })));
    }
    const history = dispatch.assignments.filter(entry => entry.requestId === item.requestId)
      .flatMap(entry => (entry.history ?? []).map(decision => ({ ...decision, unitId: entry.unitId })));
    if (history.length) info.append(dispatchHistory(item.requestId, history));
    if (actions.childElementCount) row.append(actions);
    return row;
  }));
}

const dispatchMessages = {
  NoEligibleRoute: 'Brak dostępnego objazdu. Sprawdź zamknięcia lub wybierz inną jednostkę.',
  FixtureMissing: 'Brakuje nagrania ORS dla tego przebiegu.',
  PositionUnverified: 'Potwierdź punkt startu jednostki.',
  SituationUnavailable: 'Brak połączenia z serwisem sytuacji. Trasa oczekuje na weryfikację.',
  SituationRevisionUnavailable: 'Brakuje aktualnej rewizji sytuacji.',
  RoutingUnavailable: 'Serwis tras jest niedostępny.',
  RouteEvidenceUnavailable: 'Brakuje danych do ponownej weryfikacji trasy.',
  RouteRequiresReview: 'Warunki przejazdu się zmieniły. Przelicz trasę jednostki.',
  SituationChanged: 'Sytuacja zmieniła się podczas sprawdzania trasy. Spróbuj ponownie.',
  UnitUnavailable: 'Ta jednostka została już przydzielona.',
  BookingConflict: 'Jednostka lub zgłoszenie ma już aktywną rezerwację.',
};

function dispatchRouteLabel(status) {
  const roads = status?.segments?.map(label).join(', ');
  if (status?.state === 'Blocked') {
    const closedAt = status.observedAt ? ' Zamknięcie: ' + new Date(status.observedAt).toLocaleString('pl-PL') + '.' : '';
    return 'Trasa wycofana. Zamknięte odcinki: ' + roads + '.' + closedAt;
  }
  if (status?.state === 'ReviewRequired') return 'Dostępność odcinka się zmieniła. Przelicz trasę: ' + roads + '.';
  return dispatchMessages[status?.code] ?? 'Trasa jednostki oczekuje na weryfikację.';
}

const dispatchHistoryOpen = new Set();
function dispatchHistory(requestId, history) {
  const details = element('details');
  details.open = dispatchHistoryOpen.has(requestId);
  details.addEventListener('toggle', () => {
    if (details.open) dispatchHistoryOpen.add(requestId); else dispatchHistoryOpen.delete(requestId);
  });
  const labels = { Assigned: 'Przydział', PositionConfirmed: 'Ręczne potwierdzenie pozycji',
    RouteWithdrawn: 'Wycofanie trasy', Replanned: 'Przeliczenie trasy' };
  const list = element('ul');
  for (const item of [...history].reverse()) {
    const reason = item.segments?.map(label).join(', ');
    list.append(element('li', [new Date(item.at).toLocaleString('pl-PL'), item.unitId,
      labels[item.kind] ?? statuses[item.kind] ?? item.kind, 'rewizja ' + item.revision, reason].filter(Boolean).join(' · ')));
  }
  details.append(element('summary', 'Historia zadania'), list);
  return details;
}

async function dispatchAction(requestId, action, message) {
  try {
    await action();
    dispatchErrors.delete(requestId);
    return message;
  } catch (error) {
    const reason = dispatchMessages[error.code] ?? error.message;
    dispatchErrors.set(requestId, reason);
    return reason;
  } finally {
    operationsSignature = null;
  }
}

function unitPositionLabel(unit) {
  const evidence = unit.positionEvidence;
  if (evidence?.mode === 'manual') return 'pozycja potwierdzona ręcznie';
  if (evidence?.mode === 'synthetic' && evidence.egnosApplied === true && unit.positionVerified)
    return 'EGNOS (symulacja) · HPL ' + evidence.hpl + ' m';
  if (evidence?.mode === 'synthetic' && evidence.hpl != null)
    return 'HPL ' + evidence.hpl + ' m (symulacja) · potwierdź pozycję';
  return 'pozycja wymaga potwierdzenia';
}

function withConfirmedUnitPosition(unit, action) {
  if (unit.positionVerified) return action({});
  if (window.confirm('Potwierdź pozycję jednostki: ' + label(unit.node) + '.'))
    return action({ confirmedStartNode: unit.node });
}

export function initOperations() {
  subscribe((vm, info) => {
    // Valencia has no cases or unit tasks; a resident signed in with Keycloak may not read them (INV-14).
    if (vm.scenario === 'valencia' || !vm.incident || !vm.dispatch) return;
    if (info.reason === 'refresh') {
      if (info.revisionChanged) dispatchRankings.clear();
      const operations = JSON.stringify([vm.incident, vm.dispatch, [...dispatchRankings]]);
      if (operations !== operationsSignature) {
        renderOperations(vm.incident, vm.dispatch);
        operationsSignature = operations;
      }
    } else if (info.reason === 'invalidate' && vm.incident && vm.dispatch) {
      const assignments = vm.dispatch.assignments.map(assignment => assignment.active
        ? { ...assignment, routeCurrent: null,
          routeStatus: { state: 'Unverified', code: 'SituationUnavailable' } }
        : assignment);
      renderOperations(vm.incident, { ...vm.dispatch, assignments });
      operationsSignature = null;
    }
  });
}
