const names = {
  'walk-start': 'Punkt zbiórki', pickup: 'Węzeł przy odbiorze',
  'east-junction': 'Przed mostem', 'exit-east': 'Wyjście wschodnie',
  'north-junction': 'Dojście północne', 'exit-north': 'Wyjście północne',
};
export const statuses = {
  Open: 'Otwarta sprawa', Acknowledged: 'Potwierdzono odbiór', Moving: 'W drodze', PickedUp: 'Osoba odebrana',
  ExitConfirmed: 'Wyjście potwierdzone', Assigned: 'Jednostka przydzielona',
  AtPickup: 'Na miejscu odbioru', PickupConfirmed: 'Odbiór osoby potwierdzony',
  Completed: 'Zakończono zadanie', Cancelled: 'Anulowano', Available: 'Gotowa', Booked: 'Zajęta',
};
const formatTime = new Intl.DateTimeFormat('pl-PL', {
  timeZone: 'UTC', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
});
export const formatObservation = new Intl.DateTimeFormat('pl-PL', {
  timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'short',
});
export const time = value => formatTime.format(new Date(value));
export const minutes = seconds => Math.round(seconds / 60) + ' min';
export const label = id => names[id] || id;
export const segmentLabel = id => ({
  'bridge-2': 'Most Świętokrzyski', 'north-link': 'Dojście północne',
  'walk-to-pickup': 'Dojście do punktu przesiadkowego', 'east-link': 'Dojazd do mostu',
  'north-exit-link': 'Dojście do wyjścia', 'outside-connector': 'Objazd dla jednostki',
})[id] || id;

// Management counts a case as outside the zone only with an explicit exit confirmation that carries a position fix
// (EvacuationCase.confirmedOutside, INV-12). Older answers without the field fall back to the progress state.
export const confirmedOutside = item => item.confirmedOutside ?? item.progress === 'ExitConfirmed';

// The position mode reported with the fix; the server never upgrades it (contracts/management).
export const fixModes = { measured: 'pomiar odbiornika', simulated: 'SYMULACJA', replay: 'ODTWORZENIE zapisu' };

export function exitLine(item) {
  const fix = item.exit;
  if (!fix) return null;
  const evidence = fix.evidence ?? {};
  return 'Wyjście: pozycja ' + (fixModes[fix.mode] ?? fix.mode) + ' · ±' + Math.round(fix.accuracy) + ' m · ' +
    time(fix.observedAt) + ' UTC · Galileo: ' + (evidence.galileoUsed ? 'tak' : 'nie') +
    ' · OSNMA: ' + (evidence.osnma ?? '—') + ' · EGNOS: ' + (evidence.egnos ?? '—');
}
