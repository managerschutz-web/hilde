import { call } from '../bridge.js';
import { h, clear, toast, errorText, debounce, confirmDialog } from '../components/dom.js';
export function renderDictionary(root) {
    clear(root);
    const rulesBox = h('div', { class: 'card pad' });
    const termsBox = h('div', { class: 'card pad' });
    const snippetsBox = h('div', { class: 'card pad span2' });
    root.append(h('header', { class: 'view-head' }, h('div', null, h('h1', null, 'Wörterbuch'), h('p', { class: 'muted' }, 'Ersetzungen gelten exakt und vor sowie nach der KI-Bereinigung. Fachbegriffe helfen der Spracherkennung und bleiben unverändert.'))), h('div', { class: 'grid2' }, rulesBox, termsBox, snippetsBox));
    async function load() {
        try {
            const { rules, terms, snippets } = await call('dictionary.get');
            drawRules(rules);
            drawTerms(terms);
            drawSnippets(snippets);
        }
        catch (e) {
            toast(errorText(e), 'error');
        }
    }
    function drawRules(rules) {
        clear(rulesBox);
        rulesBox.append(h('h2', null, 'Ersetzungsregeln'));
        const table = h('table', { class: 'table' }, h('thead', null, h('tr', null, h('th', null, 'Erkannt'), h('th', null, 'Wird zu'), h('th', null, 'Optionen'), h('th', null, ''))));
        const body = h('tbody');
        for (const r of rules) {
            body.append(h('tr', { class: r.enabled ? '' : 'disabled' }, h('td', null, h('code', null, r.pattern)), h('td', null, h('code', null, r.replacement)), h('td', { class: 'muted small' }, [r.wholeWord ? 'ganzes Wort' : 'Teilwort', r.caseSensitive ? 'Groß/klein exakt' : 'Groß/klein egal', r.enabled ? '' : 'aus'].filter(Boolean).join(' · ')), h('td', { class: 'actions' }, h('button', { class: 'btn small', onclick: () => editRule(r) }, 'Bearbeiten'), h('button', {
                class: 'btn small danger-ghost', 'aria-label': `Regel ${r.pattern} löschen`, onclick: async () => {
                    if (!await confirmDialog('Regel löschen?', `„${r.pattern}“ → „${r.replacement}“`, 'Löschen', true))
                        return;
                    await call('dictionary.deleteRule', { id: r.id });
                    load();
                }
            }, '✕'))));
        }
        table.append(body);
        rulesBox.append(table, h('button', { class: 'btn primary', onclick: () => editRule(null) }, '+ Neue Regel'));
    }
    function editRule(r) {
        const rule = r ? { ...r } : { id: 0, pattern: '', replacement: '', wholeWord: true, caseSensitive: false, enabled: true, sortOrder: 0 };
        const overlay = document.getElementById('overlay');
        const pattern = h('input', { class: 'input', value: rule.pattern, placeholder: 'z. B. Marta und Prahl' });
        const replacement = h('input', { class: 'input', value: rule.replacement, placeholder: 'z. B. MARTENS & PRAHL' });
        const whole = h('input', { type: 'checkbox', checked: rule.wholeWord });
        const cs = h('input', { type: 'checkbox', checked: rule.caseSensitive });
        const en = h('input', { type: 'checkbox', checked: rule.enabled });
        const sample = h('textarea', { class: 'textarea small', placeholder: 'Beispielsatz für die Vorschau …' });
        sample.value = rule.pattern ? `Termin mit ${rule.pattern} am Montag.` : '';
        const preview = h('div', { class: 'preview' }, '—');
        const collect = () => ({ ...rule, pattern: pattern.value, replacement: replacement.value, wholeWord: whole.checked, caseSensitive: cs.checked, enabled: en.checked });
        const refresh = debounce(async () => {
            if (!pattern.value.trim() || !sample.value) {
                preview.textContent = '—';
                return;
            }
            try {
                const res = await call('dictionary.preview', { rule: collect(), sample: sample.value });
                preview.textContent = `${res.result}  (${res.matches} Treffer)`;
            }
            catch (e) {
                preview.textContent = errorText(e);
            }
        }, 200);
        [pattern, replacement, sample].forEach(i => i.addEventListener('input', refresh));
        [whole, cs, en].forEach(i => i.addEventListener('change', refresh));
        const close = () => { dlg.remove(); overlay.classList.remove('show'); };
        const dlg = h('div', { class: 'dialog wide', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Ersetzungsregel' }, h('h2', null, r ? 'Regel bearbeiten' : 'Neue Regel'), h('label', { class: 'field' }, h('span', null, 'Erkannter Text'), pattern), h('label', { class: 'field' }, h('span', null, 'Ersetzen durch'), replacement), h('div', { class: 'row' }, h('label', { class: 'check' }, whole, 'nur ganze Wörter'), h('label', { class: 'check' }, cs, 'Groß-/Kleinschreibung beachten'), h('label', { class: 'check' }, en, 'aktiv')), h('label', { class: 'field' }, h('span', null, 'Vorschau'), sample), preview, h('div', { class: 'row end' }, h('button', { class: 'btn', onclick: close }, 'Abbrechen'), h('button', {
            class: 'btn primary', onclick: async () => {
                try {
                    await call('dictionary.saveRule', collect());
                    close();
                    toast('Gespeichert', 'ok');
                    load();
                }
                catch (e) {
                    toast(errorText(e), 'error');
                }
            }
        }, 'Speichern')));
        dlg.addEventListener('keydown', e => { if (e.key === 'Escape')
            close(); });
        overlay.append(dlg);
        overlay.classList.add('show');
        pattern.focus();
        refresh();
    }
    function drawSnippets(snippets) {
        clear(snippetsBox);
        snippetsBox.append(h('h2', null, 'Textbausteine'), h('p', { class: 'muted small' }, 'Sag beim Diktieren „Baustein“ und den Namen – z. B. „Baustein Gruß“ – und HILDE setzt den hinterlegten Text ein. Zeilenumbrüche bleiben erhalten.'), h('div', { class: 'snippets' }, snippets.length === 0 ? h('span', { class: 'muted' }, 'Noch keine Bausteine.') : null, snippets.map(s => h('div', { class: 'snippet' }, h('div', { class: 'snippet-head' }, h('strong', null, '„Baustein ' + s.name + '“'), h('span', { class: 'spacer' }), h('button', { class: 'btn small', onclick: () => editSnippet(s) }, 'Bearbeiten'), h('button', { class: 'btn small danger-ghost', 'aria-label': `Baustein ${s.name} löschen`, onclick: async () => {
                if (!await confirmDialog('Baustein löschen?', s.name, 'Löschen', true))
                    return;
                await call('dictionary.deleteSnippet', { id: s.id });
                load();
            } }, '✕')), h('div', { class: 'snippet-text' }, s.text)))), h('button', { class: 'btn primary', onclick: () => editSnippet(null) }, '+ Neuer Baustein'));
    }
    function editSnippet(s) {
        const overlay = document.getElementById('overlay');
        const name = h('input', { class: 'input', value: s?.name ?? '', placeholder: 'z. B. Gruß Kunde' });
        const text = h('textarea', { class: 'textarea', placeholder: 'Text, der eingefügt werden soll …' });
        text.value = s?.text ?? '';
        const close = () => { dlg.remove(); overlay.classList.remove('show'); };
        const dlg = h('div', { class: 'dialog wide', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Textbaustein' }, h('h2', null, s ? 'Baustein bearbeiten' : 'Neuer Baustein'), h('label', { class: 'field' }, h('span', null, 'Name (wird gesprochen: „Baustein …“)'), name), h('label', { class: 'field' }, h('span', null, 'Text'), text), h('div', { class: 'row end' }, h('button', { class: 'btn', onclick: close }, 'Abbrechen'), h('button', { class: 'btn primary', onclick: async () => {
                try {
                    await call('dictionary.saveSnippet', { id: s?.id ?? 0, name: name.value, text: text.value });
                    close();
                    toast('Gespeichert', 'ok');
                    load();
                }
                catch (e) {
                    toast(errorText(e), 'error');
                }
            } }, 'Speichern')));
        dlg.addEventListener('keydown', e => { if (e.key === 'Escape')
            close(); });
        overlay.append(dlg);
        overlay.classList.add('show');
        name.focus();
    }
    function drawTerms(terms) {
        clear(termsBox);
        const input = h('input', { class: 'input', placeholder: 'Fachbegriff oder Eigenname …', 'aria-label': 'Neuer Fachbegriff' });
        const add = async () => {
            const term = input.value.trim();
            if (!term)
                return;
            try {
                await call('dictionary.saveTerm', { term });
                input.value = '';
                load();
            }
            catch (e) {
                toast(errorText(e), 'error');
            }
        };
        input.addEventListener('keydown', e => { if (e.key === 'Enter')
            add(); });
        termsBox.append(h('h2', null, 'Fachbegriffe & Eigennamen'), h('p', { class: 'muted small' }, 'Werden der Spracherkennung als Hinweis mitgegeben und von der KI exakt übernommen.'), h('div', { class: 'row' }, input, h('button', { class: 'btn primary', onclick: add }, 'Hinzufügen')), h('div', { class: 'chips' }, terms.length === 0 ? h('span', { class: 'muted' }, 'Noch keine Begriffe.') : null, terms.map(t => h('span', { class: 'chip' }, t.term, h('button', { class: 'chip-x', 'aria-label': `${t.term} entfernen`, onclick: async () => { await call('dictionary.deleteTerm', { id: t.id }); load(); } }, '✕')))));
    }
    load();
    return () => { };
}
