export const $ = id => document.getElementById(id);

export const text = (id, value) => { $(id).textContent = value; };

export function element(tag, value, className) {
  const node = document.createElement(tag);
  if (value) node.textContent = value;
  if (className) node.className = className;
  return node;
}

export function notice(value, error = false) {
  text('notice', value);
  $('notice').classList.toggle('error', error);
}
