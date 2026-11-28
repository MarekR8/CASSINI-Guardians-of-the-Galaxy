const names = {
  'walk-start': 'Punkt zbiórki', pickup: 'Węzeł przy odbiorze',
  'east-junction': 'Przed mostem', 'exit-east': 'Wyjście wschodnie',
  'north-junction': 'Dojście północne', 'exit-north': 'Wyjście północne',
};
export const statuses = {
  Open: 'Otwarta sprawa', Acknowledged: 'Potwierdzono odbiór', Moving: 'W drodze',
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
