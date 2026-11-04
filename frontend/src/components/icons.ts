// 图标来自 public/assets/brand/icons.svg，用 <use> 引用，颜色跟随 currentColor。
export type IconName = string;

export function icon(name: IconName, className = ''): string {
  return `<svg class="icon ${className}" viewBox="0 0 20 20" aria-hidden="true"><use href="./assets/brand/icons.svg#i-${name}"></use></svg>`;
}
