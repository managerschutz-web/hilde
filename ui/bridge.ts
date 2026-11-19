// Brücke zur .NET-Seite (WebView2). Anfrage/Antwort mit ID, dazu Push-Ereignisse.

interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
}

declare global {
  interface Window { chrome?: { webview?: WebViewHost } }
}

export class BridgeError extends Error {
  constructor(message: string, public readonly errorId?: string) { super(message); }
}

type Pending = { resolve: (v: unknown) => void; reject: (e: unknown) => void };
type Listener = (data: any) => void;

const pending = new Map<number, Pending>();
const listeners = new Map<string, Set<Listener>>();
let nextId = 1;

function host(): WebViewHost {
  const w = window.chrome?.webview;
  if (!w) throw new BridgeError('HILDE-Oberfläche läuft außerhalb der App.');
  return w;
}

export function initBridge(): void {
  host().addEventListener('message', (e: MessageEvent) => {
    const msg = e.data as any;
    if (msg && typeof msg.id === 'number' && pending.has(msg.id)) {
      const p = pending.get(msg.id)!;
      pending.delete(msg.id);
      if (msg.ok) p.resolve(msg.result);
      else p.reject(new BridgeError(msg.error ?? 'Unbekannter Fehler', msg.errorId ?? undefined));
    } else if (msg && typeof msg.event === 'string') {
      listeners.get(msg.event)?.forEach(l => { try { l(msg.data); } catch (err) { console.error(err); } });
    }
  });
}

export function call<T = any>(method: string, params?: unknown, timeoutMs = 120000): Promise<T> {
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      if (pending.delete(id)) reject(new BridgeError('Zeitüberschreitung bei ' + method));
    }, timeoutMs);
    pending.set(id, {
      resolve: v => { clearTimeout(timer); resolve(v as T); },
      reject: e => { clearTimeout(timer); reject(e); },
    });
    host().postMessage({ id, method, params: params ?? {} });
  });
}

export function on(event: string, listener: Listener): () => void {
  if (!listeners.has(event)) listeners.set(event, new Set());
  listeners.get(event)!.add(listener);
  return () => listeners.get(event)?.delete(listener);
}
