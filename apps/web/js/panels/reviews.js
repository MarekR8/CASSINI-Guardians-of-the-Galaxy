// FlyPix / synthetic detections awaiting a coordinator decision.
import { api, commandId } from '../api.js';
import { $, element } from '../dom.js';
import { segmentLabel } from '../format.js';
import { fitTo, geometryBounds } from '../map.js';
import { state, subscribe } from '../state.js';
import { button } from './button.js';

let reviewSignature = null;

function renderReviews(reviews) {
  const list = $('reviews');
  list.replaceChildren();
  if (!reviews.length) list.append(element('li', 'Brak detekcji dla scenariusza.'));
  for (const feature of reviews) {
    const item = element('li');
    const details = element('div');
    const confidence = feature.confidence == null ? '' : ' · ' + Math.round(feature.confidence * 100) + '% pewności';
    const status = { Pending: 'Do weryfikacji', Approved: 'Zatwierdzona', Rejected: 'Odrzucona' }[feature.status];
    const provenance = feature.mode === 'synthetic' ? 'Symulacja'
      : feature.mode === 'manual' ? 'FlyPix · adnotacja ręczna' : 'FlyPix AI · ' + (feature.mode === 'live' ? 'API' : 'zapis');
    const corridors = feature.segmentIds.filter(id => !id.startsWith('osm-')).map(segmentLabel);
    const roads = feature.segmentIds.length - corridors.length;
    if (roads) corridors.push(roads + ' odc. OSM');
    details.append(element('b', corridors.join(' · ') || 'Brak dopasowania do dróg'),
      element('p', status + ' · ' + provenance + confidence),
      element('p', feature.imageProductId + ' · ' + feature.observedAt));
    item.append(details);
    if (feature.status === 'Pending') {
      const decide = decision => api('situation', 'commands/review-decision', {
        commandId: commandId(), expectedRevision: state.situation.revision, reviewFeatureId: feature.id, decision,
      }).then(() => decision === 'approve' ? 'Zatwierdzono zamknięcie wykrytych odcinków.' : 'Odrzucono detekcję.');
      const controls = element('div', null, 'buttons');
      if (feature.segmentIds.length) controls.append(button('Zatwierdź zamknięcie', () => decide('approve')));
      controls.append(button('Odrzuć', () => decide('reject')));
      if (state.network.scenario === 'valencia') {
        const focus = element('button', 'Pokaż na mapie');
        focus.type = 'button';
        focus.addEventListener('click', () => {
          fitTo(geometryBounds(feature.geometry));
          $('map').scrollIntoView({ block: 'nearest' });
        });
        controls.append(focus);
      }
      if (state.network.scenario === 'valencia' && feature.segmentIds.length) {
        const segments = element('details');
        segments.append(element('summary', 'Dopasowane odcinki OSM'),
          element('p', feature.segmentIds.join(' · ')));
        item.append(segments);
      }
      item.append(controls);
    }
    list.append(item);
  }
}

export function initReviews() {
  subscribe((vm, info) => {
    if (info.reason !== 'refresh') return;
    const reviews = vm.snapshot.reviewFeatures || [];
    const signature = JSON.stringify(reviews);
    if (signature !== reviewSignature) {
      renderReviews(reviews);
      reviewSignature = signature;
    }
  });
}
