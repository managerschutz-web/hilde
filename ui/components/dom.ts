// Kleine DOM-Helfer ohne Framework.

type Child = Node | string | number | null | undefined | false;
type Props = Record<string, any> | null;

export function h<K extends keyof HTMLElementTagNameMap>(tag: K, props?: Props, ...children: (Child | Child[])[]): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v === undefined || v === null || v === false) continue;
      if (k === 'class') el.className = String(v);
      else if (k === 'dataset') Object.assign(el.dataset, v);
      // CSSOM statt style-Attribut: erlaubt unter der strengen Content-Security-Policy
      else if (k === 'style') el.style.cssText = String(v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === 'value' && 'value' in el) (el as any).value = v;
      else if (k === 'checked' && 'checked' in el) (el as any).checked = !!v;
      else if (v === true) el.setAttribute(k, '');
      else el.setAttribute(k, String(v));
    }
  }
  for (const c of children.flat()) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

export function clear(el: Element): void { while (el.firstChild) el.removeChild(el.firstChild); }

export function toast(text: string, kind: 'info' | 'ok' | 'error' = 'info', ms = 3500): void {
  const box = document.getElementById('toasts')!;
  const t = h('div', { class: `toast ${kind}` }, text);
  box.append(t);
  window.setTimeout(() => { t.classList.add('out'); window.setTimeout(() => t.remove(), 300); }, ms);
}

export function errorText(e: unknown): string {
  const any = e as any;
  return any?.errorId ? `${any.message} (${any.errorId})` : (any?.message ?? String(e));
}

export function fmtDate(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export function fmtDuration(ms: number): string {
  const s = Math.round(ms / 1000);
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')} min`;
}

export function fmtBytes(b: number | null | undefined): string {
  if (b == null) return '–';
  if (b >= 1e9) return (b / 1e9).toLocaleString('de-DE', { maximumFractionDigits: 1 }) + ' GB';
  if (b >= 1e6) return (b / 1e6).toLocaleString('de-DE', { maximumFractionDigits: 0 }) + ' MB';
  return (b / 1e3).toLocaleString('de-DE', { maximumFractionDigits: 0 }) + ' KB';
}

export function debounce<A extends any[]>(fn: (...a: A) => void, ms: number): (...a: A) => void {
  let t = 0;
  return (...a: A) => { window.clearTimeout(t); t = window.setTimeout(() => fn(...a), ms); };
}

/** Modaler Bestätigungsdialog (tastaturbedienbar). */
export function confirmDialog(title: string, text: string, okLabel = 'OK', danger = false): Promise<boolean> {
  return new Promise(resolve => {
    const overlay = document.getElementById('overlay')!;
    const close = (v: boolean) => { dlg.remove(); overlay.classList.remove('show'); resolve(v); };
    const ok = h('button', { class: danger ? 'btn danger' : 'btn primary', onclick: () => close(true) }, okLabel);
    const dlg = h('div', { class: 'dialog', role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
      h('h2', null, title), h('p', null, text),
      h('div', { class: 'row end' }, h('button', { class: 'btn', onclick: () => close(false) }, 'Abbrechen'), ok));
    dlg.addEventListener('keydown', e => { if (e.key === 'Escape') close(false); });
    overlay.append(dlg);
    overlay.classList.add('show');
    ok.focus();
  });
}

export const MODE_LABEL: Record<string, string> = { Original: 'Original', Clean: 'Bereinigt', Professional: 'Professionell' };
