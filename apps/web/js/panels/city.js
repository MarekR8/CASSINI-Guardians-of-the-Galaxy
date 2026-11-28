// City route picking, Valencia radar and zone tools, archive focus and the Sentinel-2 / orthophoto switch.
import { $, text } from '../dom.js';
import { minutes } from '../format.js';
import { fitTo, geometryBounds, onMapClick, redraw, resetView } from '../map.js';
import { requestCityPath, run, state, subscribe } from '../state.js';

let valenciaViewInitialized = false;

function showCityPath(path) {
  state.cityPath = path;
  const found = path.route;
  const title = state.network?.scenario === 'valencia' ? 'Trasa testowa OSM · ' : 'Trasa orientacyjna · ';
  text('city-status', found ? title + minutes(found.seconds) : 'Brak trasy w wybranym trybie.');
}

function fitValenciaRadar() {
  const radar = state.cityImagery?.scenes[$('valencia-image').value];
  if (radar) fitTo(radar.bounds);
  else resetView();
}

function renderRadarCredit() {
  const selected = $('valencia-image').value;
  const radar = state.network?.scenario === 'valencia' ? state.cityImagery?.scenes[selected] : null;
  $('radar-credit').hidden = !radar;
  if (radar) text('radar-credit', 'Contains modified Copernicus Sentinel data ' + radar.date.slice(0, 4) +
    ' · Sentinel-1 VV · obserwacja ' + radar.date);
}

function resetPicking(status) {
  state.cityPicking = false;
  state.cityPoints = [];
  state.cityPath = null;
  $('city-pick').setAttribute('aria-pressed', 'false');
  text('city-status', status);
}

export function initCity() {
  subscribe((vm, info) => {
    if (info.cityPathUpdated) showCityPath(state.cityPath);
    if (state.cityPath && !vm.readAvailable) text('city-status', 'Trasa oczekuje na ponowną weryfikację.');
    if (info.reason !== 'refresh') return;
    renderRadarCredit();
    if (vm.scenario === 'valencia' && info.restored && !valenciaViewInitialized) {
      fitValenciaRadar();
      valenciaViewInitialized = true;
    }
  });
  $('valencia-nodes').addEventListener('change', () => redraw());
  $('valencia-image').addEventListener('change', () => {
    redraw();
    renderRadarCredit();
    fitValenciaRadar();
  });
  $('archive-focus').addEventListener('click', () => {
    fitTo(geometryBounds(state.satelliteScene?.floodArchive?.geometry));
  });
  $('map-layer').addEventListener('click', () => {
    state.satelliteDetail = !state.satelliteDetail;
    redraw();
  });
  $('city-pick').addEventListener('click', () => {
    const picking = !state.cityPicking;
    resetPicking(picking ? 'Wskaż punkt startowy na mapie.' : '');
    state.cityPicking = picking;
    $('city-pick').setAttribute('aria-pressed', String(picking));
    redraw();
  });
  $('city-mode').addEventListener('change', () => {
    resetPicking('Wskaż punkty trasy dla wybranego trybu.');
    redraw();
  });
  onMapClick(({ lon, lat }) => {
    if (!state.cityPicking || state.busy || !state.readAvailable) return;
    const [w, s, e, n] = state.network.city.bounds;
    if (lon < w || lon > e || lat < s || lat > n) return;
    state.cityPoints.push([lon, lat]);
    redraw();
    if (state.cityPoints.length === 1) {
      text('city-status', 'Wskaż cel na mapie.');
      return;
    }
    state.cityPicking = false;
    $('city-pick').setAttribute('aria-pressed', 'false');
    run(async () => {
      const path = await requestCityPath();
      if (state.cityPoints.length === 2) showCityPath(path);
      return 'Sprawdzono dojazd w bieżącej rewizji.';
    });
  });
}
