const reasons = {
  SiteClosed: 'zamknięty', SiteDraft: 'w przygotowaniu', SiteSuspended: 'zawieszony',
  OutsideOperatingWindow: 'poza godzinami działania', UnverifiedEvidence: 'brak weryfikacji',
  EvidenceOutsideValidity: 'weryfikacja poza okresem ważności', StaleSituationEvidence: 'wymaga weryfikacji',
  SituationUnavailable: 'brak bieżących danych', Overcommitted: 'przekroczono limity',
  NoAvailablePlaces: 'brak miejsc',
};

export function createSiteCatalog(panel, api) {
  const status = panel.querySelector('p');
  const items = panel.querySelector('ul');
  let context = null;
  let enabled = false;
  let sequence = 0;

  fetch('/features').then(response => response.ok ? response.json() : null).then(features => {
    enabled = features?.sheltering === true;
    refresh();
  }).catch(() => {});
  panel.addEventListener('toggle', refresh);

  async function refresh() {
    const current = ++sequence;
    panel.hidden = !enabled || !context;
    if (panel.hidden || !panel.open) return;
    const at = encodeURIComponent(context.at);
    const incident = encodeURIComponent(context.incidentId);
    try {
      const [points, shelters] = await Promise.all([
        api('situation', 'queries/assembly-points?incidentId=' + incident + '&at=' + at),
        api('sheltering', 'queries/shelter-availability?at=' + at),
      ]);
      if (current !== sequence) return;
      if (shelters.situationRevision !== points.situationRevision) throw new Error('Situation changed');
      status.textContent = 'Dane ćwiczeniowe · rewizja sytuacji ' + points.situationRevision;
      items.replaceChildren(
        ...points.assemblyPoints.map(site => row(site, 'Punkt zbiórki')),
        ...shelters.shelters.map(site => row(site,
          `Wolne: ${site.available.places}, w tym dostępne: ${site.available.accessiblePlaces} · Zajęte: ${site.commitments.occupiedPlaces}`)),
      );
    } catch {
      if (current !== sequence) return;
      status.textContent = 'Nie udało się potwierdzić bieżącej dostępności obiektów.';
      items.replaceChildren();
    }
  }

  function row(site, detail) {
    const item = document.createElement('li');
    const name = document.createElement('strong');
    const evidence = document.createElement('span');
    name.textContent = site.name;
    evidence.textContent = detail + ' · ' + (site.eligible ? 'kandydat' : reasons[site.ineligibleReason] || 'niedostępny');
    item.append(name, evidence);
    return item;
  }

  return { update(next) { context = next; refresh(); } };
}
