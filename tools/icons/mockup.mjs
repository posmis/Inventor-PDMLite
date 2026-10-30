// Макет окна PdmLite Explorer для docs/STYLE.md: node tools/icons/mockup.mjs
// Рисуется в HTML теми же цветами и иконками, что и тема WPF. Это иллюстрация
// правил оформления, а не снимок экрана приложения.
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { icons } from './icons.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const { chromium } = createRequire(import.meta.url)('playwright');

const svg = (name, size = 16) => `<svg width="${size}" height="${size}" viewBox="0 0 16 16">${icons[name].map(e =>
  `<path d="${e.d}" fill="${e.fill ?? 'none'}"${e.stroke ? ` stroke="${e.stroke}" stroke-width="${e.w ?? 1}" stroke-linejoin="round" stroke-linecap="${e.cap ?? 'butt'}"` : ''}${e.dash ? ` stroke-dasharray="${e.dash}"` : ''}/>`).join('')}</svg>`;

const chevron = open => `<svg width="16" height="16" viewBox="0 0 16 16"><path d="M6,4 L10,8 L6,12" fill="none" stroke="#6E6E6E" stroke-width="1.3" stroke-linejoin="round" ${open ? 'transform="rotate(90 8 8)"' : ''}/></svg>`;
const row = (depth, icon, name, { open, leaf, sel, active } = {}) =>
  `<div class="row${sel ? ' sel' : ''}" style="padding-left:${depth * 16}px">${leaf ? '<span class="sp"></span>' : chevron(open)}<span class="mark${active ? ' on' : ''}"></span>${svg(icon)}<span class="${active ? 'b' : ''}">${name}</span></div>`;

const html = `<html><head><style>
:root{--window:#F0F0F0;--panel:#FFF;--toolbar:#F5F5F5;--section:#EDEDED;--border:#C6C6C6;--divider:#E1E1E1;--text:#1E1E1E;--text2:#5F5F5F;--accent:#0696D7;--hover:#E5F3FB;--sel:#CCE8F7;--selb:#7FC3E8}
*{box-sizing:border-box} body{margin:0;padding:16px;background:#d9dde2;font:12px "Segoe UI","Noto Sans",sans-serif;color:var(--text)}
.win{width:980px;border:1px solid #8a8a8a;background:var(--window);box-shadow:0 4px 18px rgba(0,0,0,.25)}
.title{height:30px;display:flex;align-items:center;gap:8px;padding:0 10px;background:#fff;border-bottom:1px solid var(--divider)}
.title .x{margin-left:auto;color:var(--text2);letter-spacing:14px}
.menu{display:flex;gap:2px;padding:2px 4px}.menu span{padding:3px 8px}
.tb{display:flex;align-items:center;gap:2px;padding:3px 4px;background:var(--toolbar);border-bottom:1px solid var(--border)}
.btn{display:flex;align-items:center;gap:5px;padding:3px 6px;border:1px solid transparent;border-radius:2px}
.btn.hover{background:var(--hover);border-color:#A8D8F0}.btn.on{background:var(--sel);border-color:var(--selb)}
.sep{width:1px;height:18px;background:var(--border);margin:0 4px}
.main{display:flex;gap:4px;padding:4px;height:470px}
.pane{background:var(--panel);border:1px solid var(--border);display:flex;flex-direction:column}
.ptb{display:flex;align-items:center;gap:2px;padding:3px 4px;background:var(--toolbar);border-bottom:1px solid var(--divider)}
.search{flex:1;display:flex;align-items:center;gap:4px;height:24px;border:1px solid #ABADB3;background:#fff;padding:0 4px;color:var(--text2)}
.tree{flex:1;padding:2px}.row{display:flex;align-items:center;height:22px;border:1px solid transparent;padding-right:4px}
.row svg{flex:none}.row .mark+svg{margin-right:5px}.row.sel{background:var(--sel);border-color:var(--selb)}
.sp{width:16px;flex:none}.mark{width:3px;height:14px;margin-right:3px;flex:none}.mark.on{background:var(--accent)}.b{font-weight:700}
.foot{padding:3px 6px;background:var(--toolbar);border-top:1px solid var(--divider);color:var(--text2)}
.card{padding:10px}.head{display:flex;gap:10px;align-items:flex-start;margin-bottom:4px}.h1{font-size:15px;font-weight:600}
.sec{background:var(--section);font-weight:600;padding:3px 6px;margin:10px 0 4px}
.kv{display:grid;grid-template-columns:96px 1fr}.kv div{padding:3px 6px}.kv .k{color:var(--text2)}
.hint{color:var(--text2);padding:2px 6px}
.status{display:flex;align-items:center;height:24px;background:var(--toolbar);border-top:1px solid var(--border);padding:0 6px;gap:10px}
.status .i{display:flex;align-items:center;gap:5px}.status .d{width:1px;height:16px;background:var(--border)}
.ctx{position:absolute;left:300px;top:330px;background:#fff;border:1px solid #A0A0A0;box-shadow:2px 2px 6px rgba(0,0,0,.2);padding:2px;width:250px}
.ctx div{display:flex;align-items:center;gap:8px;padding:4px 8px}.ctx .hl{background:var(--hover)}.ctx hr{border:0;border-top:1px solid var(--divider);margin:2px 0 2px 30px}
</style></head><body><div class="win" style="position:relative">
<div class="title">${svg('App.PdmLite')}<span>Изделие-100 — PdmLite Explorer</span><span class="x">─ ☐ ✕</span></div>
<div class="menu"><span>Файл</span><span>Вид</span><span>Сервис</span><span>Справка</span></div>
<div class="tb">
 <div class="btn">${svg('Cmd.OpenFolder')}Открыть папку</div><div class="btn">${svg('Cmd.OpenProjectFile')}Открыть .ipj</div><div class="sep"></div>
 <div class="btn hover">${svg('Cmd.Refresh')}Обновить</div><div class="sep"></div><div class="btn">${svg('Cmd.InventorProject')}Открыть активный проект Inventor</div>
</div>
<div class="main">
 <div class="pane" style="flex:3">
  <div class="ptb"><div class="search">${svg('Cmd.Search')}Поиск по имени файла</div>
   <div class="btn on">${svg('Cmd.FollowActive')}</div><div class="btn">${svg('Cmd.CollapseAll')}</div><div class="btn">${svg('Cmd.Refresh')}</div></div>
  <div class="tree">
   ${row(0, 'Folder.Open', 'Изделие-100', { open: true })}
   ${row(1, 'Folder.Open', 'Сборки', { open: true })}
   ${row(2, 'Doc.Assembly', 'АБВГ.100.000_Изделие.iam', { leaf: true })}
   ${row(2, 'Doc.Drawing', 'АБВГ.100.000СБ_Изделие.dwg', { leaf: true })}
   ${row(2, 'Doc.Assembly', 'АБВГ.301.000_Рама-сварная.iam', { leaf: true, active: true })}
   ${row(2, 'Doc.Drawing', 'АБВГ.301.000СБ_Рама-сварная.dwg', { leaf: true, sel: true })}
   ${row(2, 'Doc.Drawing', 'АБВГ.301.000МЭ_Рама-сварная.dwg', { leaf: true })}
   ${row(1, 'Folder.Open', 'Детали', { open: true })}
   ${row(2, 'Doc.Part', 'АБВГ.301.001_Швеллер.ipt', { leaf: true })}
   ${row(2, 'Doc.Part', 'АБВГ.301.002_Пластина-опорная.ipt', { leaf: true })}
   ${row(2, 'Doc.Drawing', 'АБВГ.301.002_Пластина-опорная.idw', { leaf: true })}
   ${row(1, 'Folder', 'Презентации')}
   ${row(1, 'Doc.Project', 'Изделие-100.ipj', { leaf: true })}
  </div>
  <div class="foot">Изделие-100: документов 9</div>
 </div>
 <div class="pane" style="flex:2"><div class="card">
  <div class="head">${svg('Doc.Drawing', 32)}<div><div class="h1">АБВГ.301.000СБ_Рама-сварная.dwg</div><div style="color:var(--text2)">Чертёж</div></div></div>
  <div class="sec">Файл</div>
  <div class="kv"><div class="k">Папка</div><div>D:\\Проекты\\Изделие-100\\Сборки</div><div class="k">Размер</div><div>412,6 КБ</div><div class="k">Изменён</div><div>29.10.2026 16:42</div></div>
  <div class="sec">Свойства документа</div>
  <div class="hint">Обозначение, наименование и другие iProperties появятся на следующем шаге.</div>
 </div></div>
</div>
<div class="status"><div class="i">${svg('Doc.Project')}D:\\Проекты\\Изделие-100\\Изделие-100.ipj</div><div class="d"></div>
 <div class="i">${svg('State.Online')}Inventor 2025: подключён</div><div class="d"></div><div class="i">${svg('Doc.Assembly')}<b>АБВГ.301.000_Рама-сварная.iam</b></div></div>
<div class="ctx"><div class="hl"><span style="display:flex">${svg('Cmd.Open')}</span><b>Открыть</b></div><hr>
 <div>${svg('Cmd.ShowInWindows')}Показать в проводнике Windows</div><div>${svg('Cmd.CopyPath')}Копировать путь</div><hr>
 <div>${svg('Cmd.Refresh')}Обновить дерево проекта</div></div>
</div></body></html>`;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1014, height: 640 }, deviceScaleFactor: 1 });
await page.setContent(html);
await page.screenshot({ path: join(root, 'docs/images/explorer-mockup.png'), fullPage: true });
await browser.close();
console.log('docs/images/explorer-mockup.png обновлён');
