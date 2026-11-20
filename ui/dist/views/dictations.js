import { call, on } from '../bridge.js';
import { h, clear, toast, errorText, fmtDate, fmtDuration, debounce, confirmDialog, MODE_LABEL } from '../components/dom.js';
const STATUS_LABEL = {
    done: 'Eingefügt', recovered: 'Wiederhergestellt', failed: 'Fehlgeschlagen', processing: 'In Arbeit', recording: 'Aufnahme',
};
export function renderDictations(root) {
    let search = '';
    let selected = null;
    const list = h('div', { class: 'list', role: 'listbox', 'aria-label': 'Diktate' });
    const detail = h('section', { class: 'detail', 'aria-live': 'polite' });
    const searchBox = h('input', {
        type: 'search', class: 'input', placeholder: 'Archiv durchsuchen …', 'aria-label': 'Archiv durchsuchen',
        oninput: debounce((e) => { search = e.target.value; load(); }, 250),
    });
    clear(root);
    root.append(h('header', { class: 'view-head' }, h('div', null, h('h1', null, 'Diktate'), h('p', { class: 'muted' }, 'Taste halten, sprechen, loslassen – der Text erscheint im aktiven Feld. Doppeltippen = freihändig, Esc = verwerfen.')), searchBox), h('div', { class: 'split' }, list, detail));
    async function load() {
        try {
            const items = await call('transcripts.list', { search, limit: 300 });
            clear(list);
            if (items.length === 0) {
                list.append(h('div', { class: 'empty' }, search ? 'Keine Treffer.' : 'Noch keine Diktate. Halte die Diktiertaste gedrückt und sprich.'));
                clear(detail);
                return;
            }
            for (const it of items) {
                const card = h('button', {
                    class: 'card item' + (it.id === selected ? ' active' : ''), role: 'option',
                    'aria-selected': it.id === selected ? 'true' : 'false', dataset: { id: it.id }, onclick: () => select(it.id),
                }, h('div', { class: 'item-top' }, h('span', { class: 'item-title' }, it.title || 'Diktat'), h('span', { class: `badge s-${it.status}` }, STATUS_LABEL[it.status] ?? it.status)), h('div', { class: 'item-preview' }, it.preview || '—'), h('div', { class: 'item-meta' }, `${fmtDate(it.createdUtc)} · ${fmtDuration(it.durationMs)} · ${MODE_LABEL[it.mode] ?? it.mode}`));
                list.append(card);
            }
            if (!selected && items.length)
                select(items[0].id);
        }
        catch (e) {
            toast(errorText(e), 'error');
        }
    }
    async function select(id) {
        selected = id;
        try {
            const { record: r, hasAudio, error } = await call('transcripts.get', { id });
            const info = await call('app.info');
            const LABEL = { final: 'Endfassung', clean: 'Bereinigt', raw: 'Original' };
            const textOf = (v) => v === 'final' ? r.finalText : v === 'clean' ? r.cleanText : r.rawText;
            let variant = 'final';
            let sideBySide = false;
            let compareWith = 'raw';
            const title = h('input', { class: 'input title-input', value: r.title, 'aria-label': 'Titel' });
            const work = h('div', { class: 'work' });
            const tabs = h('div', { class: 'tabs', role: 'tablist' });
            const saveTitle = debounce(async () => {
                try {
                    await call('transcripts.update', { id, title: title.value });
                }
                catch (e) {
                    toast(errorText(e), 'error');
                }
            }, 600);
            const saveFinal = debounce(async () => {
                try {
                    await call('transcripts.update', { id, finalText: r.finalText });
                }
                catch (e) {
                    toast(errorText(e), 'error');
                }
            }, 600);
            title.addEventListener('input', saveTitle);
            const editor = (v) => {
                const t = h('textarea', { class: 'textarea', 'aria-label': LABEL[v], spellcheck: 'true' });
                t.value = textOf(v);
                t.readOnly = v !== 'final';
                if (v === 'final')
                    t.addEventListener('input', () => { r.finalText = t.value; saveFinal(); });
                return t;
            };
            const pane = (label, body, extra) => h('div', { class: 'pane' }, h('div', { class: 'pane-head' }, label, extra ?? null), body);
            const render = () => {
                clear(work);
                tabs.querySelectorAll('button').forEach(b => b.setAttribute('aria-selected', String(!sideBySide && b.dataset.v === variant)));
                sideBtn.setAttribute('aria-pressed', String(sideBySide));
                if (!sideBySide) {
                    work.append(editor(variant));
                    return;
                }
                const sel = h('select', { class: 'input mini', 'aria-label': 'Vergleichen mit', onchange: (e) => { compareWith = e.target.value; render(); } }, ['raw', 'clean'].map(v => h('option', { value: v, selected: v === compareWith }, LABEL[v])));
                work.append(h('div', { class: 'compare' }, pane(sel, editor(compareWith)), pane('Endfassung (bearbeitbar)', editor('final'))));
            };
            for (const v of ['final', 'clean', 'raw']) {
                tabs.append(h('button', { class: 'tab', role: 'tab', dataset: { v }, onclick: () => { variant = v; sideBySide = false; render(); } }, LABEL[v]));
            }
            const sideBtn = h('button', { class: 'tab', 'aria-pressed': 'false', title: 'Original und Endfassung nebeneinander',
                onclick: () => { sideBySide = !sideBySide; render(); } }, '⇆ Nebeneinander');
            // KI-Nachbearbeitung mit Vorher/Nachher
            const rewrite = async (kind) => {
                const source = sideBySide ? 'final' : variant;
                const before = textOf(source);
                if (!before.trim())
                    return;
                const proposal = h('textarea', { class: 'textarea', 'aria-label': 'Vorschlag', spellcheck: 'true' });
                proposal.value = '';
                proposal.placeholder = 'HILDE überarbeitet … (das kann einige Sekunden dauern)';
                proposal.readOnly = true;
                const notes = h('div', { class: 'notes' });
                const actions = h('div', { class: 'row' });
                const kindLabel = kind === 'shorten' ? 'Gekürzt' : 'Sachlich & klar';
                clear(work);
                work.append(h('div', { class: 'compare' }, pane(`Vorher · ${LABEL[source]}`, (() => { const t = editor(source); t.readOnly = true; return t; })()), pane(h('span', null, `Vorschlag · ${kindLabel}`), proposal, h('span', { class: 'spinner', 'aria-hidden': 'true' }))), notes, actions);
                try {
                    const res = await call('transcripts.rewrite', { id, kind, text: before }, 10 * 60000);
                    proposal.value = res.text;
                    proposal.readOnly = false;
                    work.querySelector('.spinner')?.remove();
                    const ratio = Math.round((1 - res.text.length / Math.max(1, before.length)) * 100);
                    notes.append(h('div', { class: 'muted small' }, `${res.seconds.toFixed(1)} s · ${ratio > 0 ? ratio + ' % kürzer' : Math.abs(ratio) + ' % länger'}`));
                    if (res.notes.length)
                        notes.append(h('div', { class: 'banner warn' }, h('strong', null, 'Bitte prüfen: '), res.notes.join(' · ')));
                    clear(actions);
                    actions.append(h('button', { class: 'btn primary', onclick: async () => {
                            r.finalText = proposal.value;
                            await call('transcripts.update', { id, finalText: r.finalText });
                            toast('Als Endfassung übernommen', 'ok');
                            variant = 'final';
                            sideBySide = false;
                            render();
                            load();
                        } }, 'Übernehmen'), h('button', { class: 'btn', onclick: async () => {
                            await navigator.clipboard.writeText(proposal.value).catch(() => { });
                            toast('Kopiert', 'ok');
                        } }, 'Kopieren'), h('button', { class: 'btn', onclick: () => rewrite(kind) }, 'Nochmal'), h('span', { class: 'spacer' }), h('button', { class: 'btn', onclick: () => render() }, 'Verwerfen'));
                }
                catch (e) {
                    work.querySelector('.spinner')?.remove();
                    notes.append(h('div', { class: 'banner error' }, errorText(e)));
                    actions.append(h('button', { class: 'btn', onclick: () => render() }, 'Zurück'));
                }
            };
            const aiButtons = info.llm
                ? [h('button', { class: 'btn ai', title: 'Text auf das Wesentliche kürzen', onclick: () => rewrite('shorten') }, '✂ Kürzen'),
                    h('button', { class: 'btn ai', title: 'Sachlicher und klarer formulieren', onclick: () => rewrite('clarify') }, '◎ Sachlich & klar')]
                : [h('span', { class: 'muted small' }, 'KI-Bearbeitung: Modell in der Einrichtung laden')];
            clear(detail);
            const parts = [
                title,
                h('div', { class: 'meta' }, `${fmtDate(r.createdUtc)} · ${fmtDuration(r.durationMs)} · ${r.language?.toUpperCase() ?? ''} · ${MODE_LABEL[r.mode] ?? r.mode}`, r.targetApp ? ` · ${r.targetApp}` : '', r.fallbackReason ? h('span', { class: 'note' }, ' · ' + r.fallbackReason) : ''),
                error ? h('div', { class: 'banner error' }, `${error} (${r.errorId})`) : null,
                h('div', { class: 'toolbar' }, tabs, sideBtn, h('span', { class: 'spacer' }), ...aiButtons),
                work,
                h('div', { class: 'row' }, h('button', { class: 'btn primary', onclick: async () => { await call('transcripts.copy', { id, variant: sideBySide ? 'final' : variant }); toast('Kopiert', 'ok'); } }, 'Kopieren'), h('button', { class: 'btn', onclick: () => exportAs(id, 'txt') }, 'Als TXT'), h('button', { class: 'btn', onclick: () => exportAs(id, 'md') }, 'Als Markdown'), h('button', { class: 'btn', onclick: () => exportAs(id, 'json') }, 'Als JSON'), (r.status === 'failed' && hasAudio)
                    ? h('button', { class: 'btn', onclick: async () => { await call('transcripts.reprocess', { id }); toast('Wird erneut verarbeitet …'); } }, 'Erneut verarbeiten')
                    : null, h('span', { class: 'spacer' }), h('button', {
                    class: 'btn danger-ghost', onclick: async () => {
                        if (!await confirmDialog('Diktat löschen?', 'Text und ggf. Audio werden vollständig gelöscht.', 'Löschen', true))
                            return;
                        await call('transcripts.delete', { id });
                        selected = null;
                        toast('Gelöscht', 'ok');
                        load();
                    }
                }, 'Löschen'))
            ];
            detail.append(...parts.filter((p) => p !== null));
            render();
            highlight(id);
        }
        catch (e) {
            toast(errorText(e), 'error');
        }
    }
    function highlight(id) {
        list.querySelectorAll('.item').forEach(n => {
            const on = n.dataset.id === id;
            n.classList.toggle('active', on);
            n.setAttribute('aria-selected', String(on));
        });
    }
    async function exportAs(id, format) {
        try {
            const r = await call('transcripts.export', { id, format });
            if (r?.saved)
                toast('Gespeichert: ' + r.path, 'ok');
        }
        catch (e) {
            toast(errorText(e), 'error');
        }
    }
    const off = on('transcript', () => load());
    load();
    return off;
}
