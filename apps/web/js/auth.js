// S8: optional Keycloak sign-in (compose profile `full`). When /auth-config.json reports enabled:false
// (the default offline stack) this module does nothing and #auth-bar stays hidden.
// The Gateway enforces the roles; the per-role view in css/auth.css only hides controls a role cannot use.
import { setAuthHeaderProvider } from './api.js';
import { $, element } from './dom.js';
import { run, setReadableServices } from './state.js';

// Highest role first: the view follows the strongest role, the Gateway allows the union.
const roles = ['coordinator', 'dispatcher', 'responder', 'resident'];
const roleLabels = { coordinator: 'Koordynator', dispatcher: 'Dyspozytor', responder: 'Ratownik', resident: 'Mieszkaniec' };

const config = await fetch('/auth-config.json', { cache: 'no-store' })
  .then(response => response.ok ? response.json() : null)
  .catch(() => null);
if (config?.enabled) await startAuth(config);

async function startAuth({ url, realm, clientId }) {
  const bar = $('auth-bar');
  bar.hidden = false;
  document.body.classList.add('auth-enabled');
  bar.replaceChildren(element('span', 'Logowanie…', 'auth-status'));
  // Until sign-in completes requests carry no token: the Gateway answers 401 and nothing is confirmed.
  setAuthHeaderProvider(() => null);

  let keycloak;
  try {
    const { default: Keycloak } = await import('../vendor/keycloak-js/keycloak.js');
    keycloak = new Keycloak({ url, realm, clientId });
    // login-required redirects to Keycloak and back; the code flow uses PKCE S256, no session iframe.
    await keycloak.init({ onLoad: 'login-required', pkceMethod: 'S256', checkLoginIframe: false });
  } catch (error) {
    bar.replaceChildren(element('span', 'Brak połączenia z usługą logowania. Komendy są zablokowane.', 'auth-status auth-error'));
    document.body.classList.add('role-none');
    return;
  }

  setAuthHeaderProvider(async () => {
    try {
      await keycloak.updateToken(30);
    } catch {
      keycloak.login();
      return null;
    }
    return keycloak.token ? 'Bearer ' + keycloak.token : null;
  });
  // Refresh ahead of expiry even when no request is pending.
  keycloak.onTokenExpired = () => keycloak.updateToken(30).catch(() => keycloak.login());

  const granted = keycloak.realmAccess?.roles ?? [];
  const role = roles.find(name => granted.includes(name));
  document.body.classList.add('role-' + (role ?? 'none'));
  // Residents read only the shared situation picture and routes (Gateway rule, INV-14); skip the other reads
  // so the panel does not mark them as stale.
  if (role === 'resident') {
    setReadableServices(['situation', 'routing']);
    run(async () => null);
  }

  const user = element('span', keycloak.tokenParsed?.preferred_username ?? 'użytkownik', 'auth-user');
  const badge = element('span', role ? roleLabels[role] : 'Brak roli', 'auth-role' + (role ? '' : ' auth-error'));
  const logout = element('button', 'Wyloguj', 'auth-logout');
  logout.type = 'button';
  logout.addEventListener('click', () => keycloak.logout({ redirectUri: location.origin + '/' }));
  bar.replaceChildren(user, badge, logout);
  if (role && role !== 'coordinator') tagOperationControls();
}

// Operation rows mix commands of different services in one .buttons group; tag them so css/auth.css
// can hide the ones the role cannot send. Untagged controls stay visible and the Gateway answers 403.
const caseProgress = new Set(['Potwierdź odbiór', 'Zgłoś przejście', 'Potwierdź wyjście']);
const unitProgress = new Set(['Potwierdź zadanie', 'Zgłoś przyjazd', 'Potwierdź odbiór', 'Zakończ zadanie']);

function tagOperationControls() {
  const cases = $('cases');
  const requests = $('requests');
  const tag = () => {
    for (const control of cases?.querySelectorAll('.buttons > button') ?? []) {
      control.classList.toggle('auth-op-management-progress', caseProgress.has(control.textContent));
      control.classList.toggle('auth-op-dispatch-request', control.textContent === 'Zgłoś potrzebę pomocy');
    }
    for (const control of requests?.querySelectorAll('.buttons > button, .buttons > select') ?? []) {
      const progress = control.tagName === 'BUTTON' && unitProgress.has(control.textContent);
      control.classList.toggle('auth-op-dispatch-progress', progress);
      control.classList.toggle('auth-op-dispatch-plan', !progress);
    }
  };
  for (const list of [cases, requests])
    if (list) new MutationObserver(tag).observe(list, { childList: true, subtree: true });
  tag();
}
