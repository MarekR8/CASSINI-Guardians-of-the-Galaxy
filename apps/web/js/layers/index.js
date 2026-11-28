// S2: map layers, one module per family, each registered with registerLayer() from ../map.js through common.js.
// Drawing order comes from the panes; registration order only sets the legend order within a group.
import './imagery.js';
import './network.js';
import './archive.js';
import './hazards.js';
import './review.js';
import './closures.js';
import './routes.js';
import './dispatch.js';
import './units.js';
import './nodes.js';
import './labels.js';
import './status.js';
import './legend.js';
