export const LOCAL_MODELS = [
  { id: 'paraformer-streaming-int8', name: 'Paraformer Streaming · INT8', tag: '省显存', budget: '模型显存 0 GB · 系统内存先留约 1–2 GB', note: '用电脑的处理器识别，不占独显显存。可以先试中文夹英文的日常说话。', docs: 'sherpa' },
  { id: 'qwen3-asr-0.6b', name: 'Qwen3-ASR · 0.6B', tag: '建议用显卡', budget: '模型显存先留约 4–6 GB', note: '需要留出一些显存。让 AI 检查你的电脑，再决定是否安装。', docs: 'qwen' },
  { id: 'qwen3-asr-1.7b', name: 'Qwen3-ASR · 1.7B', tag: '占用更多', budget: '模型显存先留约 6–8 GB', note: '占用更多资源。可以和较小的模型对照试说，再决定是否换用。', docs: 'qwen' },
];

/** 与原生复制一致的模拟实现；默认提示词仍保持原文。 */
export function targetedLocalPrompt(prompt: string, id: string | undefined, assessment: boolean): string {
  if (id === undefined) return prompt;
  const model = LOCAL_MODELS.find(item => item.id === id);
  if (!model) throw new Error('没有这个本地模型选项。');
  const scope = assessment
    ? '本次只检查这个模型是否适合我的电脑，用简短的人话告诉我结论，不安装。'
    : '本次只安装并连接这个模型，已有文件优先复用，保留其他模型与当前选择。常规配置和排错自主完成，不要求方案摘要或逐步确认。';
  return `${prompt}\n\n## 本次界面选择的模型\n\n模型：${model.name}\n登记 ID：${id}\n\n${scope}\n`;
}
