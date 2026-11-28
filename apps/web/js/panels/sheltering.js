// SH-T07: shelters, travel-party decisions, assignments and reception (opt-in sheltering overlay, sample scenario).
// Each fact comes from its owner and is shown separately: a verified exit (Management), a point check-in (no owner
// yet), a reservation (Sheltering allocation), a published route (SH-T06, not wired yet) and a confirmed reception
// (Sheltering reception). Nothing here infers one fact from another.
//
// Staleness: the panel keeps the last consistent read with its revisions. When a read fails, or the shelters were
// read against an older Situation revision, the panel says so and shows no instruction as current.
import { authorization, commandId } from '../api.js';
import { $, element } from '../dom.js';
import { subscribe } from '../state.js';

const assignmentLabels = {
  Pending: 'czeka na rezerwację', Reserved: 'zarezerwowano', AwaitingReplacement: 'wycofana · czeka na zastępstwo',
  Admitted: 'wykorzystana przy przyjęciu', Closed: 'zamknięta po wyjściu',
};
const planLabels = { Approved: 'zatwierdzony', ReviewRequired: 'wymaga przeglądu', Superseded: 'zastąpiony' };
const siteReasons = {
  SiteClosed: 'zamknięty', SiteDraft: 'w przygotowaniu', SiteSuspended: 'zawieszony',
  OutsideOperatingWindow: 'poza godzinami działania', UnverifiedEvidence: 'brak weryfikacji',
  EvidenceOutsideValidity: 'weryfikacja poza okresem ważności', StaleSituationEvidence: 'wymaga ponownej weryfikacji',
  SituationUnavailable: 'brak bieżących danych', Overcommitted: 'przekroczono limity', NoAvailablePlaces: 'brak miejsc',
};

export function initSheltering() {
  const panel = $('sheltering-panel');
  const status = $('sheltering-status');
  const badge = $('sheltering-revision');
  let enabled = false;
  let context = null;
  let token = null;          // exercise operator token: memory only, never stored
  let last = null;           // last consistent read
  let sequence = 0;
  let outcome = '';          // result of the last action; kept across refreshes until the next action

  fetch('/features').then(response => response.ok ? response.json() : null)
    .then(features => { enabled = features?.sheltering === true; refresh(); })
    .catch(() => {});

  $('sheltering-token').addEventListener('submit', event => {
    event.preventDefault();
    token = $('sheltering-token-input').value.trim() || null;
    outcome = '';
    $('sheltering-token-input').value = '';
    $('sheltering-token-state').textContent = token ? 'Token operatora aktywny' : 'Bez tokenu';
    refresh();
  });
  $('sheltering-token-clear').addEventListener('click', () => {
    token = null;
    $('sheltering-token-state').textContent = 'Bez tokenu';
    refresh();
  });

  subscribe((vm, info) => {
    if (info.reason === 'redraw') return;
    context = vm.scenario !== 'valencia' && vm.incident && vm.distribution
      ? { incidentId: vm.incident.incidentId, at: vm.distribution.at, cases: vm.incident.cases,
          situationRevision: vm.revision } : context;
    refresh();
  });

  async function call(service, path, data) {
    // An exercise operator token (Sheltering Stage B accounts) replaces the dashboard sign-in for these calls only.
    const signedIn = token ? 'Bearer ' + token : await authorization();
    const headers = { ...(signedIn ? { Authorization: signedIn } : {}),
      ...(data ? { 'Content-Type': 'application/json' } : {}) };
    const response = await fetch('/api/' + service + '/' + path,
      data ? { method: 'POST', headers, body: JSON.stringify(data) } : { headers });
    const body = await response.json().catch(() => null);
    if (!response.ok) throw Object.assign(new Error(body?.detail || body?.code || 'HTTP ' + response.status),
      { status: response.status, code: body?.code });
    return body;
  }

  async function optional(service, path) {
    try {
      return { ok: true, body: await call(service, path) };
    } catch (error) {
      return { ok: false, status: error.status, code: error.code };
    }
  }

  async function refresh() {
    const current = ++sequence;
    panel.hidden = !enabled || !context;
    if (panel.hidden) return;
    const at = encodeURIComponent(context.at);
    const incident = encodeURIComponent(context.incidentId);
    try {
      const [points, shelters, assignments] = await Promise.all([
        call('situation', 'queries/assembly-points?incidentId=' + incident + '&at=' + at),
        call('sheltering', 'queries/shelter-availability?at=' + at),
        call('sheltering', 'queries/shelter-assignments?incidentId=' + incident),
      ]);
      // Reception needs an operator token when the Stage B role boundary is on; a refusal is shown, not hidden.
      const reception = await optional('sheltering', 'queries/shelter-reception?incidentId=' + incident + '&at=' + at);
      const groupIds = [...new Set(assignments.assignments.map(item => item.groupPlanRef?.groupId).filter(Boolean))];
      const plans = await Promise.all(groupIds.map(async groupId => [groupId,
        await optional('management', 'queries/approved-travel-plans/' + encodeURIComponent(groupId) + '?incidentId=' + incident)]));
      if (current !== sequence) return;
      last = {
        observedAt: new Date(), situationRevision: shelters.situationRevision, assignmentRevision: assignments.revision,
        points: points.assemblyPoints, shelters: shelters.shelters, assignments: assignments.assignments,
        reception, plans: Object.fromEntries(plans), cases: context.cases ?? [],
        consistent: points.situationRevision === shelters.situationRevision,
      };
      render(false);
    } catch {
      if (current !== sequence) return;
      render(true);
    }
  }

  function render(failed) {
    // Stale when the last read failed, the two owners disagreed on the revision, or the dashboard has since seen a
    // newer Situation revision than the shelters were checked against.
    const stale = failed || !last?.consistent ||
      (last && context.situationRevision != null && last.situationRevision !== context.situationRevision);
    panel.classList.toggle('sheltering-stale', Boolean(stale));
    if (!last) {
      status.textContent = 'Nie udało się odczytać schronień. Brak potwierdzonych wskazań.';
      badge.textContent = 'Brak danych';
      for (const id of ['sheltering-sites', 'sheltering-reception', 'sheltering-groups', 'sheltering-assignments'])
        $(id).replaceChildren();
      return;
    }
    const time = last.observedAt.toLocaleTimeString('pl-PL');
    badge.textContent = `Rewizja sytuacji ${last.situationRevision ?? '—'} · przydziały ${last.assignmentRevision}`;
    status.textContent = (stale
      ? `Dane nieaktualne: ostatni spójny odczyt o ${time} (rewizja ${last.situationRevision ?? '—'}). Żadna wytyczna poniżej nie jest bieżąca.`
      : `Dane ćwiczeniowe · odczyt o ${time}.`) + (outcome ? ' ' + outcome : '');
    status.dataset.stale = stale ? 'true' : 'false';
    renderSites(stale);
    renderReception(stale);
    renderGroups();
    renderAssignments(stale);
  }

  function renderSites(stale) {
    $('sheltering-sites').replaceChildren(
      ...last.points.map(site => record(site.name, `Punkt zbiórki · ${site.eligible ? 'czynny' : siteReasons[site.ineligibleReason] || 'niedostępny'}`)),
      ...last.shelters.map(site => {
        const item = record(site.name,
          `${site.eligible ? 'przyjmuje' : siteReasons[site.ineligibleReason] || 'niedostępne'} · wolne ${site.available.places}` +
          ` (dostępne ${site.available.accessiblePlaces}) · zarezerwowane ${site.commitments.heldPlaces}` +
          ` · zajęte ${site.commitments.occupiedPlaces} z ${site.resources.places}`);
        item.dataset.shelterId = site.shelterId;
        if (!stale && site.status === 'open') item.append(action('Zamknij schronienie', () => closeShelter(site)));
        return item;
      }));
  }

  function renderReception(stale) {
    const list = $('sheltering-reception');
    if (!last.reception.ok) {
      list.replaceChildren(record('Przyjęcia', last.reception.status === 401
        ? 'Podaj token operatora schronienia albo koordynatora, aby zobaczyć przyjęcia.'
        : last.reception.status === 403 ? 'Ten token nie obejmuje tego incydentu.' : 'Brak odczytu przyjęć.'));
      return;
    }
    list.replaceChildren(...last.reception.body.shelters.map(site => {
      const counters = site.counters;
      const item = record(site.name,
        `oczekuje ${counters.awaitingAdmission.people} (spóźnieni ${counters.overdueArrivals}) · obecni ${counters.present.people}` +
        ` · przyjęcia ${counters.admissionsConfirmed} · wyjścia ${counters.departuresConfirmed}` +
        (counters.transferredAwaitingDeparture ? ` · przeniesieni bez potwierdzonego wyjścia ${counters.transferredAwaitingDeparture}` : ''));
      item.dataset.shelterId = site.shelterId;
      if (!stale) {
        for (const hold of site.awaiting)
          item.append(action(`Przyjmij ${hold.caseId}`, () => report('confirm-shelter-admission', site,
            { arrivals: [{ caseId: hold.caseId, legId: hold.legId }] })));
        for (const admission of site.admissions.filter(entry => !entry.departedAt))
          item.append(action(`Wyjście ${admission.caseId}`, () => report('confirm-shelter-departure', site,
            { departures: [{ caseId: admission.caseId, legId: admission.legId }] })));
      }
      return item;
    }));
  }

  function renderGroups() {
    const entries = Object.entries(last.plans);
    $('sheltering-groups').replaceChildren(...(entries.length ? entries.map(([groupId, plan]) => {
      if (!plan.ok) return record(groupId, 'Brak zatwierdzonego planu podróży.');
      const value = plan.body;
      const parties = value.proposal.parties.map(party =>
        `${party.caseIds.join(' + ')} → ${party.destinationId} (${party.startNode})`).join('; ');
      return record(groupId, `Plan ${value.planRevision}: ${planLabels[value.status] || value.status} · ` +
        `${value.proposal.status} · ${parties || 'brak wspólnego wyjazdu'}`);
    }) : [record('Grupy', 'Brak przydziałów powiązanych z planem grupy.')]));
  }

  function renderAssignments(stale) {
    const reception = last.reception.ok ? last.reception.body.shelters : [];
    const body = $('sheltering-assignments').querySelector('tbody');
    body.replaceChildren(...last.assignments.map(assignment => {
      const person = last.cases.find(item => item.caseId === assignment.caseId);
      const admission = reception.flatMap(site => site.admissions.map(entry => ({ ...entry, shelterId: site.shelterId })))
        .find(entry => entry.caseId === assignment.caseId && entry.legId === assignment.legId);
      const reservation = assignment.status === 'Reserved'
        ? `${stale ? 'ostatnio: ' : ''}zarezerwowano w ${assignment.reservation.shelterId}${stale ? ' (niepotwierdzone)' : ''}`
        : assignment.status === 'AwaitingReplacement'
          ? `wycofana z ${assignment.withdrawn?.shelterId ?? '—'} · czeka na zastępstwo`
          : assignmentLabels[assignment.status] || assignment.status;
      const row = element('tr');
      row.dataset.caseId = assignment.caseId;
      row.dataset.status = assignment.status;
      row.append(
        cell(assignment.caseId), cell(assignment.legId),
        cell(!person ? 'brak sprawy w Management' : person.progress === 'ExitConfirmed' ? 'potwierdzone' : 'niepotwierdzone', 'fact-exit'),
        cell('brak właściciela danych', 'fact-checkin'),
        cell(reservation, 'fact-reservation'),
        cell('nieopublikowana · SH-T06', 'fact-route'),
        cell(!last.reception.ok ? 'brak uprawnień' : admission
          ? `${admission.shelterId}${admission.departedAt ? ' · wyszedł' : ' · obecny'}` : 'nieprzyjęty', 'fact-reception'),
      );
      return row;
    }));
  }

  async function report(operation, site, people) {
    const label = operation === 'confirm-shelter-admission' ? 'Przyjęcie' : 'Wyjście';
    try {
      const reportId = 'report-' + commandId();
      await call('sheltering', 'commands/' + operation, {
        commandId: 'cmd-' + reportId, expectedVersion: site.version, shelterId: site.shelterId,
        incidentId: context.incidentId, reportId, observedAt: context.at, ...people,
      });
      outcome = `${label} zapisane w ${site.name}.`;
    } catch (error) {
      outcome = `${label} odrzucone: ${error.code || error.message}.`;
    }
    refresh();
  }

  async function closeShelter(site) {
    try {
      await call('sheltering', 'commands/configure-shelter', {
        commandId: commandId(), expectedVersion: site.version, shelterId: site.shelterId, name: site.name,
        place: site.place, stepFree: site.stepFree, status: 'closed', operatingWindow: site.operatingWindow,
        evidence: site.evidence, resources: site.resources,
      });
      outcome = `${site.name} zamknięte. Przydziały czekają na zastępstwo; obecni pozostają policzeni do wyjścia.`;
    } catch (error) {
      outcome = `Nie zamknięto ${site.name}: ${error.code || error.message}.`;
    }
    refresh();
  }

  function record(title, detail) {
    const item = element('li');
    const text = element('div');
    text.append(element('strong', title), element('p', detail));
    item.append(text);
    return item;
  }

  function action(label, handler) {
    const button = element('button', label);
    button.type = 'button';
    button.addEventListener('click', handler);
    return button;
  }

  function cell(text, className) {
    const value = element('td', text);
    if (className) value.className = className;
    return value;
  }
}
