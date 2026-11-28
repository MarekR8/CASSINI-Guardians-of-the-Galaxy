// Scenario heading, panel visibility, credits, revision and data sources.
import { $, text } from '../dom.js';
import { formatObservation, time } from '../format.js';
import { subscribe } from '../state.js';

let sourcesSignature = null;
let lastSourceRevision = null;

function renderSources(hazards) {
  const imported = hazards.filter(h => h.productId);
  text('sources', imported.length
    ? imported.map(h => (h.source.includes('sentinel-1') ? 'Sentinel-1' : 'Copernicus EMS') +
      ' · ' + h.productId + ' · ' + h.observedAt +
      (h.provenance?.mode === 'recorded' ? ' · Dane zapisane' : '')).join(' | ')
    : 'Zasięg zalania pochodzi ze scenariusza ćwiczeniowego.');
}

function render(vm, info) {
  if (info.reason !== 'refresh') return;
  const { network, snapshot: situation, incident, distribution } = vm;
  const valencia = vm.scenario === 'valencia';
  document.body.classList.toggle('valencia', valencia);
  for (const id of ['exercise-controls', 'exercise-routing', 'exercise-operations', 'scenario-time'])
    $(id).hidden = valencia;
  $('valencia-tools').hidden = !valencia;
  if (valencia) {
    const { safe = 0, hazard = 0, unknown = 0 } = network.city.zoneCounts;
    const count = value => value.toLocaleString('pl-PL');
    text('valencia-zone-counts', count(hazard) + ' w strefie zagrożenia · ' + count(safe) +
      ' poza wykrytym zalaniem · ' + count(unknown) + ' bez oceny');
    $('map').setAttribute('aria-label', 'Mapa zalania i dróg w obszarze zdjęć Sentinel-1.');
  }
  const scene = vm.satellite;
  text('incident', valencia ? 'DEMO · POWÓDŹ · WALENCJA'
    : 'DEMO · POWÓDŹ' + (incident ? ' · ' + incident.incidentId : ''));
  text('map-area', valencia ? 'Walencja · obszar Sentinel-1' : 'Kłodzko · obszar ćwiczeń');
  $('map-credit').hidden = !valencia;
  $('city-tools').hidden = !network.city;
  $('city-routing').hidden = !network.city;
  if (scene) {
    $('satellite-source').href = scene.sourceUrl;
    text('satellite-source', 'Contains modified Copernicus Sentinel data ' + scene.observedAt.slice(0, 4));
    text('satellite-date', ' · Sentinel-2 L2A · obserwacja ' +
      formatObservation.format(new Date(scene.observedAt)) + ' UTC');
    if (scene.detail) $('ortho-source').href = scene.detail.sourceUrl;
  }
  const archive = scene?.floodArchive;
  $('archive-credit').hidden = !archive;
  $('archive-tools').hidden = !archive;
  if (archive) {
    $('archive-source').href = archive.sourceUrl;
    text('archive-source', '© European Union, Copernicus EMS · ' + (archive.label ? archive.label + ' · ' : '') + archive.productId);
    text('archive-date', ' · obserwacja ' + formatObservation.format(new Date(archive.observedAt)) + ' UTC' +
      (archive.resolutionMeters ? ' · ' + archive.resolutionMeters + ' m' : ''));
  }
  $('review-panel').hidden = !valencia;
  text('revision', 'Rewizja ' + situation.revision);
  if (!valencia) text('clock', time(distribution.at) + ' UTC');
  const hazards = situation.hazards || [];
  const sources = JSON.stringify(hazards);
  if (sources !== sourcesSignature || lastSourceRevision !== situation.revision) {
    renderSources(hazards);
    sourcesSignature = sources;
    lastSourceRevision = situation.revision;
  }
  const estimate = situation.populationEstimate;
  $('population-estimate').hidden = !estimate;
  if (estimate) {
    const zone = hazards.some(h => h.productId) ? 'strefie' : 'strefie ćwiczeniowej';
    text('population-estimate',
      'Szacunek ludności w ' + zone + ' · ' + new Intl.NumberFormat('pl-PL').format(estimate.value) +
      ' osób · ' + estimate.source + ' ' + estimate.year);
  }
}

export function initHeader() {
  subscribe(render);
}
