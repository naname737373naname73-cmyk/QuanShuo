import type { AppSettings, LocalAsrProfileEntry, LocalModelStatus } from '../bridge/types';
import { element } from '../utils/html';
import { LOCAL_MODELS } from './localModelCatalog';

/** 所有服务文字用 textContent；缓存按地址与配置名匹配，不暗示模型仍在运行。 */
export function renderLocalModel(panel: HTMLElement, status: LocalModelStatus | null, endpoint: string, model: string): void {
  panel.replaceChildren();
  const matching = status?.endpoint === endpoint.trim() && status?.model === model.trim() ? status : null;
  const info = matching?.connected ? matching.modelInfo : null;
  panel.dataset.known = info?.name ? 'true' : 'false';
  const head = element('div', 'local-model-heading');
  head.append(element('h4', undefined, '当前本地模型'));
  const refresh = element('button', 'link-button', '测试连接 · 更新资料') as HTMLButtonElement;
  refresh.dataset.action = 'test-local-model';
  head.append(refresh);
  panel.append(head);
  if (info?.name) {
    panel.append(element('strong', 'local-model-name', info.name));
    const device = info.device?.toLowerCase() === 'cpu' ? 'CPU（处理器）' : info.device;
    panel.append(element('p', 'local-model-detail', [device || '设备未报告', info.runtime, info.precision].filter(Boolean).join(' · ')));
    const resources = element('dl', 'local-model-resources');
    resources.append(element('dt', undefined, '预计模型显存'), element('dd', undefined, memory(info.estimatedVramMb)),
      element('dt', undefined, '预计系统内存'), element('dd', undefined, memory(info.estimatedRamMb)));
    panel.append(resources);
    if (info.device?.toLowerCase() === 'cpu') panel.append(element('p', 'field-note', '模型在 CPU 上运行，不占独显显存，仍会使用系统内存和处理器。'));
  } else {
    panel.append(element('strong', 'local-model-name', matching?.connected ? '服务已连接，尚未报告模型' : '实际模型尚未确认'));
    panel.append(element('p', 'field-note', matching?.connected
      ? '请让安装 agent 补充模型资料，再点“测试连接”。旧服务仍可正常识别。'
      : matching ? '上次连接未成功。请启动本地服务后测试。' : '点击“测试连接”读取实际模型；打开设置不会启动或加载模型。'));
    panel.append(element('p', 'local-model-detail', model.trim() === 'default' || !model.trim()
      ? '配置：服务默认（default 不是模型名称）' : `配置名：${model.trim()}（未经服务确认）`));
  }
  const time = matching && new Date(matching.checkedAt);
  if (time && Number.isFinite(time.getTime())) panel.append(element('p', 'field-note', `上次测试：${time.toLocaleString('zh-CN', { hour12: false })}。资料由服务提供，不代表此刻仍在运行；资源数字是预算，非实时占用。`));
}

function memory(mb: number | null | undefined): string {
  if (typeof mb !== 'number' || !Number.isFinite(mb) || mb < 0) return '未提供';
  if (mb === 0) return '0 GB';
  return mb < 1024 ? `约 ${Math.round(mb)} MB` : `约 ${Number((mb / 1024).toFixed(1))} GB`;
}

/** 选型预算，非实测最低要求。依据与预算假设见 docs/local-asr-model-info-20261103.md。 */
export function localModelRecommendations(profiles: LocalAsrProfileEntry[] | null = null, current?: AppSettings | null, status?: LocalModelStatus | null): HTMLElement {
  const section = element('div', 'local-model-recommendations');
  section.id = 'local-model-recommendations';
  const heading = element('div', 'local-model-heading');
  const refresh = element('button', 'link-button', '刷新已接入模型') as HTMLButtonElement;
  refresh.dataset.action = 'refresh-local-profiles';
  heading.append(element('h4', undefined, '本地模型 · 安装与切换'), refresh);
  section.append(heading, element('p', 'field-note', '下面两个按钮会复制提示词。把①发给能操作电脑的 AI，看看是否适合；想装时再发②，让 AI 装好并连接圈说。完成后刷新列表，就能验证并切换。'));
  for (const { id, name, tag, budget, note, docs } of LOCAL_MODELS) {
    const entry = profiles?.find(item => item.id === id);
    const profile = entry?.profile;
    const sameRoute = !!profile && current?.speechProvider === 'local' && current.localSpeechEndpoint === profile.endpoint && current.localSpeechModel === profile.model;
    const selected = sameRoute && status?.connected === true && status.endpoint === profile!.endpoint && status.model === profile!.model &&
      status.modelInfo?.catalogId === id && status.modelInfo.name === profile!.expectedModelName;
    const item = element('article', 'local-model-option');
    item.dataset.modelId = id;
    item.dataset.current = String(selected);
    const heading = element('div', 'local-model-heading');
    heading.append(element('strong', undefined, name), element('span', 'local-model-tag', tag));
    const source = element('button', 'link-button', '官方资料') as HTMLButtonElement;
    source.dataset.action = 'local-model-docs';
    source.dataset.model = docs;
    const statusLabel = element('span', 'local-profile-status', selected ? '当前选用' : sameRoute ? '当前连接 · 身份待验证' : profile ? '已登记 · 切换时验证' : entry?.error ? '登记待修复' : entry ? '尚未接入' : '登记资料未读取');
    const controls = element('div', 'button-row local-model-actions');
    for (const [action, label] of [['copy-model-assessment', '① 看电脑适不适合'], ['copy-model-setup', '② 帮我装好']]) {
      const button = element('button', 'secondary-button', label) as HTMLButtonElement;
      button.dataset.action = action;
      button.dataset.modelId = id;
      controls.append(button);
    }
    const activate = element('button', 'secondary-button', selected ? '当前选用' : '验证并切换') as HTMLButtonElement;
    activate.dataset.action = 'switch-local-model';
    activate.dataset.modelId = id;
    activate.dataset.switchable = String(!!profile && !selected);
    activate.disabled = !profile || selected;
    controls.append(activate);
    const meta = element('div', 'local-model-heading');
    meta.append(statusLabel, source);
    item.append(heading, element('p', 'local-model-budget', budget), element('p', 'field-note', note), meta, controls);
    if (entry?.error) item.append(element('p', 'field-note', entry.error));
    section.append(item);
  }
  section.append(element('p', 'field-note', '以上是初步预留估算，不是你的电脑实测结果。是否适合，还要看其他软件占用了多少、模型怎样安装；让 AI 先检查即可。更大不一定更适合。'));
  return section;
}
