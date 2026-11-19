import { initBridge, call, on } from './bridge.js';
import { toast, errorText } from './components/dom.js';
import { renderDictations } from './views/dictations.js';
import { renderDictionary } from './views/dictionary.js';
import { renderSettings, applyTheme } from './views/settings.js';
import { renderPhase3 } from './views/phase3.js';
import { initSetup, openSetup } from './views/setup.js';

type View = 'dictations' | 'meetings' | 'files' | 'dictionary' | 'settings';

const viewEl = document.getElementById('view') as HTMLElement;
let teardown: (() => void) | null = null;
let current: View = 'dictations';

const STATE_TEXT: Record<string, string> = {
  ready: 'Bereit', recording: 'Aufnahme läuft', paused: 'Pausiert', processing: 'Verarbeite …', error: 'Fehler', done: 'Fertig',
};

function navigate(view: View): void {
  teardown?.();
  current = view;
  document.querySelectorAll<HTMLButtonElement>('.nav-item').forEach(b => {
    const active = b.dataset.view === view;
    b.classList.toggle('active', active);
    if (active) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
  });
  teardown = view === 'dictations' ? renderDictations(viewEl)
    : view === 'dictionary' ? renderDictionary(viewEl)
    : view === 'settings' ? renderSettings(viewEl)
    : renderPhase3(viewEl, view);
  viewEl.focus();
}

async function start(): Promise<void> {
  try {
    initBridge();
  } catch (e) {
    viewEl.textContent = errorText(e);
    return;
  }
  document.querySelectorAll<HTMLButtonElement>('.nav-item').forEach(b =>
    b.addEventListener('click', () => navigate(b.dataset.view as View)));

  // Pfeiltasten in der Navigation
  document.querySelector('.nav')!.addEventListener('keydown', (e: Event) => {
    const ke = e as KeyboardEvent;
    if (ke.key !== 'ArrowDown' && ke.key !== 'ArrowUp') return;
    const items = [...document.querySelectorAll<HTMLButtonElement>('.nav-item')];
    const i = items.indexOf(document.activeElement as HTMLButtonElement);
    const next = items[(i + (ke.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length];
    next.focus();
    ke.preventDefault();
  });

  const statusLine = document.getElementById('statusLine')!;
  on('status', (s: any) => {
    statusLine.textContent = s.message ?? STATE_TEXT[s.state] ?? s.state;
    statusLine.dataset.state = s.state;
    if (s.state === 'error' && s.message) toast(`${s.message}${s.errorId ? ` (${s.errorId})` : ''}`, 'error', 6000);
  });
  on('settings', (s: any) => {
    applyTheme(s.theme);
    document.getElementById('hotkeyHint')!.textContent = `${s.hotkey.displayName} halten und sprechen`;
  });
  on('navigate', (d: any) => navigate(d.view));

  try {
    const [info, settings] = await Promise.all([call<any>('app.info'), call<any>('settings.get')]);
    applyTheme(settings.theme);
    document.getElementById('hotkeyHint')!.textContent = `${info.hotkey} halten und sprechen`;
    statusLine.textContent = STATE_TEXT[info.state] ?? 'Bereit';
    initSetup(() => navigate('dictations'));
    navigate(current);
    if (!info.setupCompleted) openSetup(1);
  } catch (e) {
    toast(errorText(e), 'error');
  }
}

start();
