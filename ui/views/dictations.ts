import { call, on } from '../bridge.js';
import { h, clear, toast, errorText, fmtDate, fmtDuration, debounce, confirmDialog, MODE_LABEL } from '../components/dom.js';

interface Item {
  id: string; title: string; createdUtc: string; status: string; mode: string; durationMs: number;
  targetApp?: string; errorId?: string; preview: string; hasAudio: boolean; language: string;
}

const STATUS_LABEL: Record<string, string> = {
  done: 'Eingefügt', recovered: 'Wiederhergestellt', failed: 'Fehlgeschlagen', processing: 'In Arbeit', recording: 'Aufnahme',
};

export function renderDictations(root: HTMLElement): () => void {
  let search = '';
  let selected: string | null = null;
  const list = h('div', { class: 'list', role: 'listbox', 'aria-label': 'Diktate' });
  const detail = h('section', { class: 'detail', 'aria-live': 'polite' });
  const searchBox = h('input', {
    type: 'search', class: 'input', placeholder: 'Archiv durchsuchen …', 'aria-label': 'Archiv durchsuchen',
    oninput: debounce((e: Event) => { search = (e.target as HTMLInputElement).value; load(); }, 250),
  });

  clear(root);
  root.append(
    h('header', { class: 'view-head' },
      h('div', null, h('h1', null, 'Diktate'), h('p', { class: 'muted' }, 'Taste halten, sprechen, loslassen – der Text erscheint im aktiven Feld. Doppeltippen = freihändig, Esc = verwerfen.')),
      searchBox),
    h('div', { class: 'split' }, list, detail));

  async function load(): Promise<void> {
    try {
      const items = await call<Item[]>('transcripts.list', { search, limit: 300 });
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
        },
          h('div', { class: 'item-top' },
            h('span', { class: 'item-title' }, it.title || 'Diktat'),
            h('span', { class: `badge s-${it.status}` }, STATUS_LABEL[it.status] ?? it.status)),
          h('div', { class: 'item-preview' }, it.preview || '—'),
          h('div', { class: 'item-meta' }, `${fmtDate(it.createdUtc)} · ${fmtDuration(it.durationMs)} · ${MODE_LABEL[it.mode] ?? it.mode}`));
        list.append(card);
      }
      if (!selected && items.length) select(items[0].id);
    } catch (e) { toast(errorText(e), 'error'); }
  }

  async function select(id: string): Promise<void> {
    selected = id;
    try {
      const { record: r, hasAudio, error, audioUrl, segments, keepAudio } = await call<any>('transcripts.get', { id });
      const info = await call<any>('app.info');
      type Variant = 'final' | 'clean' | 'raw';
      const LABEL: Record<Variant, string> = { final: 'Endfassung', clean: 'Bereinigt', raw: 'Original' };
      const textOf = (v: Variant) => v === 'final' ? r.finalText : v === 'clean' ? r.cleanText : r.rawText;
      let variant: Variant = 'final';
      let sideBySide = false;
      let compareWith: Variant = 'raw';

      const title = h('input', { class: 'input title-input', value: r.title, 'aria-label': 'Titel' });
      const work = h('div', { class: 'work' });
      const tabs = h('div', { class: 'tabs', role: 'tablist' });
      const saveTitle = debounce(async () => {
        try { await call('transcripts.update', { id, title: title.value }); } catch (e) { toast(errorText(e), 'error'); }
      }, 600);
      const saveFinal = debounce(async () => {
        try { await call('transcripts.update', { id, finalText: r.finalText }); } catch (e) { toast(errorText(e), 'error'); }
      }, 600);
      title.addEventListener('input', saveTitle);

      const editor = (v: Variant) => {
        const t = h('textarea', { class: 'textarea', 'aria-label': LABEL[v], spellcheck: 'true' }) as HTMLTextAreaElement;
        t.value = textOf(v);
        t.readOnly = v !== 'final';
        if (v === 'final') {
          t.addEventListener('input', () => { r.finalText = t.value; saveFinal(); });
          // Lernen aus Korrekturen: beim Verlassen des Feldes Änderungen auswerten
          let atFocus = t.value;
          t.addEventListener('focus', () => { atFocus = t.value; });
          t.addEventListener('blur', () => { if (t.value !== atFocus) suggest(atFocus, t.value); });
        }
        return t;
      };
      const pane = (label: Node | string, body: Node, extra?: Node | null) =>
        h('div', { class: 'pane' }, h('div', { class: 'pane-head' }, label, extra ?? null), body);

      const suggestBox = h('div', { class: 'suggest' });
      const suggest = async (before: string, after: string) => {
        try {
          const list = await call<{ from: string; to: string }[]>('transcripts.suggestRules', { before, after });
          for (const sg of list) {
            const row = h('div', { class: 'banner learn' },
              h('span', null, 'Als Regel speichern? ', h('code', null, sg.from), ' → ', h('code', null, sg.to)),
              h('span', { class: 'spacer' }),
              h('button', { class: 'btn small primary', onclick: async () => {
                await call('dictionary.saveRule', { id: 0, pattern: sg.from, replacement: sg.to, wholeWord: true, caseSensitive: false, enabled: true, sortOrder: 0 });
                toast('Regel gespeichert – gilt ab dem nächsten Diktat', 'ok'); row.remove();
              } }, 'Speichern'),
              h('button', { class: 'btn small', onclick: () => row.remove() }, 'Nein'));
            suggestBox.append(row);
          }
        } catch { /* Vorschläge sind optional */ }
      };

      // Audio-Wiedergabe mit anklickbaren Abschnitten
      const audioBox = h('div', { class: 'audio' });
      if (audioUrl) {
        const player = h('audio', { controls: true, preload: 'metadata', src: audioUrl }) as HTMLAudioElement;
        let stopAt = Infinity;
        player.addEventListener('timeupdate', () => { if (player.currentTime >= stopAt) { player.pause(); stopAt = Infinity; } });
        const segs = Array.isArray(segments) ? segments as { s: number; e: number; t: string }[] : [];
        audioBox.append(h('div', { class: 'audio-head' }, h('strong', null, 'Aufnahme'), player));
        if (segs.length) audioBox.append(h('div', { class: 'segs' }, segs.map(sg => h('button', {
            class: 'seg', title: 'Diese Stelle abspielen', onclick: () => { player.currentTime = sg.s; stopAt = sg.e + 0.15; player.play(); }
          }, h('span', { class: 'seg-time' }, fmtClock(sg.s)), sg.t))));
      } else if (!keepAudio) {
        audioBox.append(h('div', { class: 'muted small' }, 'Keine Aufnahme gespeichert. Für die Wiedergabe in den Einstellungen „Audio behalten“ einschalten.'));
      }

      const render = () => {
        clear(work);
        tabs.querySelectorAll('button').forEach(b => b.setAttribute('aria-selected', String(!sideBySide && b.dataset.v === variant)));
        sideBtn.setAttribute('aria-pressed', String(sideBySide));
        if (!sideBySide) { work.append(editor(variant)); return; }
        const sel = h('select', { class: 'input mini', 'aria-label': 'Vergleichen mit', onchange: (e: Event) => { compareWith = (e.target as HTMLSelectElement).value as Variant; render(); } },
          (['raw', 'clean'] as Variant[]).map(v => h('option', { value: v, selected: v === compareWith }, LABEL[v])));
        work.append(h('div', { class: 'compare' }, pane(sel, editor(compareWith)), pane('Endfassung (bearbeitbar)', editor('final'))));
      };

      for (const v of ['final', 'clean', 'raw'] as Variant[]) {
        tabs.append(h('button', { class: 'tab', role: 'tab', dataset: { v }, onclick: () => { variant = v; sideBySide = false; render(); } }, LABEL[v]));
      }
      const sideBtn = h('button', { class: 'tab', 'aria-pressed': 'false', title: 'Original und Endfassung nebeneinander',
        onclick: () => { sideBySide = !sideBySide; render(); } }, '⇆ Nebeneinander');

      // KI-Nachbearbeitung mit Vorher/Nachher
      const rewrite = async (kind: 'shorten' | 'clarify') => {
        const source: Variant = sideBySide ? 'final' : variant;
        const before = textOf(source);
        if (!before.trim()) return;
        const proposal = h('textarea', { class: 'textarea', 'aria-label': 'Vorschlag', spellcheck: 'true' }) as HTMLTextAreaElement;
        proposal.value = '';
        proposal.placeholder = 'HILDE überarbeitet … (das kann einige Sekunden dauern)';
        proposal.readOnly = true;
        const notes = h('div', { class: 'notes' });
        const actions = h('div', { class: 'row' });
        const kindLabel = kind === 'shorten' ? 'Gekürzt' : 'Sachlich & klar';
        clear(work);
        work.append(h('div', { class: 'compare' },
          pane(`Vorher · ${LABEL[source]}`, (() => { const t = editor(source); t.readOnly = true; return t; })()),
          pane(h('span', null, `Vorschlag · ${kindLabel}`), proposal, h('span', { class: 'spinner', 'aria-hidden': 'true' }))), notes, actions);
        try {
          const res = await call<any>('transcripts.rewrite', { id, kind, text: before }, 10 * 60000);
          proposal.value = res.text;
          proposal.readOnly = false;
          work.querySelector('.spinner')?.remove();
          const ratio = Math.round((1 - res.text.length / Math.max(1, before.length)) * 100);
          notes.append(h('div', { class: 'muted small' },
            `${res.seconds.toFixed(1)} s · ${ratio > 0 ? ratio + ' % kürzer' : Math.abs(ratio) + ' % länger'}`));
          if (res.notes.length) notes.append(h('div', { class: 'banner warn' }, h('strong', null, 'Bitte prüfen: '), res.notes.join(' · ')));
          clear(actions);
          actions.append(
            h('button', { class: 'btn primary', onclick: async () => {
              r.finalText = proposal.value;
              await call('transcripts.update', { id, finalText: r.finalText });
              toast('Als Endfassung übernommen', 'ok');
              variant = 'final'; sideBySide = false; render(); load();
            } }, 'Übernehmen'),
            h('button', { class: 'btn', onclick: async () => {
              await navigator.clipboard.writeText(proposal.value).catch(() => { });
              toast('Kopiert', 'ok');
            } }, 'Kopieren'),
            h('button', { class: 'btn', onclick: () => rewrite(kind) }, 'Nochmal'),
            h('span', { class: 'spacer' }),
            h('button', { class: 'btn', onclick: () => render() }, 'Verwerfen'));
        } catch (e) {
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
      const parts: (Node | null)[] = [
        title,
        h('div', { class: 'meta' },
          `${fmtDate(r.createdUtc)} · ${fmtDuration(r.durationMs)} · ${r.language?.toUpperCase() ?? ''} · ${MODE_LABEL[r.mode] ?? r.mode}`,
          r.targetApp ? ` · ${r.targetApp}` : '',
          r.fallbackReason ? h('span', { class: 'note' }, ' · ' + r.fallbackReason) : ''),
        error ? h('div', { class: 'banner error' }, `${error} (${r.errorId})`) : null,
        h('div', { class: 'toolbar' }, tabs, sideBtn, h('span', { class: 'spacer' }), ...aiButtons),
        work, suggestBox, audioBox,
        h('div', { class: 'row' },
          h('button', { class: 'btn primary', onclick: async () => { await call('transcripts.copy', { id, variant: sideBySide ? 'final' : variant }); toast('Kopiert', 'ok'); } }, 'Kopieren'),
          h('button', { class: 'btn', onclick: () => exportAs(id, 'txt') }, 'Als TXT'),
          h('button', { class: 'btn', onclick: () => exportAs(id, 'md') }, 'Als Markdown'),
          h('button', { class: 'btn', onclick: () => exportAs(id, 'json') }, 'Als JSON'),
          (r.status === 'failed' && hasAudio)
            ? h('button', { class: 'btn', onclick: async () => { await call('transcripts.reprocess', { id }); toast('Wird erneut verarbeitet …'); } }, 'Erneut verarbeiten')
            : null,
          h('span', { class: 'spacer' }),
          h('button', {
            class: 'btn danger-ghost', onclick: async () => {
              if (!await confirmDialog('Diktat löschen?', 'Text und ggf. Audio werden vollständig gelöscht.', 'Löschen', true)) return;
              await call('transcripts.delete', { id }); selected = null; toast('Gelöscht', 'ok'); load();
            }
          }, 'Löschen'))];
      detail.append(...parts.filter((p): p is Node => p !== null));
      render();
      highlight(id);
    } catch (e) { toast(errorText(e), 'error'); }
  }

  function fmtClock(sec: number): string {
    const m = Math.floor(sec / 60), s = Math.floor(sec % 60);
    return `${m}:${String(s).padStart(2, '0')}`;
  }

  function highlight(id: string): void {
    list.querySelectorAll<HTMLElement>('.item').forEach(n => {
      const on = n.dataset.id === id;
      n.classList.toggle('active', on);
      n.setAttribute('aria-selected', String(on));
    });
  }

  async function exportAs(id: string, format: string): Promise<void> {
    try {
      const r = await call<any>('transcripts.export', { id, format });
      if (r?.saved) toast('Gespeichert: ' + r.path, 'ok');
    } catch (e) { toast(errorText(e), 'error'); }
  }

  const off = on('transcript', () => load());
  load();
  return off;
}
