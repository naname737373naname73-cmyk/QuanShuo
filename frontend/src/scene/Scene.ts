import type { QuanShuoRecord } from '../bridge/types';

/**
 * 主窗口背后的那幅画。星湖是 WebGL 湖面，海棠是会上色的花枝，业务层不区分。
 * 事件：starhover（{ id, x, y } 或 null）、starselect（记录 ID）、period、resized、contextrestored。
 */
export interface Scene extends EventTarget {
  initialize(): Promise<void>;
  setRecords(records: QuanShuoRecord[], animateNew: boolean): void;
  setDay(day: number, immediate?: boolean): void;
  setMicrophoneLevel(level: number): void;
  setForcedVoice(level: number): void;
  setQuiet(quiet: boolean): void;
  setHovered(id: string | null): void;
  setSelected(ids: Set<string>): void;
  celebrate(): void;
  setStageImage(image: TexImageSource | null, rect: DOMRect | null): void;
  updateStageRect(rect: DOMRect): void;
}
