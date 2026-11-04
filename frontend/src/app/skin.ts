export type Skin = 'starlake' | 'crabapple';

/** 两套皮肤的说法不同：星湖里一条记录是一颗星，海棠里是一朵花。 */
export interface SkinText {
  sceneLabel: string;
  countBefore: string;
  countAfter: string;
  emptyImage: string;
  emptyTitle: string;
  emptyLine: string;
  onboardingTitle: string;
  onboardingLead: string;
  onboardingCaptured: string;
  quietNote: string;
  toastIcon: string;
}

export const SKIN_TEXT: Record<Skin, SkinText> = {
  starlake: {
    sceneLabel: '星空、雪山和湖面。今天的记录是天上的星星',
    countBefore: '今天亮起',
    countAfter: '颗星',
    emptyImage: './assets/brand/empty-lake.svg',
    emptyTitle: '湖面还很安静',
    emptyLine: '圈住屏幕说一句，第一颗星就亮了。',
    onboardingTitle: '点亮第一颗星。',
    onboardingLead: '每条记录都是一张 PNG 和一份 Markdown，离开圈说也能打开。今天的记录会在天上连成一头鲸。',
    onboardingCaptured: '圈选已打开。保存后，第一颗星就会亮起来。',
    quietNote: '关闭水面、星光和流星的所有动效，只留静态画面。系统开启“减少动画”时也一样。',
    toastIcon: 'star',
  },
  crabapple: {
    sceneLabel: '一枝海棠的淡墨画。今天的每条记录会给一朵花上色',
    countBefore: '今天开了',
    countAfter: '朵花',
    emptyImage: './assets/crabapple/mark-bud.svg',
    emptyTitle: '枝头还很安静',
    emptyLine: '圈住屏幕说一句，第一朵花就开了。',
    onboardingTitle: '开出第一朵花。',
    onboardingLead: '每条记录都是一张 PNG 和一份 Markdown，离开圈说也能打开。今天的记录会给这枝海棠一朵一朵上色。',
    onboardingCaptured: '圈选已打开。保存后，第一朵花就开了。',
    quietNote: '关闭花开和花瓣飘落的动效，只留静态画面。系统开启“减少动画”时也一样。',
    toastIcon: 'petal',
  },
};
