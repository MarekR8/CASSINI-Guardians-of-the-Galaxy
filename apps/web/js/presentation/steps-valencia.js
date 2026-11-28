// Presentation steps for Valencia, the demo city. Routing serves the city route and the task 03 evacuation model
// (evacuation-plan, distribution, draw). Dispatch and Management have no Valencia data, so those steps stay blocked.
// The shelters of the evacuation model are synthetic (data/demo/routing/valencia/shelters.synthetic.json).
import { $ } from '../dom.js';
import { geometryBounds } from '../map.js';
import { notify, requestCityPath, routeGeometry, state, viewModel } from '../state.js';
import {
  blocked, ewssStep, hazardLines, lineBounds, linkStep, mergeBounds, populationLine, resetStep, revision,
} from './common.js';

const FIXTURE = '/data/presentation/valencia-review.synthetic.json';
const memory = { baseline: null, features: {} };
let fixture = null;

async function loadFixture() {
  if (fixture) return fixture;
  const response = await fetch(FIXTURE, { cache: 'no-store' });
  if (!response.ok) throw new Error('Brak lokalnego pliku detekcji syntetycznej (' + FIXTURE + ').');
  fixture = await response.json();
  return fixture;
}

// Uses the city panel state, so the map draws the route and refresh() re-checks it after each new revision.
async function cityRoute() {
  const { route } = await loadFixture();
  state.cityPicking = false;
  state.cityPoints = [route.from, route.to];
  $('city-mode').value = route.mode;
  const path = await requestCityPath();
  state.cityPath = path;
  return path;
}

// Called after run() refreshed the view: lets the city panel show the route status and the map redraw it.
function showCityPath() {
  notify('redraw', { cityPathUpdated: true });
}

const minutes = seconds => Math.floor(seconds / 60) + ' min ' + String(Math.round(seconds % 60)).padStart(2, '0') + ' s';
const routeFocus = path => () => mergeBounds(lineBounds(path?.route?.geometry), ...memory.baseline
  ? [lineBounds(memory.baseline.route?.geometry)] : []);

async function importDetection(ctx, key) {
  const detection = (await loadFixture()).detections[key];
  const snapshot = await ctx.command('import', 'situation', 'import-flypix', async () => ({
    expectedRevision: await revision(), layerId: detection.layerId, imageProductId: detection.imageProductId,
    observedAt: detection.observedAt, mode: 'synthetic', geoJson: detection.geoJson,
  }));
  const feature = (snapshot.reviewFeatures ?? []).find(item => item.layerId === detection.layerId);
  if (!feature) throw new Error('Usługa sytuacji nie zwróciła zaimportowanej detekcji.');
  memory.features[key] = feature;
  return { snapshot, feature, detection };
}

async function approveDetection(ctx, key) {
  const detection = (await loadFixture()).detections[key];
  const current = await ctx.read('situation', 'snapshot');
  const feature = (current.reviewFeatures ?? []).find(item => item.layerId === detection.layerId) ?? memory.features[key];
  if (!feature) throw new Error('Najpierw zaimportuj detekcję (poprzedni krok).');
  if (feature.status !== 'Pending' && !ctx.has('approve'))
    return { snapshot: current, feature, decided: true };
  const snapshot = await ctx.command('approve', 'situation', 'review-decision', async () => ({
    expectedRevision: await revision(), reviewFeatureId: feature.id, decision: 'approve',
  }));
  return { snapshot, feature, decided: false };
}

const memoryDraw = { id: null };
const percent = weight => Math.round(weight * 100) + '%';

// The plan is recomputed in the background after every revision; until then Routing answers 503 PlanCalculating.
async function readPlanned(ctx, path, timeout = 60_000) {
  const until = Date.now() + timeout;
  for (;;) {
    try {
      return await ctx.read('routing', path);
    } catch (error) {
      if (error.code !== 'PlanCalculating' || Date.now() > until) throw error;
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
  }
}

// Routing returns the drawn route as OSM node IDs; look up their positions for the map.
async function nodeLine(ctx, ids) {
  const points = [];
  for (let i = 0; i < ids.length; i += 20) {
    const nodes = await Promise.all(ids.slice(i, i + 20).map(id => ctx.read('routing', 'city-node/' + id)));
    points.push(...nodes.map(node => [node.longitude, node.latitude]));
  }
  return points;
}

async function evacuationDraw(ctx) {
  const { distribution: config } = await loadFixture();
  const query = 'distribution?node=' + config.node + '&profile=' + config.profile;
  const distribution = await readPlanned(ctx, query);
  const plan = await readPlanned(ctx, 'evacuation-plan');
  if (distribution.status !== 'Ok' && !ctx.has('draw')) return { distribution, plan, result: null, config };
  const result = await ctx.command('draw', 'routing', 'draw', async () => {
    const fresh = await readPlanned(ctx, query);
    return { node: config.node, mode: config.profile, revision: fresh.revision, seed: config.seed };
  });
  memoryDraw.id = ctx.commandIdOf('draw');
  routeGeometry.set(memoryDraw.id, await nodeLine(ctx, result.route.nodes));
  state.draw = { id: memoryDraw.id, state: 'Active', ...result };
  state.pendingDraw = null;
  return { distribution, plan, result, config };
}

// State of the evacuation draw after a closure, as Routing reports it (draws/{commandId}).
function drawLine() {
  const draw = viewModel().draw;
  if (!draw || draw.id !== memoryDraw.id) return null;
  return 'Wylosowana trasa ewakuacji: ' + ({ Active: 'nadal aktywna', Withdrawn: 'wycofana po zamknięciu',
    Expired: 'wygasła', Unknown: 'oczekuje na potwierdzenie' }[draw.state] ?? draw.state);
}

const reviewLine = feature => 'SYMULACJA · ' + feature.imageProductId + ' · ' + feature.observedAt + ' · ' +
  feature.segmentIds.length + ' odc. OSM';

export function valenciaSteps() {
  return [
    resetStep('valencia'),
    {
      id: 'situation',
      title: 'Obraz sytuacji: EMSR773, Sentinel-1, strefy, ludność',
      summary: 'Archiwalna obserwacja Copernicus EMS (EMSR773, październik 2024), radar Sentinel-1 przed i po zdarzeniu, ' +
        'strefy węzłów sieci i szacunek ludności.',
      async run(ctx) {
        const snapshot = await ctx.read('situation', 'snapshot');
        const { zoneCounts } = viewModel().network.city;
        const count = value => (value ?? 0).toLocaleString('pl-PL');
        return {
          message: 'Obraz sytuacji z rewizji ' + snapshot.revision + '. Radar: 31.10.2024 po zdarzeniu.',
          details: [...hazardLines(snapshot), populationLine(snapshot),
            'Węzły: ' + count(zoneCounts.hazard) + ' w strefie zagrożenia · ' + count(zoneCounts.safe) + ' poza zalaniem']
            .filter(Boolean),
          focus: () => {
            $('valencia-image').value = 'event';
            $('valencia-image').dispatchEvent(new Event('change'));
            if (!$('valencia-nodes').checked) {
              $('valencia-nodes').checked = true;
              $('valencia-nodes').dispatchEvent(new Event('change'));
            }
            return viewModel().cityImagery?.scenes.event?.bounds ?? null;
          },
        };
      },
    },
    blocked('alert', 'Zgłoszenie powodzi od mieszkańca',
      'Zgłoszenie zalania zapisane w Management.',
      'Oczekuje na task 6 — brak obsługi Walencji w usłudze Management (zgłoszenia i sprawy mieszkańców).'),
    {
      id: 'city-route',
      title: 'Trasa piesza w mieście (OSM)',
      summary: 'Trasa testowa pieszo między dwoma punktami w bieżącej rewizji sytuacji (zapytanie city-route).',
      async run() {
        const path = await cityRoute();
        memory.baseline = path;
        return {
          message: path.route ? 'Trasa piesza: ' + minutes(path.route.seconds) + ', ' + path.route.segments.length +
            ' odcinków (rewizja ' + path.revision + ').' : 'Brak trasy pieszej między punktami.',
          after: showCityPath,
          focus: routeFocus(path),
        };
      },
    },
    {
      id: 'distribution',
      title: 'Rozkład tras w węźle i losowanie (model task 03)',
      summary: 'Węzeł w strefie zalania dostaje rozkład tras do schronów (logit-sue, schrony syntetyczne) ' +
        'i losuje trasę z widocznej rewizji. Ludność to szacunek GHSL, nie liczba osób.',
      async run(ctx) {
        const { distribution, plan, result, config } = await evacuationDraw(ctx);
        const names = new Map(distribution.options.map(option => [option.exit, option.name]));
        const details = [
          'Węzeł OSM ' + distribution.node + ' · profil ' + distribution.profile + ' · fala ' + distribution.wave +
            ' · termin opuszczenia strefy ' + Math.round(distribution.deadlineMinutes) + ' min (założenie modelu)',
          ...distribution.options.map(option => option.name + ' (SYMULACJA) · ' + percent(option.weight) + ' · ' +
            minutes(option.seconds) + ' · obciążenie ' + Math.round(option.expectedLoad).toLocaleString('pl-PL') +
            ' / ' + option.capacity.toLocaleString('pl-PL')),
          'Plan ' + plan.model + ' · rewizja ' + plan.revision + ' · szacunek ' +
            Math.round(plan.people.estimated).toLocaleString('pl-PL') + ' os. w obserwowanym zalaniu',
        ];
        if (!result) return { tone: 'warning', details,
          message: 'Brak trasy z węzła: ' + distribution.status + ' (rewizja ' + distribution.revision + ').' };
        return {
          message: 'Wylosowano: ' + (names.get(result.route.exit) ?? result.route.exit) + ' · ' +
            minutes(result.route.seconds) + ' (rewizja ' + result.revision + ', seed ' + config.seed + ').',
          details,
          after: () => notify('redraw'),
          focus: () => lineBounds(routeGeometry.get(memoryDraw.id)),
        };
      },
    },
    blocked('transport', 'Prośba o transport → jednostka',
      'Osoba bez samochodu prosi o transport, dyspozytor przydziela jednostkę z dojazdem i wyjazdem.',
      'Oczekuje na task 4 — brak obsługi Walencji w usłudze Dispatch (zgłoszenia pomocy i jednostki).'),
    {
      id: 'flypix',
      title: 'Detekcja FlyPix: zalane przejście (SYMULACJA)',
      summary: 'Import syntetycznej detekcji z pliku apps/web/data/presentation (oznaczona jako symulacja). ' +
        'Usługa sytuacji dopasowuje ją do odcinków OSM; detekcja czeka na decyzję operatora.',
      async run(ctx) {
        const { snapshot, feature } = await importDetection(ctx, 'crossing');
        const onRoute = memory.baseline?.route
          ? feature.segmentIds.filter(id => memory.baseline.route.segments.includes(id)).length : null;
        return {
          message: 'Detekcja do weryfikacji (' + (feature.status === 'Pending' ? 'oczekuje na decyzję' : feature.status) +
            '), rewizja ' + snapshot.revision + '.',
          details: [reviewLine(feature), onRoute == null ? null : 'Odcinki na trasie pieszej: ' + onRoute].filter(Boolean),
          focus: () => geometryBounds(feature.geometry),
        };
      },
    },
    {
      id: 'approve',
      title: 'Zatwierdzenie → zamknięcie odcinków',
      summary: 'Operator zatwierdza detekcję. Dopasowane odcinki stają się zamknięciem z dowodem (exercise-verified).',
      async run(ctx) {
        const { snapshot, feature, decided } = await approveDetection(ctx, 'crossing');
        const roads = await ctx.read('situation', 'closed-roads');
        return {
          message: (decided ? 'Detekcja miała już decyzję. ' : 'Zatwierdzono zamknięcie. ') + 'Zamknięte drogi: ' +
            roads.roads.length + ' (rewizja ' + roads.revision + ').',
          details: [reviewLine(feature), 'Rewizja po decyzji: ' + snapshot.revision],
          focus: () => mergeBounds(...roads.roads.map(road => lineBounds(road.geometry))),
        };
      },
    },
    {
      id: 'detour',
      title: 'Przeliczenie trasy → objazd',
      summary: 'Trasa piesza jest liczona ponownie dla nowej rewizji. Pokazujemy objazd albo brak trasy jako osobny stan.',
      async run() {
        const path = await cityRoute();
        const closed = new Set(viewModel().snapshot?.closedSegments ?? []);
        const route = path.route;
        const changed = route && memory.baseline?.route &&
          route.segments.join() !== memory.baseline.route.segments.join();
        return {
          tone: route ? null : 'warning',
          message: !route ? 'Brak trasy: zamknięcia odcinają cel.'
            : !memory.baseline?.route ? 'Trasa: ' + minutes(route.seconds) + ' (brak trasy bazowej do porównania).'
            : changed ? 'Objazd: ' + minutes(route.seconds) + ' (wcześniej ' + minutes(memory.baseline.route.seconds) + ').'
              : 'Trasa bez zmian — zamknięcie jej nie dotyczy.',
          details: [...(route ? ['Odcinki zamknięte na nowej trasie: ' + route.segments.filter(id => closed.has(id)).length]
            : []), 'Rewizja: ' + path.revision, drawLine()].filter(Boolean),
          after: showCityPath,
          focus: routeFocus(path),
        };
      },
    },
    {
      id: 'no-route',
      title: 'Odcięcie punktu startowego → brak trasy',
      summary: 'Druga syntetyczna detekcja obejmuje okolicę punktu startowego. Po zatwierdzeniu trasa nie istnieje — ' +
        'stan „brak trasy” różni się od trasy wycofanej i od braku danych.',
      async run(ctx) {
        const imported = await importDetection(ctx, 'isolation');
        await approveDetection(ctx, 'isolation');
        const path = await cityRoute();
        return {
          message: path.route ? 'Trasa nadal istnieje: ' + minutes(path.route.seconds) + '.'
            : 'Brak trasy: punkt startowy jest odcięty przez zatwierdzone zamknięcia.',
          tone: path.route ? null : 'warning',
          details: [reviewLine(imported.feature), 'Rewizja: ' + path.revision],
          after: showCityPath,
          focus: () => geometryBounds(imported.feature.geometry),
        };
      },
    },
    linkStep(),
    ewssStep(),
    blocked('exit', 'Potwierdzenie wyjścia ze strefy → licznik',
      'Telefon z Galileo potwierdza wyjście ze strefy; licznik „potwierdzono poza strefą”.',
      'Oczekuje na task 6 — brak obsługi Walencji w usłudze Management (sprawy mieszkańców); telefon z Galileo: task 5.'),
  ];
}
