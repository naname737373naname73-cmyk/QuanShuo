import type { Bridge } from './types';

interface WebViewMessage {
  id?: string;
  type: string;
  payload?: unknown;
  error?: string;
}

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: MessageEvent<WebViewMessage>) => void): void;
  removeEventListener(type: 'message', listener: (event: MessageEvent<WebViewMessage>) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebView };
  }
}

export class NativeBridge implements Bridge {
  private pending = new Map<string, { resolve: (value: unknown) => void; reject: (reason: Error) => void }>();
  private listeners = new Map<string, Set<(payload: unknown) => void>>();
  private sequence = 0;

  constructor(private webview: WebView) {
    this.onMessage = this.onMessage.bind(this);
    this.webview.addEventListener('message', this.onMessage);
  }

  request<T>(type: string, payload?: unknown): Promise<T> {
    const id = `web-${++this.sequence}`;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
      this.webview.postMessage({ id, type, payload });
    });
  }

  subscribe(type: string, listener: (payload: unknown) => void): () => void {
    const listeners = this.listeners.get(type) ?? new Set();
    listeners.add(listener);
    this.listeners.set(type, listeners);
    return () => listeners.delete(listener);
  }

  dispose(): void {
    this.webview.removeEventListener('message', this.onMessage);
    this.pending.clear();
  }

  private onMessage(event: MessageEvent<WebViewMessage>): void {
    const message = event.data;
    if (message.id) {
      const request = this.pending.get(message.id);
      if (request) {
        this.pending.delete(message.id);
        if (message.error) request.reject(new Error(message.error));
        else request.resolve(message.payload);
      }
      return;
    }
    this.listeners.get(message.type)?.forEach((listener) => listener(message.payload));
  }
}
