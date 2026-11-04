import type { QuanShuoRecord } from '../bridge/types';

export function formatTime(timestamp: string): string {
  return new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(new Date(timestamp));
}

export function dayKey(timestamp: string): string {
  const date = new Date(timestamp);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

export function formatDay(timestamp: string, now = new Date()): string {
  const date = new Date(timestamp);
  const current = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const target = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const days = Math.round((current.getTime() - target.getTime()) / 86_400_000);
  if (days === 0) return '今天';
  if (days === 1) return '昨天';
  return new Intl.DateTimeFormat('zh-CN', { month: 'long', day: 'numeric', weekday: 'short' }).format(date);
}

export function countToday(records: QuanShuoRecord[]): number {
  const today = dayKey(new Date().toISOString());
  return records.filter((record) => dayKey(record.timestamp) === today).length;
}

export function formatRecordForAI(record: QuanShuoRecord): string {
  const date = new Date(record.timestamp);
  const pad = (value: number) => String(value).padStart(2, '0');
  const timestamp = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
  const body = `[说明] ${record.text}\n[时间] ${timestamp}，来自 ${record.appName}`;
  if (!record.imagePath) return body;
  const path = record.imagePath.includes(' ') ? `"${record.imagePath}"` : record.imagePath;
  return `[截图] ${path}\n${body}`;
}

/** 只要原话：每条之间空一行，没有文字的记录跳过。 */
export function joinTexts(records: QuanShuoRecord[]): string {
  return records.map((record) => record.text.trim()).filter(Boolean).join('\n\n');
}

export function formatManyForAI(records: QuanShuoRecord[]): string {
  if (records.length === 1) return formatRecordForAI(records[0]);
  return records.map((record, index) => `[${index + 1}]\n${formatRecordForAI(record)}`).join('\n\n');
}
