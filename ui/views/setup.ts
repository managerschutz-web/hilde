import { call, on } from '../bridge.js';
import { h, clear, toast, errorText, fmtBytes } from '../components/dom.js';

let opener: ((step: number) => void) | null = null;

/** Öffnet den Einrichtungsassistenten (Schritt 1–5). */
export function openSetup(step = 1): void { opener?.(step); }

export function initSetup(onDone: () => void): void {
  opener = (start: number) => show(start, onDone);
}

function show(startStep: number, onDone: () => void): void {
  const overlay = document.getElementById('overlay')!;
  clear(overlay);
  const panel = h('div', { class: 'wizard', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'HILDE einrichten' });
  overlay.append(panel);
  overlay.classList.add('show', 'full');

  let step = startStep;
  let tier = 'Standard';
  let llmSkipped = false;
  const offs: (() => void)[] = [];
  const cleanup = () => { offs.forEach(f => f()); offs.length = 0; };
  const close = () => { cleanup(); overlay.classList.remove('show', 'full'); clear(overlay); };

  const steps = ['Willkommen', 'Leistungsstufe', 'Modelle', 'Mikrofon & Test', 'Fertig'];

  async function render(): Promise<void> {
    cleanup();
    clear(panel);
    const progress = h('ol', { class: 'steps' }, steps.map((t, i) => h('li', { class: i + 1 === step ? 'current' : i + 1 < step ? 'done' : '' }, t)));
    const body = h('div', { class: 'wizard-body' });
    panel.append(h('div', { class: 'wizard-head' }, h('img', { src: 'assets/hilde-avatar.png', width: 44, height: 44, alt: '' }), h('h1', null, 'HILDE einrichten')), progress, body);
    try {
      if (step === 1) await stepWelcome(body);
      else if (step === 2) await stepTier(body);
      else if (step === 3) await stepModels(body);
      else if (step === 4) await stepMic(body);
      else await stepDone(body);
    } catch (e) { body.append(h('div', { class: 'banner error' }, errorText(e))); }
    (panel.querySelector('.wizard-body button.primary') as HTMLElement | null)?.focus();
  }

  const nav = (back: boolean, nextLabel: string, next: () => void | Promise<void>, nextDisabled = false) =>
    h('div', { class: 'row end wizard-nav' },
      back ? h('button', { class: 'btn', onclick: () => { step--; render(); } }, 'Zurück') : null,
      h('button', { class: 'btn primary', disabled: nextDisabled, onclick: next }, nextLabel));

  async function stepWelcome(body: HTMLElement): Promise<void> {
    const sys = await call<any>('setup.system');
    tier = (sys.recommendedTier as string).toLowerCase() === 'light' ? 'Light' : 'Standard';
    const gb = (b: number) => (b / 1024 ** 3).toLocaleString('de-DE', { maximumFractionDigits: 1 }) + ' GB';
    const ok = (good: boolean, text: string) => h('li', { class: good ? 'ok' : 'warn' }, (good ? '✓ ' : '! ') + text);
    body.append(
      h('div', { class: 'wizard-hero' },
        h('div', null,
          h('h2', null, 'Hallo, ich bin HILDE.'),
          h('p', null, 'Taste drücken. Sprechen. Loslassen. Ich wandle Sprache vollständig auf diesem PC in Text um – ohne Cloud, ohne Konto, ohne Telemetrie. Für die Einrichtung lade ich einmalig meine Sprachmodelle; danach arbeite ich offline.')),
        h('img', { src: 'assets/hilde-mascot.png', alt: '' })),
      h('ul', { class: 'checks' },
        ok(sys.isWindows11OrLater, sys.isWindows11OrLater ? 'Windows 11' : 'Windows 11 empfohlen – ' + sys.osDescription),
        ok(sys.is64Bit, '64-Bit-System'),
        ok(sys.totalMemoryBytes >= 7.5 * 1024 ** 3, `Arbeitsspeicher ${gb(sys.totalMemoryBytes)}`),
        ok((sys.freeDiskBytes ?? 0) >= 5 * 1024 ** 3, `Freier Speicher ${sys.freeDiskBytes != null ? gb(sys.freeDiskBytes) : 'unbekannt'}`),
        h('li', null, `${sys.logicalCores} Prozessorkerne`)),
      nav(false, 'Weiter', () => { step = 2; render(); }));
  }

  async function stepTier(body: HTMLElement): Promise<void> {
    const sys = await call<any>('setup.system');
    const rec = (sys.recommendedTier as string).toLowerCase() === 'light' ? 'Light' : 'Standard';
    const card = (value: string, title: string, text: string) => {
      const input = h('input', { type: 'radio', name: 'tier', value, checked: tier === value, onchange: () => { tier = value; } });
      return h('label', { class: 'choice' }, input, h('div', null, h('strong', null, title + (rec === value ? ' · empfohlen' : '')), h('p', { class: 'muted' }, text)));
    };
    body.append(
      h('h2', null, 'Leistungsstufe wählen'),
      h('p', { class: 'muted' }, 'Orientierungswerte – die tatsächlichen Zeiten misst der Test im übernächsten Schritt.'),
      card('Light', 'Leicht', 'Für Rechner mit ca. 8 GB RAM. Kleinere Modelle (~1,3 GB), schneller, etwas ungenauer.'),
      card('Standard', 'Standard', 'Für Rechner ab ca. 16 GB RAM. Große Erkennung + bessere Textbereinigung (~3,1 GB).'),
      nav(true, 'Weiter', async () => {
        const s = await call<any>('settings.get');
        s.performanceTier = tier;
        await call('settings.save', s);
        step = 3; render();
      }));
  }

  async function stepModels(body: HTMLElement): Promise<void> {
    const s = await call<any>('settings.get');
    tier = s.performanceTier;
    const updateInfo = h('div');
    const list = h('div', { class: 'models' });
    const status = h('div', { class: 'muted' });
    const bars = new Map<string, HTMLElement>();
    let downloading = false;

    const draw = async () => {
      const st = await call<any>('models.status', { tier });
      clear(list);
      for (const m of st.models) {
        const fill = h('div', { class: 'bar-fill', style: `width:${m.installed ? 100 : 0}%` });
        bars.set(m.id, fill);
        list.append(h('div', { class: 'model' },
          h('div', { class: 'model-head' },
            h('strong', null, m.displayName),
            h('span', { class: m.installed ? 'badge s-done' : 'badge' }, m.installed ? (m.verified ? 'installiert ✓' : 'installiert') : fmtBytes(m.sizeBytes))),
          h('div', { class: 'muted small' }, `Lizenz: ${m.license}${m.required ? '' : ' · optional (KI-Bereinigung)'}`),
          h('div', { class: 'bar' }, fill)));
      }
      status.textContent = `Noch zu laden: ${fmtBytes(st.requiredBytes)} · frei: ${fmtBytes(st.freeBytes)}`;
      const missingRequired = st.models.some((m: any) => m.required && !m.installed);
      const missingLlm = st.models.some((m: any) => !m.required && !m.installed);
      nextBtn.disabled = missingRequired || downloading;
      dlBtn.disabled = downloading || (!missingRequired && !missingLlm);
      skipLlm.parentElement!.style.display = missingLlm ? '' : 'none';
      downloading = st.downloading;
    };

    const skipLlm = h('input', { type: 'checkbox', onchange: (e: Event) => { llmSkipped = (e.target as HTMLInputElement).checked; } });
    const dlBtn = h('button', { class: 'btn primary', onclick: async () => {
      downloading = true; dlBtn.disabled = true; pauseBtn.hidden = false;
      await call('models.download', { tier, includeLlm: !llmSkipped });
    } }, 'Modelle herunterladen');
    const pauseBtn = h('button', { class: 'btn', hidden: true, onclick: () => call('models.cancel') }, 'Pausieren');
    const importFolder = h('button', { class: 'btn', onclick: () => doImport('folder') }, 'Aus Ordner importieren');
    const importZip = h('button', { class: 'btn', onclick: () => doImport('zip') }, 'Offline-Paket (ZIP)');
    const nextBtn = h('button', { class: 'btn primary', onclick: () => { step = 4; render(); } }, 'Weiter') as HTMLButtonElement;

    async function doImport(type: string): Promise<void> {
      try {
        const r = await call<any>('models.import', { type }, 30 * 60000);
        if (!r.cancelled) toast(`${r.imported} Modell(e) importiert`, r.imported > 0 ? 'ok' : 'error');
        draw();
      } catch (e) { toast(errorText(e), 'error'); }
    }

    offs.push(on('modelProgress', (p: any) => {
      const bar = bars.get(p.modelId);
      if (bar && p.bytesTotal) bar.style.width = `${Math.min(100, (p.bytesDone / p.bytesTotal) * 100).toFixed(1)}%`;
      if (p.phase === 'verify') status.textContent = `${p.displayName}: Prüfsumme wird geprüft …`;
      else if (p.phase === 'download' && p.bytesTotal) status.textContent = `${p.displayName}: ${fmtBytes(p.bytesDone)} von ${fmtBytes(p.bytesTotal)}`;
      else if (p.message) status.textContent = `${p.displayName}: ${p.message}`;
    }));
    offs.push(on('modelsDone', (r: any) => {
      downloading = false; pauseBtn.hidden = true;
      if (r.ok) toast('Alle Modelle sind eingerichtet', 'ok');
      else toast(r.message + (r.errorId ? ` (${r.errorId})` : ''), r.cancelled ? 'info' : 'error', 6000);
      draw();
    }));

    body.append(
      h('h2', null, 'Modelle einrichten'),
      h('p', { class: 'muted' }, 'Downloads lassen sich unterbrechen und später fortsetzen. Jede Datei wird per SHA-256 geprüft. Ohne Internet: Offline-Paket importieren.'),
      updateInfo, list, status,
      h('label', { class: 'check' }, skipLlm, 'KI-Textbereinigung vorerst überspringen (regelbasierte Bereinigung bleibt aktiv)'),
      h('div', { class: 'row' }, dlBtn, pauseBtn, importFolder, importZip),
      h('div', { class: 'row end wizard-nav' }, h('button', { class: 'btn', onclick: () => { step = 2; render(); } }, 'Zurück'), nextBtn));
    await draw();

    // Neuere geprüfte Modellliste? Bei Neuinstallation direkt verwenden, sonst anbieten.
    try {
      const st = await call<any>('models.status', { tier });
      const r = await call<any>('models.checkUpdates', { tier }, 20000);
      if (r.ok && r.available) {
        const nothingInstalled = !st.models.some((m: any) => m.installed && m.kind !== 'vad');
        if (nothingInstalled) {
          await call('models.applyUpdate', { tier, includeLlm: !llmSkipped });
          downloading = true; pauseBtn.hidden = false;
          updateInfo.append(h('div', { class: 'banner learn' }, `Neueste geprüfte Modellliste (Stand ${r.remoteDate}) wird verwendet – Download läuft.`));
          await draw();
        } else {
          updateInfo.append(h('div', { class: 'banner learn' },
            h('span', null, `Neuere geprüfte Modelle verfügbar (Stand ${r.remoteDate}).`), h('span', { class: 'spacer' }),
            h('button', { class: 'btn small primary', onclick: async () => {
              await call('models.applyUpdate', { tier, includeLlm: !llmSkipped });
              updateInfo.textContent = 'Neue Modelle werden geladen …';
              downloading = true; pauseBtn.hidden = false; await draw();
            } }, 'Aktualisieren')));
        }
      }
    } catch { /* Liste nicht erreichbar → mitgelieferte Modelle */ }
  }

  async function stepMic(body: HTMLElement): Promise<void> {
    const s = await call<any>('settings.get');
    const devices = await call<any[]>('devices.list').catch(() => []);
    const meter = h('div', { class: 'meter big' }, h('div', { class: 'meter-fill' }));
    const result = h('div', { class: 'test-result' });
    let recording = false;
    offs.push(on('level', (l: number) => { (meter.firstChild as HTMLElement).style.width = `${Math.round(l * 100)}%`; }));
    await call('mic.monitorStart', { deviceId: s.microphoneId }).catch(() => { });
    offs.push(() => { call('mic.monitorStop').catch(() => { }); if (recording) call('test.cancel').catch(() => { }); });

    const sel = h('select', { class: 'input', onchange: async (e: Event) => {
      s.microphoneId = (e.target as HTMLSelectElement).value || null;
      await call('settings.save', s);
      await call('mic.monitorStart', { deviceId: s.microphoneId }).catch(err => toast(errorText(err), 'error'));
    } },
      h('option', { value: '' }, 'Windows-Standardmikrofon'),
      devices.map((d: any) => h('option', { value: d.id, selected: d.id === s.microphoneId }, d.name + (d.isDefault ? ' (Standard)' : ''))));

    const testBtn = h('button', { class: 'btn primary', onclick: async () => {
      if (!recording) {
        await call('mic.monitorStop').catch(() => { });
        try { await call('test.start'); } catch (e) { toast(errorText(e), 'error'); return; }
        recording = true;
        testBtn.textContent = 'Stopp – Text erkennen';
        clear(result);
        result.append(h('p', { class: 'muted' }, 'Sprich einen kurzen Satz, z. B. „Ähm, der Termin ist am Dienstag, nein, am Mittwoch um zehn.“'));
      } else {
        recording = false;
        testBtn.textContent = 'Wird verarbeitet …';
        testBtn.setAttribute('disabled', '');
        try {
          const r = await call<any>('test.stop', {}, 5 * 60000);
          clear(result);
          if (!r.ok) result.append(h('div', { class: 'banner error' }, r.message));
          else result.append(
            h('div', { class: 'label' }, 'Erkannt'), h('div', { class: 'quote' }, r.raw),
            h('div', { class: 'label' }, 'Aufbereitet'), h('div', { class: 'quote strong' }, r.clean),
            h('div', { class: 'muted small' }, `Audio ${r.audioSeconds.toFixed(1)} s · Erkennung ${r.asrSeconds.toFixed(1)} s · gesamt ${r.totalSeconds.toFixed(1)} s${r.usedModel ? '' : ' · ohne KI: ' + (r.fallback ?? 'Regeln')}`));
        } catch (e) { toast(errorText(e), 'error'); }
        testBtn.removeAttribute('disabled');
        testBtn.textContent = 'Erneut testen';
        await call('mic.monitorStart', { deviceId: s.microphoneId }).catch(() => { });
      }
    } }, 'Testdiktat starten');

    body.append(
      h('h2', null, 'Mikrofon wählen und testen'),
      h('label', { class: 'field' }, h('span', null, 'Mikrofon'), sel),
      meter,
      h('p', { class: 'muted small' }, 'Der Balken sollte beim Sprechen deutlich ausschlagen. Der erste Test dauert länger, weil die Modelle geladen werden.'),
      h('div', { class: 'row' }, testBtn), result,
      nav(true, 'Weiter', () => { step = 5; render(); }));
  }

  async function stepDone(body: HTMLElement): Promise<void> {
    const s = await call<any>('settings.get');
    const auto = h('input', { type: 'checkbox', checked: s.autostart });
    body.append(
      h('h2', null, 'Fertig eingerichtet'),
      h('div', { class: 'howto' },
        h('div', null, h('kbd', null, s.hotkey.displayName), ' halten → sprechen → loslassen'),
        h('div', null, h('kbd', null, s.hotkey.displayName), ' zweimal tippen → freihändig, erneut drücken → fertig'),
        h('div', null, h('kbd', null, 'Esc'), ' → Aufnahme verwerfen')),
      h('p', { class: 'muted' }, 'Der Text landet in dem Feld, das beim Drücken der Taste aktiv war. Klappt das nicht, bietet HILDE ihn zum Kopieren an.'),
      h('label', { class: 'check' }, auto, 'HILDE mit Windows starten'),
      nav(true, 'Los geht’s', async () => {
        s.autostart = auto.checked;
        await call('settings.save', s);
        await call('setup.complete', { llmSkipped });
        close();
        onDone();
      }));
  }

  render();
}
