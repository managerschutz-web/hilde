import { call, on } from '../bridge.js';
import { h, clear, toast, errorText, debounce, fmtBytes, confirmDialog } from '../components/dom.js';
import { openSetup } from './setup.js';
export function applyTheme(theme) {
    document.documentElement.dataset.theme = theme === 'light' || theme === 'dark' ? theme : 'system';
}
export function renderSettings(root) {
    clear(root);
    const offs = [];
    let s;
    const persist = debounce(async () => {
        try {
            s = await call('settings.save', s);
            applyTheme(s.theme);
            toast('Gespeichert', 'ok', 1200);
        }
        catch (e) {
            toast(errorText(e), 'error');
        }
    }, 350);
    function select(label, value, options, set, help) {
        const sel = h('select', { class: 'input', onchange: (e) => { set(e.target.value); persist(); } }, options.map(([v, l]) => h('option', { value: v, selected: v.toLowerCase() === String(value).toLowerCase() }, l)));
        return h('label', { class: 'field' }, h('span', null, label), sel, help ? h('small', { class: 'muted' }, help) : null);
    }
    function toggle(label, value, set, help) {
        const cb = h('input', { type: 'checkbox', role: 'switch', checked: value, onchange: (e) => { set(e.target.checked); persist(); } });
        return h('label', { class: 'switch-row' }, h('span', null, h('span', null, label), help ? h('small', { class: 'muted block' }, help) : null), cb);
    }
    function number(label, value, min, max, step, unit, set) {
        const inp = h('input', { type: 'number', class: 'input narrow', value, min, max, step,
            onchange: (e) => { set(Number(e.target.value)); persist(); } });
        return h('label', { class: 'field inline' }, h('span', null, label), h('span', { class: 'row tight' }, inp, h('span', { class: 'muted' }, unit)));
    }
    async function draw() {
        try {
            s = await call('settings.get');
            const [devices, info, models, loc, uninst, voices] = await Promise.all([
                call('devices.list').catch(() => []),
                call('app.info'),
                call('models.status', { tier: s.performanceTier }),
                call('models.location'),
                call('app.uninstallInfo').catch(() => ({ available: false })),
                call('tts.voices').catch(() => []),
            ]);
            clear(root);
            // Hotkey
            const hotkeyBtn = h('button', { class: 'btn', onclick: async () => {
                    hotkeyBtn.textContent = 'Jetzt gewünschte Taste drücken …';
                    try {
                        const r = await call('hotkey.capture', {}, 15000);
                        if (r.ok) {
                            s.hotkey.virtualKey = r.vk;
                            s.hotkey.displayName = r.name;
                            persist();
                        }
                        else
                            toast(r.message, 'error', 5000);
                    }
                    catch (e) {
                        toast(errorText(e), 'error');
                    }
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
                    }
                    catch (e) {
                        monitoring = false;
                        toast(errorText(e), 'error');
                    }
                } }, 'Pegel testen');
            offs.push(on('level', (l) => { meter.firstChild.style.width = `${Math.round(l * 100)}%`; }));
            offs.push(() => { if (monitoring)
                call('mic.monitorStop').catch(() => { }); });
            const devOptions = [['', 'Windows-Standardmikrofon'], ...devices.map((d) => [d.id, d.name + (d.isDefault ? ' (Standard)' : '')])];
            const installed = models.models.filter((m) => m.installed).length;
            root.append(h('header', { class: 'view-head' }, h('div', null, h('h1', null, 'Einstellungen'), h('p', { class: 'muted' }, 'Änderungen werden sofort gespeichert.')), h('div', { class: 'row tight' }, h('button', { class: 'btn', onclick: () => call('app.openLogs') }, 'Protokolle öffnen'), h('button', { class: 'btn', onclick: () => call('app.openData') }, 'Datenordner'))), h('div', { class: 'settings' }, section('Diktat', h('div', { class: 'field inline' }, h('span', null, 'Diktiertaste'), hotkeyBtn), number('Mindestens halten', s.hotkey.minHoldMs, 100, 800, 25, 'ms', v => s.hotkey.minHoldMs = v), number('Doppeltipp-Fenster', s.hotkey.doubleTapMs, 200, 800, 25, 'ms', v => s.hotkey.doubleTapMs = v), toggle('Esc verwirft die laufende Aufnahme', s.hotkey.escapeCancels, v => s.hotkey.escapeCancels = v), toggle('Live-Text während des Sprechens', s.livePreview, v => s.livePreview = v, 'Zeigt den erkannten Text in der Pill und rechnet schon vor – lange Diktate sind danach schneller fertig.'), toggle('Diktierbefehle', s.dictationCommands, v => s.dictationCommands = v, '„neuer Absatz“, „neue Zeile“ und „Doppelpunkt“ werden umgesetzt.'), toggle('Schnellmodus für kurze Diktate', s.fastShortDictation, v => s.fastShortDictation = v, 'Kurze Aufnahmen werden mit kleinerem Rechenfenster erkannt. Bei Erkennungsproblemen testweise ausschalten.')), voiceSection(voices, info), section('Mikrofon', select('Gerät', s.microphoneId ?? '', devOptions, v => s.microphoneId = v || null), h('div', { class: 'row' }, monBtn, meter)), section('Sprache & Text', select('Sprache', s.language, [['de', 'Deutsch'], ['en', 'Englisch'], ['auto', 'Automatisch erkennen']], v => s.language = v), select('Textbearbeitung', s.textMode, [['Original', 'Original – unverändert'], ['Clean', 'Bereinigt – Satzzeichen, Füllwörter, Selbstkorrekturen'], ['Professional', 'Professionell – zusätzlich Grammatik und Stil']], v => s.textMode = v, 'Die KI ändert keine Fakten, Namen, Beträge oder Termine. Im Zweifel wird ihr Ergebnis verworfen.')), section('Formatierung', h('p', { class: 'muted small' }, 'Gilt in den Modi „Bereinigt“ und „Professionell“.'), toggle('Beträge', s.format.amounts, v => s.format.amounts = v, '„1250 Euro“ → 1.250 €, „99,5 Euro“ → 99,50 €'), toggle('Datumsangaben', s.format.dates, v => s.format.dates = v, '„3. Mai 2026“ → 03.05.2026'), toggle('Uhrzeiten', s.format.times, v => s.format.times = v, '„10 Uhr 30“ → 10:30 Uhr'), toggle('Prozent', s.format.percent, v => s.format.percent = v, '„5 Prozent“ → 5 %'), toggle('IBAN und Kennzeichen', s.format.codes, v => s.format.codes = v, 'IBAN in Viererblöcken, „Kennzeichen IGB AB 123“ → IGB-AB 123')), section('Einfügen', select('Nach dem Diktat', s.insertion.flow ?? 'Auto', [['Auto', 'Halten = direkt einfügen, Doppeltippen = Diktierfenster'], ['Direct', 'Immer direkt einfügen'], ['Editor', 'Immer im Diktierfenster bearbeiten']], v => s.insertion.flow = v, 'Im Diktierfenster siehst du den Text live, kannst ihn ändern, kürzen oder sachlich fassen lassen. „Einfügen ↗“ hängt ihn an den Mauszeiger – klick dorthin, wo er hin soll.'), select('Methode', s.insertion.method, [['Auto', 'Automatisch (Zwischenablage + Einfügen)'], ['Clipboard', 'Immer Zwischenablage'], ['Typing', 'Zeichenweise tippen (ohne Zwischenablage)']], v => s.insertion.method = v), toggle('Zwischenablage danach wiederherstellen', s.insertion.restoreClipboard, v => s.insertion.restoreClipboard = v, 'Nur wenn sie in der Zwischenzeit unverändert blieb. Diktate erscheinen nicht im Windows-Zwischenablageverlauf.'), number('Wartezeit Remotedesktop', s.insertion.remoteRestoreDelayMs, 500, 5000, 100, 'ms', v => s.insertion.remoteRestoreDelayMs = v)), section('Pill', toggle('Pill anzeigen', s.pill.visible, v => s.pill.visible = v), toggle('Immer im Vordergrund', s.pill.topmost, v => s.pill.topmost = v), number('Deckkraft', Math.round(s.pill.opacity * 100), 40, 100, 5, '%', v => s.pill.opacity = v / 100), colorRow('Hintergrund', s.pill.backgroundColor, v => s.pill.backgroundColor = v), colorRow('Akzent (Aufnahme, Pegel)', s.pill.accentColor, v => s.pill.accentColor = v), h('div', { class: 'row' }, ...[['#161A22', '#34D399', 'Nacht'], ['#F4F5F7', '#0F9F75', 'Hell'], ['#0B1E3A', '#7EF0B4', 'dieapp'], ['#2A1630', '#F472B6', 'Beere'], ['#1F2937', '#F59E0B', 'Bernstein']]
                .map(([bg, ac, name]) => h('button', {
                class: 'swatch', title: name, 'aria-label': `Farbschema ${name}`, style: `--bg:${bg};--ac:${ac}`,
                onclick: () => { s.pill.backgroundColor = bg; s.pill.accentColor = ac; persist(); draw(); }
            }, h('span', null), name)))), section('Archiv', toggle('Diktate im Archiv speichern', s.archive.enabled, v => s.archive.enabled = v), toggle('Audio behalten', s.archive.keepAudio, v => s.archive.keepAudio = v, 'Nötig für die Wiedergabe im Archiv. Standard: Audio wird nach der Verarbeitung gelöscht.'), number('Audio automatisch löschen nach', s.archive.audioRetentionDays, 0, 3650, 1, 'Tagen (0 = nie)', v => s.archive.audioRetentionDays = v)), section('Leistung & Modelle', select('Leistungsstufe', s.performanceTier, [['Light', 'Leicht (ab ca. 8 GB RAM)'], ['Standard', 'Standard (ab ca. 16 GB RAM)']], v => s.performanceTier = v), toggle('Grafikkarte nutzen (Vulkan)', s.useGpu, v => s.useGpu = v, 'Bei Problemen schaltet HILDE automatisch auf die CPU um.'), number('Modelle entladen nach', s.modelIdleUnloadMinutes, 1, 240, 1, 'min Inaktivität', v => s.modelIdleUnloadMinutes = v), h('div', { class: 'row' }, h('span', { class: 'muted' }, `${installed} von ${models.models.length} Modellen installiert · noch ${fmtBytes(models.requiredBytes)} · frei ${fmtBytes(models.freeBytes)}`), h('button', { class: 'btn', onclick: () => openSetup(3) }, 'Modelle verwalten')), h('div', { class: 'field' }, h('span', null, 'Speicherort der Modelle'), h('code', { class: 'path' }, loc.path), loc.oneDrive ? h('small', { class: 'warn' }, 'Achtung: Dieser Ordner liegt in OneDrive und wird in die Cloud synchronisiert.') : null, h('div', { class: 'row tight' }, h('button', { class: 'btn', onclick: moveModels }, 'Ändern …'), loc.isDefault ? null : h('button', { class: 'btn', onclick: () => doMove({ reset: true }) }, 'Standard wiederherstellen')))), section('Allgemein', toggle('Mit Windows starten', s.autostart, v => s.autostart = v), select('Darstellung', s.theme, [['system', 'Wie Windows'], ['light', 'Hell'], ['dark', 'Dunkel']], v => s.theme = v), h('div', { class: 'row' }, h('button', { class: 'btn', onclick: () => openSetup(1) }, 'Einrichtung erneut ausführen'), h('button', { class: 'btn', onclick: () => call('app.openData') }, 'Datenordner öffnen'), h('button', { class: 'btn', onclick: () => call('app.openLogs') }, 'Protokolle öffnen'))), section('Über HILDE', h('p', null, `Version ${info.version} · Daten lokal unter `, h('code', null, info.dataPath)), h('p', { class: 'muted small' }, 'Keine Cloud, keine Telemetrie, keine Anmeldung. Protokolle enthalten nie Diktatinhalte.'), h('div', { class: 'row' }, h('button', { class: 'btn', onclick: showHealth }, 'Komponenten prüfen'), h('button', { class: 'btn', onclick: showLicenses }, 'Lizenzen anzeigen'), uninst.available ? h('button', { class: 'btn danger-ghost', onclick: uninstall }, 'HILDE deinstallieren …') : null))));
        }
        catch (e) {
            toast(errorText(e), 'error');
        }
    }
    function voiceSection(voices, _info) {
        const keyBtn = h('button', { class: 'btn', onclick: async () => {
                keyBtn.textContent = 'Jetzt gewünschte Taste drücken …';
                try {
                    const r = await call('hotkey.capture', { target: 'read' }, 15000);
                    if (r.ok) {
                        s.readAloud.virtualKey = r.vk;
                        s.readAloud.displayName = r.name;
                        persist();
                    }
                    else
                        toast(r.message, 'error', 5000);
                }
                catch (e) {
                    toast(errorText(e), 'error');
                }
                keyBtn.textContent = s.readAloud.displayName;
            } }, s.readAloud.displayName);
        const rows = voices.map(v => {
            const radio = h('input', { type: 'radio', name: 'voice', checked: v.id === s.readAloud.voiceId, disabled: !v.installed,
                onchange: () => { s.readAloud.voiceId = v.id; persist(); } });
            const action = v.installed
                ? h('button', { class: 'btn small', onclick: () => call('tts.speak', { text: `Hallo, ich bin ${v.displayName.replace(/^Stimme /, '').split(' ')[0]}. So klinge ich, wenn ich dir deine Texte vorlese.` }) }, '▶ Probe hören')
                : h('button', { class: 'btn small', onclick: async (e) => {
                        const b = e.target;
                        b.disabled = true;
                        b.textContent = 'Lädt …';
                        const r = await call('tts.download', { id: v.id }, 30 * 60000);
                        if (r.ok) {
                            toast('Stimme installiert', 'ok');
                            draw();
                        }
                        else {
                            toast(r.message, 'error');
                            b.disabled = false;
                            b.textContent = 'Stimme laden';
                        }
                    } }, `Stimme laden (${fmtBytes(v.sizeBytes)})`);
            return h('div', { class: 'voice' }, h('label', { class: 'check' }, radio, v.displayName), h('span', { class: 'spacer' }), action);
        });
        return section('Vorlesen', h('p', { class: 'muted small' }, 'Natürliche Stimme, komplett offline. Text in einem beliebigen Programm markieren und die Vorlesetaste drücken – oder Rechtsklick auf die Pill. Nochmal drücken stoppt.'), ...rows, h('div', { class: 'field inline' }, h('span', null, 'Vorlesetaste'), keyBtn), number('Tempo', Math.round(s.readAloud.speed * 100), 50, 200, 5, '%', v => s.readAloud.speed = v / 100));
    }
    function colorRow(label, value, set) {
        const inp = h('input', { type: 'color', class: 'color', value, onchange: (e) => { set(e.target.value.toUpperCase()); persist(); } });
        return h('label', { class: 'field inline' }, h('span', null, label), inp);
    }
    async function moveModels() {
        const r = await call('models.chooseFolder');
        if (r.cancelled)
            return;
        if (r.oneDrive && !await confirmDialog('OneDrive-Ordner gewählt', 'Dieser Ordner wird mit OneDrive synchronisiert. Die Modelle (mehrere GB) würden in die Cloud hochgeladen und könnten ausgelagert werden. Trotzdem verwenden?', 'Trotzdem verwenden', true))
            return;
        if (!await confirmDialog('Modelle verschieben?', `Neuer Speicherort: ${r.path}\nFrei: ${fmtBytes(r.freeBytes)}. HILDE verschiebt die Dateien und prüft sie anschließend.`, 'Verschieben'))
            return;
        doMove({ path: r.path });
    }
    async function doMove(params) {
        toast('Modelle werden verschoben …', 'info', 6000);
        try {
            const r = await call('models.move', params, 60 * 60000);
            toast(`${r.moved} Modell(e) verschoben nach ${r.path}`, 'ok', 6000);
            draw();
        }
        catch (e) {
            toast(errorText(e), 'error', 8000);
        }
    }
    function uninstall() {
        const overlay = document.getElementById('overlay');
        const del = h('input', { type: 'checkbox' });
        const close = () => { dlg.remove(); overlay.classList.remove('show'); };
        const dlg = h('div', { class: 'dialog', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'HILDE deinstallieren' }, h('h2', null, 'HILDE deinstallieren?'), h('p', null, 'HILDE wird beendet und der Windows-Deinstaller gestartet.'), h('label', { class: 'check' }, del, 'Auch meine Daten löschen (Archiv, Wörterbuch, Einstellungen, Modelle im Standardordner)'), h('div', { class: 'row end' }, h('button', { class: 'btn', onclick: close }, 'Abbrechen'), h('button', { class: 'btn danger', onclick: async () => {
                try {
                    await call('app.uninstall', { deleteData: del.checked });
                }
                catch (e) {
                    toast(errorText(e), 'error');
                    close();
                }
            } }, 'Deinstallieren')));
        dlg.addEventListener('keydown', e => { if (e.key === 'Escape')
            close(); });
        overlay.append(dlg);
        overlay.classList.add('show');
    }
    function section(title, ...children) {
        return h('section', { class: 'card pad' }, h('h2', null, title), ...children);
    }
    async function showHealth() {
        try {
            const r = await call('engines.health', {}, 20000);
            const line = (n, x) => `${n}: ${x.running ? 'läuft' : 'nicht geladen'}${x.backend ? ' · ' + x.backend : ''}${x.detail ? ' · ' + x.detail : ''}`;
            toast(`${line('Spracherkennung', r.speech)}\n${line('Textbereinigung', r.language)}${r.gpuFallback ? '\nGPU deaktiviert nach Absturz (CPU-Fallback)' : ''}`, 'info', 7000);
        }
        catch (e) {
            toast(errorText(e), 'error');
        }
    }
    async function showLicenses() {
        const text = await call('app.licenses');
        const overlay = document.getElementById('overlay');
        const close = () => { dlg.remove(); overlay.classList.remove('show'); };
        const dlg = h('div', { class: 'dialog wide', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Lizenzen' }, h('h2', null, 'Lizenzen der Drittkomponenten'), h('pre', { class: 'licenses' }, text || '—'), h('div', { class: 'row end' }, h('button', { class: 'btn primary', onclick: close }, 'Schließen')));
        dlg.addEventListener('keydown', e => { if (e.key === 'Escape')
            close(); });
        overlay.append(dlg);
        overlay.classList.add('show');
        dlg.querySelector('button').focus();
    }
    draw();
    return () => offs.forEach(f => f());
}
