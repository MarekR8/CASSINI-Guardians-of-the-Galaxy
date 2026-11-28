// Dashboard bootstrap: wires the panels, the Leaflet map and the extension modules.
import { notice } from './js/dom.js';
import { initMap } from './js/map.js';
import { initCity } from './js/panels/city.js';
import { syncControls } from './js/panels/controls.js';
import { initDistribution } from './js/panels/distribution.js';
import { initHeader } from './js/panels/header.js';
import { initIntegrations } from './js/panels/integrations.js';
import { initOperations } from './js/panels/operations.js';
import { initReviews } from './js/panels/reviews.js';
import { initScenario } from './js/panels/scenario.js';
import { initSheltering } from './js/panels/sheltering.js';
import { initSites } from './js/panels/sites.js';
import { run, setHooks, startPolling, state } from './js/state.js';

setHooks({
  notice,
  syncControls,
  defaultMessage: () => state.network?.scenario === 'valencia'
    ? 'Zalanie z października 2024 r. · archiwalna obserwacja Copernicus EMS'
    : 'Scenariusz gotowy. Wybierz węzeł i wylosuj trasę.',
});

// Subscription order matches the former render order: heading, distribution, map, map tools, panels.
initHeader();
initDistribution();
initMap();
initCity();
initOperations();
initReviews();
initScenario();
initSites();
initSheltering();
initIntegrations();

// Extension point (S2 layers, S3 metrics, S4 presentation). Loaded after the core panels subscribe.
await import('./js/extensions.js');

await run(() => {});
startPolling();
