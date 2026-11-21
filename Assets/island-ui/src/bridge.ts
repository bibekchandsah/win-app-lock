export type Envelope = {
  v: 1;
  type: string;
  id?: string;
  payload?: unknown;
};

type WebViewHost = {
  postMessage: (message: unknown) => void;
  addEventListener: (type: 'message', handler: (event: MessageEvent) => void) => void;
};

function host(): WebViewHost | undefined {
  const chrome = (window as { chrome?: { webview?: WebViewHost } }).chrome;
  return chrome?.webview;
}

export function post(type: string, payload?: unknown, id?: string): void {
  const envelope: Envelope = { v: 1, type, payload };
  if (id) envelope.id = id;
  host()?.postMessage(envelope);
}

export function onMessage(handler: (message: Envelope) => void): void {
  host()?.addEventListener('message', (event) => {
    const data = event.data as Envelope | undefined;
    if (!data || data.v !== 1 || typeof data.type !== 'string') return;
    handler(data);
  });
}
