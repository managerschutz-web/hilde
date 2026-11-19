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
            let variant = 'final';
            const title = h('input', { class: 'input title-input', value: r.title, 'aria-label': 'Titel' });
            const text = h('textarea', { class: 'textarea', 'aria-label': 'Text', spellcheck: 'true' });
            const tabs = h('div', { class: 'tabs', role: 'tablist' });
            const save = debounce(async () => {
                try {
                    await call('transcripts.update', { id, title: title.value, finalText: variant === 'final' ? text.value : undefined });
                }
                catch (e) {
                    toast(errorText(e), 'error');
                }
            }, 600);
            const showVariant = (v) => {
                variant = v;
                text.value = v === 'final' ? r.finalText : v === 'clean' ? r.cleanText : r.rawText;
                text.readOnly = v !== 'final';
                tabs.querySelectorAll('button').forEach(b => b.setAttribute('aria-selected', String(b.dataset.v === v)));
            };
            for (const [v, label] of [['final', 'Endfassung'], ['clean', 'Bereinigt'], ['raw', 'Original']]) {
                tabs.append(h('button', { class: 'tab', role: 'tab', dataset: { v }, onclick: () => showVariant(v) }, label));
            }
            title.addEventListener('input', save);
            text.addEventListener('input', () => { if (variant === 'final') {
                r.finalText = text.value;
                save();
            } });
            clear(detail);
            const parts = [
                title,
                h('div', { class: 'meta' }, `${fmtDate(r.createdUtc)} · ${fmtDuration(r.durationMs)} · ${r.language?.toUpperCase() ?? ''} · ${MODE_LABEL[r.mode] ?? r.mode}`, r.targetApp ? ` · ${r.targetApp}` : '', r.fallbackReason ? h('span', { class: 'note' }, ' · ' + r.fallbackReason) : ''),
                error ? h('div', { class: 'banner error' }, `${error} (${r.errorId})`) : null,
                tabs, text,
                h('div', { class: 'row' }, h('button', { class: 'btn primary', onclick: async () => { await call('transcripts.copy', { id, variant }); toast('Kopiert', 'ok'); } }, 'Kopieren'), h('button', { class: 'btn', onclick: () => exportAs(id, 'txt') }, 'Als TXT'), h('button', { class: 'btn', onclick: () => exportAs(id, 'md') }, 'Als Markdown'), h('button', { class: 'btn', onclick: () => exportAs(id, 'json') }, 'Als JSON'), (r.status === 'failed' && hasAudio)
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
            showVariant('final');
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
