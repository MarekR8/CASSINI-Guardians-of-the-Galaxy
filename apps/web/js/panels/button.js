import { element } from '../dom.js';
import { run } from '../state.js';

// A button that runs its handler as one serialized operation followed by a refresh.
export function button(value, handler) {
  const node = element('button', value);
  node.type = 'button';
  node.addEventListener('click', () => run(handler));
  return node;
}
