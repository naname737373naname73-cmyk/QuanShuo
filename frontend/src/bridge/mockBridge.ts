import type { AppSettings, Bridge, LocalAsrProfileEntry, LocalModelStatus, QuanShuoRecord } from './types';
import { LOCAL_MODELS, targetedLocalPrompt } from '../components/localModelCatalog';
import localAsrPrompt from '../../../docs/local-asr-agent-prompt.md?raw';
import localAsrAssessmentPrompt from '../../../docs/local-asr-assessment-prompt.md?raw';

const sampleTexts = [
  '这个按钮的间距太大了，我想和上面那行对齐。',
  '这个参数调大之后是什么效果？会不会影响渲染速度？',
  '这里的颜色太重了，换成更浅的蓝，和背景拉开层次。',
  '把这个滑块的数值告诉我，我想对比一下调整前后的效果。',
  '这张图的边缘有些模糊，能不能保持原始像素输出？',
  '我想把这里改成两列，左边放图片，右边放说明。',
  '这个开关是做什么用的，关掉以后会影响保存吗？',
  '当前窗口里哪个参数控制涟漪的扩散速度？',
];

/** 模拟一张真实的界面截图，让舞台和倒影在开发时有东西可看。 */
function screenshot(index: number): string {
  const hue = [222, 196, 262, 168][index % 4];
  const dark = index % 2 === 0;
  const bg = dark ? `hsl(${hue} 26% 13%)` : `hsl(${hue} 30% 96%)`;
  const panel = dark ? `hsl(${hue} 24% 18%)` : '#ffffff';
  const ink = dark ? `hsl(${hue} 30% 86%)` : `hsl(${hue} 34% 22%)`;
  const faint = dark ? `hsl(${hue} 18% 36%)` : `hsl(${hue} 18% 82%)`;
  const accent = `hsl(${hue} 70% ${dark ? 66 : 48}%)`;
  const rows = Array.from({ length: 6 }, (_, row) => `<rect x="316" y="${156 + row * 62}" width="${560 - (row * 37 + index * 19) % 180}" height="12" rx="6" fill="${faint}"/><rect x="316" y="${178 + row * 62}" width="${300 - (row * 53) % 110}" height="9" rx="4.5" fill="${faint}" opacity=".6"/>`).join('');
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="${index % 3 === 1 ? 720 : 800}" viewBox="0 0 1280 ${index % 3 === 1 ? 720 : 800}"><rect width="1280" height="800" fill="${bg}"/><rect width="1280" height="56" fill="${panel}"/><circle cx="32" cy="28" r="7" fill="${accent}"/><rect x="54" y="20" width="150" height="16" rx="8" fill="${ink}"/><rect x="1080" y="16" width="170" height="24" rx="12" fill="${accent}"/><rect x="24" y="80" width="248" height="696" rx="16" fill="${panel}"/>${[0, 1, 2, 3, 4, 5].map((row) => `<rect x="48" y="${112 + row * 52}" width="${184 - row * 13}" height="14" rx="7" fill="${row === index % 6 ? accent : faint}"/>`).join('')}<rect x="292" y="80" width="964" height="696" rx="16" fill="${panel}"/><rect x="316" y="108" width="280" height="22" rx="11" fill="${ink}"/>${rows}<rect x="316" y="548" width="420" height="10" rx="5" fill="${faint}"/><rect x="316" y="548" width="${180 + index * 30}" height="10" rx="5" fill="${accent}"/><circle cx="${496 + index * 30}" cy="553" r="12" fill="${accent}"/><rect x="316" y="600" width="148" height="44" rx="12" fill="${accent}"/><rect x="480" y="600" width="148" height="44" rx="12" fill="none" stroke="${faint}" stroke-width="2"/></svg>`;
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
}

export class MockBridge implements Bridge {
  private records: QuanShuoRecord[];
  private deleted: QuanShuoRecord[][] = [];
  private localModelStatus: LocalModelStatus | null = null;
  private localProfiles: LocalAsrProfileEntry[] = LOCAL_MODELS.map(item => ({ id: item.id, error: null,
    profile: item.id === 'paraformer-streaming-int8' ? { schemaVersion: 1, id: item.id, endpoint: 'ws://127.0.0.1:8765/asr', model: 'default', expectedModelName: 'Paraformer 中英双语 · INT8（演示）' } : null,
  }));
  private listeners = new Map<string, Set<(payload: unknown) => void>>();
  private settings: AppSettings = {
    hotkey: 'Ctrl+Alt+G',
    speechHotkey: 'Ctrl+Alt+H',
    dataDirectory: 'C:\\Users\\Example\\Documents\\QuanShuo',
    freeformFillColor: '#FFFFFF',
    deleteAudioAfterSuccess: true,
    autoStart: true,
    quietMode: false,
    autoSpeakAfterSelection: false,
    speechModel: 'qwen-audio-3.1-asr-flash-message',
    speechEndpoint: 'wss://dashscope.aliyuncs.com/api-ws/v1/inference',
    speechLanguage: 'zh',
    skin: new URLSearchParams(location.search).get('skin') === 'crabapple' ? 'crabapple' : 'starlake',
  };

  constructor(recordCount = 5) {
    // 模拟记录都落在今天，星座才有内容可看
    const now = Date.now();
    const midnight = new Date(now).setHours(0, 2, 0, 0);
    const span = Math.max(1, Math.min(47 * 60_000, (now - midnight) / Math.max(1, recordCount)));
    this.records = Array.from({ length: recordCount }, (_, index) => {
      const date = new Date(now - index * span);
      const id = date.toISOString().slice(11, 19).replaceAll(':', '');
      return {
        id: `mock-${index}`,
        timestamp: date.toISOString(),
        appName: ['Figma', 'Visual Studio Code', 'Microsoft Edge'][index % 3],
        windowTitle: '星湖场景与交互设计',
        imagePath: index === 6 ? '' : `C:\\Users\\Example\\Documents\\QuanShuo\\${date.toISOString().slice(0, 10)}\\${id}.png`,
        markdownPath: `C:\\Users\\Example\\Documents\\QuanShuo\\${date.toISOString().slice(0, 10)}\\${id}.md`,
        text: sampleTexts[index % sampleTexts.length],
        status: index === 3 ? 'incomplete' : index === 1 ? 'not-requested' : 'completed',
        imageUrl: index === 6 ? undefined : screenshot(index),
      } satisfies QuanShuoRecord;
    });
  }

  async request<T>(type: string, payload?: unknown): Promise<T> {
    await new Promise((resolve) => setTimeout(resolve, 35));
    switch (type) {
      case 'records.query': {
        const query = (payload as { query?: string } | undefined)?.query?.toLowerCase() ?? '';
        return this.records.filter((record) => !query || `${record.text} ${record.appName} ${record.windowTitle}`.toLowerCase().includes(query)) as T;
      }
      case 'record.update': {
        const update = payload as { id: string; text: string };
        console.debug(`mock:record.update ${update.text}`);
        if (update.text === '模拟保存失败') throw new Error('模拟保存失败');
        this.records = this.records.map((record) => record.id === update.id ? { ...record, text: update.text } : record);
        return undefined as T;
      }
      case 'record.delete': {
        const { id } = payload as { id: string };
        const deleted = this.records.find((record) => record.id === id);
        if (deleted) this.deleted.push([deleted]);
        this.records = this.records.filter((record) => record.id !== id);
        return undefined as T;
      }
      case 'records.delete': {
        const ids = new Set((payload as { ids: string[] }).ids);
        this.deleted.push(this.records.filter((record) => ids.has(record.id)));
        this.records = this.records.filter((record) => !ids.has(record.id));
        return undefined as T;
      }
      case 'record.undo': {
        const restored = this.deleted.pop() ?? [];
        this.records = [...this.records, ...restored].sort((first, second) => second.timestamp.localeCompare(first.timestamp));
        return restored[0] as T;
      }
      case 'settings.get':
        return this.settings as T;
      case 'clipboard.copy': {
        const copy = payload as { text?: string } | undefined;
        if (copy?.text && navigator.clipboard) await navigator.clipboard.writeText(copy.text).catch(() => undefined);
        return undefined as T;
      }
      case 'record.retry': {
        const { id } = payload as { id: string };
        const record = this.records.find((candidate) => candidate.id === id);
        return { ...record, status: 'completed' } as T;
      }
      case 'settings.update':
        this.settings = { ...payload as AppSettings, settingsRevision: (this.settings.settingsRevision ?? 0) + 1 };
        return this.settings as T;
      case 'record.thumbnail':
      case 'record.image': {
        const { id } = payload as { id: string };
        return this.records.find((record) => record.id === id)?.imageUrl as T;
      }
      case 'audio.devices':
        return [{ index: 0, name: '模拟麦克风' }] as T;
      case 'connection.test': {
        const configuration = (payload as { settings?: AppSettings } | undefined)?.settings ?? this.settings;
        if (configuration.speechProvider === 'local') this.localModelStatus = {
          endpoint: configuration.localSpeechEndpoint!, model: configuration.localSpeechModel!, connected: true, checkedAt: new Date().toISOString(),
          modelInfo: { name: 'Paraformer 中英双语 · INT8（演示）', runtime: 'sherpa-onnx', device: 'cpu', precision: 'INT8', estimatedVramMb: 0, estimatedRamMb: 1024, catalogId: 'paraformer-streaming-int8' },
        };
        return { ok: true, message: configuration.speechProvider === 'local' ? '模拟环境：本地服务静音协议测试通过；未验证实际模型。' : '模拟环境：麦克风与识别链路正常。', modelStatus: configuration.speechProvider === 'local' ? this.localModelStatus : null } as T;
      }
      case 'localAsr.modelInfo': return this.localModelStatus as T;
      case 'localAsr.profiles': return this.localProfiles as T;
      case 'localAsr.switchProfile': {
        const request = payload as { id: string; profile: LocalAsrProfileEntry['profile']; expectedCurrent: AppSettings };
        const profile = this.localProfiles.find(item => item.id === request.id)?.profile;
        if (!profile || JSON.stringify(profile) !== JSON.stringify(request.profile)) throw new Error('模型登记资料已变化，请刷新列表。');
        this.settings = { ...this.settings, settingsRevision: (this.settings.settingsRevision ?? 0) + 1, speechProvider: 'local', localSpeechEndpoint: profile.endpoint, localSpeechModel: profile.model };
        const result = await this.request<{ modelStatus: LocalModelStatus }>('connection.test', { settings: this.settings });
        return { settings: this.settings, modelStatus: result.modelStatus, message: `模拟环境：已切换到 ${profile.expectedModelName}；未验证真实模型。` } as T;
      }
      case 'localAsr.openModelDocs': {
        const model = (payload as { model: string }).model;
        const url = model === 'sherpa' ? 'https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-paraformer/paraformer-models.html' : model === 'qwen' ? 'https://github.com/QwenLM/Qwen3-ASR' : null;
        if (!url) throw new Error('没有这份模型资料。');
        window.open(url, '_blank', 'noopener,noreferrer');
        return undefined as T;
      }
      case 'localAsr.copySetupPrompt':
        await navigator.clipboard.writeText(targetedLocalPrompt(localAsrPrompt, (payload as { modelId?: string } | undefined)?.modelId, false));
        return undefined as T;
      case 'localAsr.copyAssessmentPrompt':
        await navigator.clipboard.writeText(targetedLocalPrompt(localAsrAssessmentPrompt, (payload as { modelId?: string } | undefined)?.modelId, true));
        return undefined as T;
      case 'scene.day':
      case 'window.minimize':
      case 'window.toggleMaximize':
      case 'window.close':
      case 'window.keepOpen':
      case 'secret.set':
      case 'system.microphoneSettings':
      case 'record.drag':
      case 'capture.test':
      case 'speech.open':
      case 'onboarding.complete':
        return undefined as T;
      default:
        throw new Error(`模拟桥接不认识消息：${type}`);
    }
  }

  subscribe(type: string, listener: (payload: unknown) => void): () => void {
    const listeners = this.listeners.get(type) ?? new Set();
    listeners.add(listener);
    this.listeners.set(type, listeners);
    return () => listeners.delete(listener);
  }
}
