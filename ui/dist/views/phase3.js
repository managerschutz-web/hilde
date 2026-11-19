import { h, clear } from '../components/dom.js';
/** Meetings und Dateien folgen in Phase 3 – interne Builds zeigen hier den Umfang. */
export function renderPhase3(root, kind) {
    clear(root);
    const meetings = kind === 'meetings';
    root.append(h('header', { class: 'view-head' }, h('div', null, h('h1', null, meetings ? 'Meetings' : 'Dateien'))), h('div', { class: 'card pad phase' }, h('div', { class: 'badge s-processing' }, 'Interner Build · Phase 3'), h('h2', null, meetings ? 'Gesprächsaufnahme mit Sprechertrennung' : 'Audio- und Videodateien transkribieren'), h('ul', null, (meetings
        ? ['Mikrofon, Systemaudio (WASAPI Loopback) oder beides – getrennt gespeichert, synchron',
            'Starten, pausieren, fortsetzen, beenden – Aufnahmestatus immer sichtbar',
            'Zeitmarken und Sprecher 1, 2 … (umbenennbar), unsichere Zuordnungen markiert',
            'Hinweis auf Einwilligung aller Beteiligten vor jeder Aufnahme']
        : ['Drag-and-drop und Mehrfachimport: WAV, MP3, M4A, FLAC, OGG, AAC, MP4, MOV, MKV, AVI, WEBM',
            'Warteschlange mit Fortschritt, Abbruch, Wiederholung – übersteht Neustarts',
            'Zeitmarken, Sprechertrennung und Export; Originale bleiben unverändert']).map(t => h('li', null, t))), h('p', { class: 'muted' }, 'Diese Ansicht ist in der freigegebenen Version vollständig umgesetzt.')));
    return () => { };
}
