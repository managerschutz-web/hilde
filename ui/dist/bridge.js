// Brücke zur .NET-Seite (WebView2). Anfrage/Antwort mit ID, dazu Push-Ereignisse.
export class BridgeError extends Error {
    errorId;
    constructor(message, errorId) {
        super(message);
        this.errorId = errorId;
    }
}
const pending = new Map();
const listeners = new Map();
let nextId = 1;
function host() {
    const w = window.chrome?.webview;
    if (!w)
        throw new BridgeError('HILDE-Oberfläche läuft außerhalb der App.');
    return w;
}
export function initBridge() {
    host().addEventListener('message', (e) => {
        const msg = e.data;
        if (msg && typeof msg.id === 'number' && pending.has(msg.id)) {
            const p = pending.get(msg.id);
            pending.delete(msg.id);
            if (msg.ok)
                p.resolve(msg.result);
            else
                p.reject(new BridgeError(msg.error ?? 'Unbekannter Fehler', msg.errorId ?? undefined));
        }
        else if (msg && typeof msg.event === 'string') {
            listeners.get(msg.event)?.forEach(l => { try {
                l(msg.data);
            }
            catch (err) {
                console.error(err);
            } });
        }
    });
}
export function call(method, params, timeoutMs = 120000) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
        const timer = window.setTimeout(() => {
            if (pending.delete(id))
                reject(new BridgeError('Zeitüberschreitung bei ' + method));
        }, timeoutMs);
        pending.set(id, {
            resolve: v => { clearTimeout(timer); resolve(v); },
            reject: e => { clearTimeout(timer); reject(e); },
        });
        host().postMessage({ id, method, params: params ?? {} });
    });
}
export function on(event, listener) {
    if (!listeners.has(event))
        listeners.set(event, new Set());
    listeners.get(event).add(listener);
    return () => listeners.get(event)?.delete(listener);
}
