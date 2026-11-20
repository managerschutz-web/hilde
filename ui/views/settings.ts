import { call, on } from '../bridge.js';
import { h, clear, toast, errorText, debounce, fmtBytes } from '../components/dom.js';
import { openSetup } from './setup.js';

export function applyTheme(theme: string): void {
  document.documentElement.dataset.theme = theme === 'light' || theme === 'dark' ? theme : 'system';
}

export function renderSettings(root: HTMLElement): () => void {
  clear(root);
  const offs: (() => void)[] = [];
  let s: any;

  const persist = debounce(async () => {
    try {
      s = await call('settings.save', s);
      applyTheme(s.theme);
      toast('Gespeichert', 'ok', 1200);
    } catch (e) { toast(errorText(e), 'error'); }
  }, 350);

  function select(label: string, value: string, options: [string, string][], set: (v: string) => void, help?: string) {
    const sel = h('select', { class: 'input', onchange: (e: Event) => { set((e.target as HTMLSelectElement).value); persist(); } },
      options.map(([v, l]) => h('option', { value: v, selected: v.toLowerCase() === String(value).toLowerCase() }, l)));
    return h('label', { class: 'field' }, h('span', null, label), sel, help ? h('small', { class: 'muted' }, help) : null);
  }

  function toggle(label: string, value: boolean, set: (v: boolean) => void, help?: string) {
    const cb = h('input', { type: 'checkbox', role: 'switch', checked: value, onchange: (e: Event) => { set((e.target as HTMLInputElement).checked); persist(); } });
    return h('label', { class: 'switch-row' }, h('span', null, h('span', null, label), help ? h('small', { class: 'muted block' }, help) : null), cb);
  }

  function number(label: string, value: number, min: number, max: number, step: number, unit: string, set: (v: number) => void) {
    const inp = h('input', { type: 'number', class: 'input narrow', value, min, max, step,
      onchange: (e: Event) => { set(Number((e.target as HTMLInputElement).value)); persist(); } });
    return h('label', { class: 'field inline' }, h('span', null, label), h('span', { class: 'row tight' }, inp, h('span', { class: 'muted' }, unit)));
  }

  async function draw(): Promise<void> {
    try {
      s = await call('settings.get');
      const [devices, info, models] = await Promise.all([
        call<any[]>('devices.list').catch(() => []),
        call<any>('app.info'),
        call<any>('models.status', { tier: s.performanceTier }),
      ]);
      clear(root);

      // Hotkey
      const hotkeyBtn = h('button', { class: 'btn', onclick: async () => {
        hotkeyBtn.textContent = 'Jetzt gewünschte Taste drücken …';
        try {
          const r = await call<any>('hotkey.capture', {}, 15000);
          if (r.ok) { s.hotkey.virtualKey = r.vk; s.hotkey.displayName = r.name; persist(); }
          else toast(r.message, 'error', 5000);
        } catch (e) { toast(errorText(e), 'error'); }
        hotkeyBtn.textContent = s.hotkey.displayName;
      } }, s.hotkey.displayName);

      // Mikrofon-Pegel
      const meter = h('div', { class: 'meter' }, h('div', { class: 'meter-fill' }));
      let monitoring = false;
      const monBtn = h('button', { class: 'btn', onclick: async () => {
        monitoring = !monitoring;
        try {
          await call(monitoring ? 'mic.monitorStart' : 'mic.monitorStop', { deviceId: s.microphoneId });
          monBtn.textContent = monitoring ? 'Pegeltest beenden' : 'Pegel testen';
        } catch (e) { monitoring = false; toast(errorText(e), 'error'); }
      } }, 'Pegel testen');
      offs.push(on('level', (l: number) => { (meter.firstChild as HTMLElement).style.width = `${Math.round(l * 100)}%`; }));
      offs.push(() => { if (monitoring) call('mic.monitorStop').catch(() => { }); });

      const devOptions: [string, string][] = [['', 'Windows-Standardmikrofon'], ...devices.map((d: any) => [d.id, d.name + (d.isDefault ? ' (Standard)' : '')] as [string, string])];
      const installed = models.models.filter((m: any) => m.installed).length;

      root.append(
        h('header', { class: 'view-head' }, h('div', null, h('h1', null, 'Einstellungen'), h('p', { class: 'muted' }, 'Änderungen werden sofort gespeichert.')),
          h('div', { class: 'row tight' },
            h('button', { class: 'btn', onclick: () => call('app.openLogs') }, 'Protokolle öffnen'),
            h('button', { class: 'btn', onclick: () => call('app.openData') }, 'Datenordner'))),
        h('div', { class: 'settings' },
          section('Diktat',
            h('div', { class: 'field inline' }, h('span', null, 'Diktiertaste'), hotkeyBtn),
            number('Mindestens halten', s.hotkey.minHoldMs, 100, 800, 25, 'ms', v => s.hotkey.minHoldMs = v),
            number('Doppeltipp-Fenster', s.hotkey.doubleTapMs, 200, 800, 25, 'ms', v => s.hotkey.doubleTapMs = v),
            toggle('Esc verwirft die laufende Aufnahme', s.hotkey.escapeCancels, v => s.hotkey.escapeCancels = v)),
          section('Mikrofon',
            select('Gerät', s.microphoneId ?? '', devOptions, v => s.microphoneId = v || null),
            h('div', { class: 'row' }, monBtn, meter)),
          section('Sprache & Text',
            select('Sprache', s.language, [['de', 'Deutsch'], ['en', 'Englisch'], ['auto', 'Automatisch erkennen']], v => s.language = v),
            select('Textbearbeitung', s.textMode, [['Original', 'Original – unverändert'], ['Clean', 'Bereinigt – Satzzeichen, Füllwörter, Selbstkorrekturen'], ['Professional', 'Professionell – zusätzlich Grammatik und Stil']], v => s.textMode = v,
              'Die KI ändert keine Fakten, Namen, Beträge oder Termine. Im Zweifel wird ihr Ergebnis verworfen.')),
          section('Einfügen',
            select('Methode', s.insertion.method, [['Auto', 'Automatisch (Zwischenablage + Einfügen)'], ['Clipboard', 'Immer Zwischenablage'], ['Typing', 'Zeichenweise tippen (ohne Zwischenablage)']], v => s.insertion.method = v),
            toggle('Zwischenablage danach wiederherstellen', s.insertion.restoreClipboard, v => s.insertion.restoreClipboard = v,
              'Nur wenn sie in der Zwischenzeit unverändert blieb. Diktate erscheinen nicht im Windows-Zwischenablageverlauf.'),
            number('Wartezeit Remotedesktop', s.insertion.remoteRestoreDelayMs, 500, 5000, 100, 'ms', v => s.insertion.remoteRestoreDelayMs = v)),
          section('Pill',
            toggle('Pill anzeigen', s.pill.visible, v => s.pill.visible = v),
            toggle('Immer im Vordergrund', s.pill.topmost, v => s.pill.topmost = v),
            number('Deckkraft', Math.round(s.pill.opacity * 100), 40, 100, 5, '%', v => s.pill.opacity = v / 100)),
          section('Archiv',
            toggle('Diktate im Archiv speichern', s.archive.enabled, v => s.archive.enabled = v),
            toggle('Audio behalten', s.archive.keepAudio, v => s.archive.keepAudio = v, 'Standard: Audio wird nach erfolgreicher Verarbeitung gelöscht.')),
          section('Leistung & Modelle',
            select('Leistungsstufe', s.performanceTier, [['Light', 'Leicht (ab ca. 8 GB RAM)'], ['Standard', 'Standard (ab ca. 16 GB RAM)']], v => s.performanceTier = v),
            toggle('Grafikkarte nutzen (Vulkan)', s.useGpu, v => s.useGpu = v, 'Bei Problemen schaltet HILDE automatisch auf die CPU um.'),
            number('Modelle entladen nach', s.modelIdleUnloadMinutes, 1, 240, 1, 'min Inaktivität', v => s.modelIdleUnloadMinutes = v),
            h('div', { class: 'row' }, h('span', { class: 'muted' }, `${installed} von ${models.models.length} Modellen installiert · noch ${fmtBytes(models.requiredBytes)} · frei ${fmtBytes(models.freeBytes)}`),
              h('button', { class: 'btn', onclick: () => openSetup(3) }, 'Modelle verwalten'))),
          section('Allgemein',
            toggle('Mit Windows starten', s.autostart, v => s.autostart = v),
            select('Darstellung', s.theme, [['system', 'Wie Windows'], ['light', 'Hell'], ['dark', 'Dunkel']], v => s.theme = v),
            h('div', { class: 'row' },
              h('button', { class: 'btn', onclick: () => openSetup(1) }, 'Einrichtung erneut ausführen'),
              h('button', { class: 'btn', onclick: () => call('app.openData') }, 'Datenordner öffnen'),
              h('button', { class: 'btn', onclick: () => call('app.openLogs') }, 'Protokolle öffnen'))),
          section('Über HILDE',
            h('p', null, `Version ${info.version} · Daten lokal unter `, h('code', null, info.dataPath)),
            h('p', { class: 'muted small' }, 'Keine Cloud, keine Telemetrie, keine Anmeldung. Protokolle enthalten nie Diktatinhalte.'),
            h('div', { class: 'row' },
              h('button', { class: 'btn', onclick: showHealth }, 'Komponenten prüfen'),
              h('button', { class: 'btn', onclick: showLicenses }, 'Lizenzen anzeigen')))));
    } catch (e) { toast(errorText(e), 'error'); }
  }

  function section(title: string, ...children: (Node | null)[]) {
    return h('section', { class: 'card pad' }, h('h2', null, title), ...children);
  }

  async function showHealth(): Promise<void> {
    try {
      const r = await call<any>('engines.health', {}, 20000);
      const line = (n: string, x: any) => `${n}: ${x.running ? 'läuft' : 'nicht geladen'}${x.backend ? ' · ' + x.backend : ''}${x.detail ? ' · ' + x.detail : ''}`;
      toast(`${line('Spracherkennung', r.speech)}\n${line('Textbereinigung', r.language)}${r.gpuFallback ? '\nGPU deaktiviert nach Absturz (CPU-Fallback)' : ''}`, 'info', 7000);
    } catch (e) { toast(errorText(e), 'error'); }
  }

  async function showLicenses(): Promise<void> {
    const text = await call<string>('app.licenses');
    const overlay = document.getElementById('overlay')!;
    const close = () => { dlg.remove(); overlay.classList.remove('show'); };
    const dlg = h('div', { class: 'dialog wide', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Lizenzen' },
      h('h2', null, 'Lizenzen der Drittkomponenten'), h('pre', { class: 'licenses' }, text || '—'),
      h('div', { class: 'row end' }, h('button', { class: 'btn primary', onclick: close }, 'Schließen')));
    dlg.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });
    overlay.append(dlg);
    overlay.classList.add('show');
    (dlg.querySelector('button') as HTMLButtonElement).focus();
  }

  draw();
  return () => offs.forEach(f => f());
}
