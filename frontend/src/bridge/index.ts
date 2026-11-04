import { MockBridge } from './mockBridge';
import { NativeBridge } from './nativeBridge';
import type { Bridge } from './types';

export function createBridge(query: URLSearchParams): Bridge {
  if (window.chrome?.webview) return new NativeBridge(window.chrome.webview);
  return new MockBridge(Number(query.get('records') ?? 5));
}
