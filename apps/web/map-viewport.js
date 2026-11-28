const ZOOM_STEP = 1.25;
const DRAG_THRESHOLD = 4;
const clamp = (value, min, max) => Math.max(min, Math.min(value, max));

export function createMapViewport(map, { zoomIn, zoomOut, reset, maxZoom }) {
  const { width, height } = map.viewBox.baseVal;
  const pointers = new Map();
  let zoom = 1;
  let x = 0;
  let y = 0;
  let previousGesture = null;
  let moved = false;

  function setView(nextX, nextY, nextZoom) {
    zoom = clamp(nextZoom, 1, maxZoom());
    const visibleWidth = width / zoom;
    const visibleHeight = height / zoom;
    x = clamp(nextX, 0, width - visibleWidth);
    y = clamp(nextY, 0, height - visibleHeight);
    map.setAttribute('viewBox', [x, y, visibleWidth, visibleHeight].join(' '));
    zoomIn.disabled = zoom === maxZoom();
    zoomOut.disabled = reset.disabled = zoom === 1;
    map.style.setProperty('--map-symbol-scale', 1 / zoom);
    map.dispatchEvent(new Event('map-view-change'));
  }

  function point(clientX, clientY) {
    return new DOMPoint(clientX, clientY).matrixTransform(map.getScreenCTM().inverse());
  }

  function moveViewport(from, to, factor = 1) {
    const anchor = point(from.x, from.y);
    const target = point(to.x, to.y);
    const nextZoom = clamp(zoom * factor, 1, maxZoom());
    const ratio = zoom / nextZoom;
    setView(anchor.x - (target.x - x) * ratio, anchor.y - (target.y - y) * ratio, nextZoom);
  }

  function zoomBy(factor) {
    const rect = map.getBoundingClientRect();
    const center = new DOMPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    moveViewport(center, center, factor);
  }

  function resetView() {
    setView(0, 0, 1);
  }

  function gesture() {
    const [first, second] = pointers.values();
    if (!second) return { center: first, distance: 1 };
    return {
      center: new DOMPoint((first.x + second.x) / 2, (first.y + second.y) / 2),
      distance: Math.max(1, Math.hypot(first.x - second.x, first.y - second.y)),
    };
  }

  zoomIn.addEventListener('click', () => zoomBy(ZOOM_STEP));
  zoomOut.addEventListener('click', () => zoomBy(1 / ZOOM_STEP));
  reset.addEventListener('click', resetView);
  map.addEventListener('wheel', event => {
    if (!event.deltaY) return;
    event.preventDefault();
    const cursor = new DOMPoint(event.clientX, event.clientY);
    moveViewport(cursor, cursor, event.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP);
  }, { passive: false });

  map.addEventListener('pointerdown', event => {
    if (event.button !== 0 || pointers.size >= 2) return;
    if (!pointers.size) moved = false;
    pointers.set(event.pointerId, new DOMPoint(event.clientX, event.clientY));
    if (pointers.size === 2) moved = true;
    previousGesture = gesture();
    map.setPointerCapture(event.pointerId);
  });
  map.addEventListener('pointermove', event => {
    if (!pointers.has(event.pointerId)) return;
    pointers.set(event.pointerId, new DOMPoint(event.clientX, event.clientY));
    const next = gesture();
    const { center, distance } = previousGesture;
    if (!moved && Math.hypot(next.center.x - center.x, next.center.y - center.y) < DRAG_THRESHOLD) return;
    moved = true;
    map.classList.add('dragging');
    moveViewport(center, next.center, next.distance / distance);
    previousGesture = next;
  });
  function endPointer(event) {
    if (!pointers.delete(event.pointerId)) return;
    previousGesture = pointers.size ? gesture() : null;
    if (!pointers.size) map.classList.remove('dragging');
  }
  for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) {
    map.addEventListener(name, endPointer);
  }
  map.addEventListener('click', event => {
    if (moved) event.stopImmediatePropagation();
    moved = false;
  }, { capture: true });

  const directions = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
  map.addEventListener('keydown', event => {
    const direction = directions[event.key];
    if (direction) setView(x + direction[0] * width / zoom / 10, y + direction[1] * height / zoom / 10, zoom);
    else if (event.key === '+' || event.key === '=') zoomBy(ZOOM_STEP);
    else if (event.key === '-') zoomBy(1 / ZOOM_STEP);
    else if (event.key === 'Home') resetView();
    else return;
    event.preventDefault();
  });

  resetView();
  return { point };
}
