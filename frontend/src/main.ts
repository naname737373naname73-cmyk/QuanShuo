import { createBridge } from './bridge';
import { App } from './app/App';
import { readQuery } from './app/query';
import './styles.css';

const query = new URLSearchParams(location.search);
const root = document.querySelector<HTMLElement>('#app');
if (!root) throw new Error('缺少应用根节点。');
const options = readQuery(query);
// 样式要在第一帧之前定下来，否则海棠打开时会先闪一下星湖的配色
document.documentElement.dataset.skin = options.skin;
const app = new App(root, createBridge(query), options);
app.initialize().catch((error: unknown) => {
  console.error(error);
  root.innerHTML = `<main class="boot"><p>圈说没有打开</p><span>${error instanceof Error ? error.message : String(error)}</span><button class="primary-button" id="reload">重新打开</button></main>`;
  root.querySelector('#reload')?.addEventListener('click', () => location.reload());
});
