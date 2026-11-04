import type { Skin } from '../app/skin';

export type TranscriptionStatus = 'not-requested' | 'recording' | 'completed' | 'incomplete';

export interface QuanShuoRecord {
  id: string;
  timestamp: string;
  appName: string;
  windowTitle: string;
  /** 只说话、没有截图的记录为空字符串。 */
  imagePath: string;
  markdownPath: string;
  audioPath?: string;
  text: string;
  status: TranscriptionStatus;
  imageUrl?: string;
}

export type CopyOperation = 'ai' | 'image-path' | 'markdown-path' | 'text' | 'image';

export interface LocalModelInfo {
  name: string;
  runtime?: string | null;
  device?: string | null;
  precision?: string | null;
  estimatedVramMb?: number | null;
  estimatedRamMb?: number | null;
  catalogId?: string | null;
}

export interface LocalAsrProfile {
  schemaVersion: number;
  id: string;
  endpoint: string;
  model: string;
  expectedModelName: string;
}

export interface LocalAsrProfileEntry {
  id: string;
  profile: LocalAsrProfile | null;
  error: string | null;
}

export interface LocalModelStatus {
  endpoint: string;
  model: string;
  connected: boolean;
  checkedAt: string;
  modelInfo?: LocalModelInfo | null;
}

export interface AppSettings {
  settingsRevision?: number;
  hotkey: string;
  speechHotkey: string;
  dataDirectory: string;
  freeformFillColor: string;
  deleteAudioAfterSuccess: boolean;
  autoStart: boolean;
  quietMode: boolean;
  autoSpeakAfterSelection: boolean;
  speechModel: string;
  speechProvider?: 'dashscope' | 'local';
  localSpeechEndpoint?: string;
  localSpeechModel?: string;
  speechEndpoint: string;
  speechLanguage: string;
  microphoneDeviceId?: string;
  vocabularyId?: string;
  fixedDayPeriod?: number;
  skin?: Skin;
}

export interface Bridge {
  request<T>(type: string, payload?: unknown): Promise<T>;
  subscribe(type: string, listener: (payload: unknown) => void): () => void;
}
