// Extension point for the task 7 slices. Each slice edits only its own module and stylesheet.
import './layers/index.js';   // S2: map layers and legend (css/layers.css)
import './metrics.js';        // S3: metrics panel #metrics-panel (css/metrics.css)
import './presentation.js';   // S4: presentation mode #presentation-panel (css/presentation.css)
import './auth.js';           // S8: optional Keycloak sign-in #auth-bar (css/auth.css)
