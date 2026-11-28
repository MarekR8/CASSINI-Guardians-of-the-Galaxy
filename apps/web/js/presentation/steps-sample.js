// Presentation steps for the synthetic `sample` exercise (Kłodzko). Mirrors tests/offline_integration.py
// and is used by the end-to-end test in CI.
import { $ } from '../dom.js';
import { label, statuses } from '../format.js';
import { state, viewModel } from '../state.js';
import {
  ewssStep, hazardBounds, hazardLines, linkStep, mergeBounds, pointBounds, populationLine, resetStep, revision,
} from './common.js';

const memory = { drawId: null, assignmentId: null, requestId: null };

function nodeBounds(ids) {
  const nodes = viewModel().network?.nodes ?? [];
  return mergeBounds(...ids.map(id => nodes.find(node => node.id === id))
    .filter(Boolean).map(node => pointBounds([node.longitude, node.latitude], .0006)));
}

function segmentBounds(ids) {
  const segments = viewModel().network?.segments ?? [];
  return nodeBounds(segments.filter(segment => ids.includes(segment.id)).flatMap(segment => [segment.from, segment.to]));
}

function setNode(node) {
  const select = $('node');
  if (![...select.options].some(option => option.value === node)) select.add(new Option(label(node), node));
  select.value = node;
}

const percent = weight => Math.round(weight * 100) + '%';
const optionsLine = distribution => distribution.options.length
  ? distribution.options.map(option => label(option.exit) + ' ' + percent(option.weight)).join(' · ')
  : 'brak dostępnej trasy';

async function draw(ctx, key, node) {
  setNode(node);
  const distribution = await ctx.read('routing', 'distribution?node=' + encodeURIComponent(node));
  if (!distribution.options.length && !ctx.has(key)) return { distribution, result: null };
  const result = await ctx.command(key, 'routing', 'draw', async () => {
    const fresh = await ctx.read('routing', 'distribution?node=' + encodeURIComponent(node));
    if (!fresh.options.length) throw Object.assign(new Error('Brak dostępnej trasy z węzła.'), { code: 'NoEligibleRoute' });
    return { node, revision: fresh.revision, at: fresh.at, seed: 1 };
  });
  memory.drawId = ctx.commandIdOf(key);
  state.draw = { id: memory.drawId, state: 'Active', ...result };
  state.pendingDraw = null;
  state.subject = null;
  return { distribution, result };
}

async function currentAssignment(ctx) {
  const view = await ctx.read('dispatch', 'assignments');
  const assignment = view.assignments.find(item => item.assignmentId === memory.assignmentId)
    ?? view.assignments.find(item => item.requestId === memory.requestId && item.state !== 'Cancelled');
  if (!assignment) throw new Error('Brak przydziału jednostki. Wykonaj najpierw krok „Prośba o transport”.');
  return { view, assignment, unit: view.units.find(unit => unit.unitId === assignment.unitId) };
}

const confirmedStart = unit => unit && !unit.positionVerified ? { confirmedStartNode: unit.node } : {};

function assignmentFocus() {
  const assignment = viewModel().dispatch?.assignments.find(item => item.assignmentId === memory.assignmentId);
  const route = assignment?.route;
  if (!route) return null;
  return segmentBounds([...route.approachSegments, ...route.outboundSegments]) ?? nodeBounds([assignment.lastKnownNode]);
}

export function sampleSteps() {
  return [
    resetStep('sample'),
    {
      id: 'alert',
      title: 'Zgłoszenie powodzi → obraz sytuacji',
      summary: 'Zgłoszenie zalania trafia do Management. Mapa pokazuje warstwę zalania z provenance i szacunek ludności.',
      async run(ctx) {
        await ctx.command('alert', 'management', 'alert', async () => ({}));
        const [incident, snapshot] = await Promise.all([ctx.read('management', 'incident'), ctx.read('situation', 'snapshot')]);
        return {
          message: 'Zapisano zgłoszenie zalania. Liczba zgłoszeń: ' + incident.alerts.length + '.',
          details: [...hazardLines(snapshot), populationLine(snapshot)].filter(Boolean),
          focus: () => hazardBounds(viewModel().snapshot),
        };
      },
    },
    {
      id: 'draw',
      title: 'Rozkład tras w węźle i losowanie',
      summary: 'Pieszy w punkcie zbiórki dostaje rozkład dopuszczalnych tras, a węzeł losuje trasę (seed 1, powtarzalnie).',
      async run(ctx) {
        const { distribution, result } = await draw(ctx, 'draw', 'walk-start');
        if (!result) return { message: 'Brak dostępnej trasy z punktu zbiórki.', details: [optionsLine(distribution)] };
        return {
          message: 'Wylosowano: ' + label(result.route.exit) + ' (rewizja ' + distribution.revision + ', seed 1).',
          details: ['Rozkład: ' + optionsLine(distribution)],
          focus: () => segmentBounds(result.route.segments),
        };
      },
    },
    {
      id: 'assist',
      title: 'Prośba o transport → przydział jednostki',
      summary: 'Osoba bez samochodu (sprawa case-15) prosi o transport. Dyspozytor rezerwuje jednostkę z trasą dojazdu i wyjazdu.',
      async run(ctx) {
        const request = await ctx.command('request', 'dispatch', 'request', async () => ({
          caseId: 'case-15', pickupNode: (await ctx.read('management', 'cases/case-15')).node,
        }));
        memory.requestId = request.requestId;
        const details = ['Zgłoszenie pomocy: ' + request.requestId + ' · ' + label(request.pickupNode)];
        const view = await ctx.read('dispatch', 'assignments');
        const existing = view.assignments.find(item => item.requestId === request.requestId && item.state !== 'Cancelled');
        if (existing && !ctx.has('assign')) {
          memory.assignmentId = existing.assignmentId;
          return { message: 'Zgłoszenie ma już przydzieloną jednostkę ' + existing.unitId + '.', details, focus: assignmentFocus };
        }
        let confirmation = null;
        const assignment = await ctx.command('assign', 'dispatch', 'assign', async () => {
          const fresh = await ctx.read('dispatch', 'assignments');
          const item = fresh.requests.find(entry => entry.requestId === request.requestId);
          const available = fresh.units.filter(unit => unit.state === 'Available');
          const unit = available.find(entry => entry.unitId === 'unit-3') ?? available[0];
          if (!unit) throw new Error('Wszystkie jednostki są zajęte.');
          confirmation = confirmedStart(unit);
          return { requestId: item.requestId, unitId: unit.unitId, expectedVersion: item.version, ...confirmation };
        });
        memory.assignmentId = assignment.assignmentId;
        if (confirmation?.confirmedStartNode)
          details.push('Pozycja jednostki bez weryfikacji (PositionUnverified) — operator potwierdził punkt startu: ' +
            label(confirmation.confirmedStartNode) + '.');
        const route = assignment.route;
        details.push('Dojazd ' + Math.round(route.approachSeconds / 60) + ' min · wyjazd ' +
          Math.round(route.outboundSeconds / 60) + ' min · ' + label(route.exit));
        return { message: 'Zarezerwowano ' + assignment.unitId + ' dla ' + assignment.caseId + '.', details, focus: assignmentFocus };
      },
    },
    {
      id: 'closure',
      title: 'Zamknięcie mostu → wycofanie tras',
      summary: 'Operator zamyka most (w scenariuszu sample zamiast detekcji FlyPix). Trasy przez most zostają wycofane.',
      async run(ctx) {
        const snapshot = await ctx.command('bridge', 'situation', 'closure', async () => ({
          segmentId: 'bridge-2', closed: true, expectedRevision: await revision(),
        }));
        const details = ['Zamknięte odcinki (rewizja ' + snapshot.revision + '): ' + snapshot.closedSegments.join(', ')];
        if (memory.drawId) {
          const status = await ctx.read('routing', 'draws/' + memory.drawId);
          details.push('Wylosowana trasa: ' + (status.state === 'Withdrawn' ? 'wycofana' : status.state));
        }
        if (memory.assignmentId) {
          const { assignment } = await currentAssignment(ctx);
          details.push('Trasa jednostki: ' + (assignment.routeCurrent === false ? 'wymaga przeliczenia'
            : assignment.routeCurrent ? 'aktualna' : 'oczekuje na weryfikację'));
        }
        return { message: 'Most zamknięty. Trasy przez most wycofano.', details, focus: () => segmentBounds(['bridge-2']) };
      },
    },
    {
      id: 'replan',
      title: 'Ponowne planowanie: jednostka i pieszy',
      summary: 'Dyspozytor przelicza trasę jednostki, a węzeł losuje nową trasę z aktualnego rozkładu.',
      async run(ctx) {
        const details = [];
        const { assignment } = await currentAssignment(ctx);
        if (assignment.routeCurrent !== true || ctx.has('unit')) {
          const replanned = await ctx.command('unit', 'dispatch', 'replan', async () => {
            const fresh = await currentAssignment(ctx);
            return { assignmentId: fresh.assignment.assignmentId, expectedVersion: fresh.assignment.version,
              ...confirmedStart(fresh.unit) };
          });
          details.push('Jednostka: nowa trasa do ' + label(replanned.route.exit));
        } else details.push('Jednostka: trasa nadal aktualna.');
        const { distribution, result } = await draw(ctx, 'redraw', 'walk-start');
        details.push('Rozkład: ' + optionsLine(distribution));
        return {
          tone: result ? null : 'warning',
          message: result ? 'Nowa trasa pieszego: ' + label(result.route.exit) + '.' : 'Brak trasy dla pieszego (osobny stan od wycofania).',
          details,
          focus: () => result ? segmentBounds(result.route.segments) : nodeBounds(['walk-start']),
        };
      },
    },
    {
      id: 'unit-progress',
      title: 'Jednostka: potwierdzenie, przyjazd, odbiór, zakończenie',
      summary: 'Jednostka potwierdza zadanie, przyjazd, odbiór osoby i zakończenie. Zakończenie nie jest potwierdzeniem wyjścia (INV-12).',
      async run(ctx) {
        const order = [['Assigned', 'acknowledge'], ['Acknowledged', 'arrive'], ['AtPickup', 'pickup'], ['PickupConfirmed', 'complete']];
        let { assignment } = await currentAssignment(ctx);
        const position = order.findIndex(([status]) => status === assignment.state);
        for (const [index, [, step]] of order.entries()) {
          if ((position < 0 || index < position) && !ctx.has(step)) continue;
          assignment = await ctx.command(step, 'dispatch', 'progress', async () => {
            const fresh = await currentAssignment(ctx);
            return { assignmentId: fresh.assignment.assignmentId, step, expectedVersion: fresh.assignment.version };
          });
        }
        const kase = await ctx.read('management', 'cases/' + assignment.caseId);
        return {
          message: 'Zadanie: ' + (statuses[assignment.state] ?? assignment.state) + '.',
          details: ['Sprawa ' + kase.caseId + ': ' + (statuses[kase.progress] ?? kase.progress) + ' (bez potwierdzenia wyjścia)'],
          focus: assignmentFocus,
        };
      },
    },
    // A field flood report sent while the link is down: queued by the Gateway, delivered after the switch.
    linkStep({ name: 'field-alert', service: 'management', operation: 'alert', build: async () => ({}) }),
    ewssStep(),
    {
      id: 'exit',
      title: 'Potwierdzenie wyjścia ze strefy → licznik',
      summary: 'Mieszkaniec case-14 potwierdza odbiór polecenia, przejście i wyjście. Licznik liczy tylko jawne potwierdzenia. ' +
        'Pozycja z Galileo/OSNMA na telefonie to task 5; tutaj zapis ćwiczenia (dane syntetyczne).',
      async run(ctx) {
        const order = [['Open', 'acknowledgement'], ['Acknowledged', 'moving'], ['Moving', 'exit-confirmed']];
        let kase = await ctx.read('management', 'cases/case-14');
        const position = order.findIndex(([status]) => status === kase.progress);
        for (const [index, [, step]] of order.entries()) {
          if ((position < 0 || index < position) && !ctx.has(step)) continue;
          kase = await ctx.command(step, 'management', 'progress', async () => ({
            caseId: 'case-14', step, expectedVersion: (await ctx.read('management', 'cases/case-14')).version,
          }));
        }
        const incident = await ctx.read('management', 'incident');
        const confirmed = incident.cases.filter(item => item.progress === 'ExitConfirmed').length;
        return {
          message: 'Potwierdzone wyjścia ze strefy: ' + confirmed + ' z ' + incident.cases.length + '.',
          details: ['case-14: ' + (statuses[kase.progress] ?? kase.progress)],
          focus: () => nodeBounds([kase.node]),
        };
      },
    },
    {
      id: 'no-exit',
      title: 'Zamknięcie północnego dojścia → brak wyjścia',
      summary: 'Drugie zamknięcie odcina ostatnie wyjście. Brak trasy jest pokazany osobno od trasy wycofanej.',
      async run(ctx) {
        const snapshot = await ctx.command('north', 'situation', 'closure', async () => ({
          segmentId: 'north-link', closed: true, expectedRevision: await revision(),
        }));
        setNode('pickup');
        const distribution = await ctx.read('routing', 'distribution?node=pickup');
        return {
          tone: distribution.options.length ? null : 'warning',
          message: distribution.options.length ? 'Nadal dostępne: ' + optionsLine(distribution) + '.'
            : 'Brak wyjścia: z węzła przy odbiorze nie ma dopuszczalnej trasy.',
          details: ['Zamknięte odcinki (rewizja ' + snapshot.revision + '): ' + snapshot.closedSegments.join(', ')],
          focus: () => segmentBounds(['north-link', 'bridge-2']),
        };
      },
    },
  ];
}
