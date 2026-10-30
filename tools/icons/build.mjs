// Генератор иконок: node tools/icons/build.mjs
// Пишет Icons.xaml и иконку приложения; при наличии Playwright — ещё и лист предпросмотра.
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { icons } from './icons.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const xamlPath = join(root, 'src/PdmLite.UI.Shared/Themes/Icons.xaml');
const icoPath = join(root, 'src/PdmLite.Explorer/Assets/PdmLite.ico');
const sheetPath = join(root, 'docs/images/icons.png');

// ---------- SVG ----------
function svg(name, size) {
  const body = icons[name].map(e => {
    const attrs = [`d="${e.d}"`, `fill="${e.fill ?? 'none'}"`];
    if (e.stroke) attrs.push(`stroke="${e.stroke}"`, `stroke-width="${e.w ?? 1}"`, `stroke-linejoin="round"`, `stroke-linecap="${e.cap ?? 'butt'}"`);
    if (e.dash) attrs.push(`stroke-dasharray="${e.dash}"`);
    return `<path ${attrs.join(' ')}/>`;
  }).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 16 16">${body}</svg>`;
}

// ---------- XAML ----------
const capName = c => (c === 'round' ? 'Round' : c === 'square' ? 'Square' : 'Flat');
function xamlDrawing(e) {
  const brush = e.fill ? ` Brush="${e.fill}"` : '';
  let pen = '';
  if (e.stroke) {
    const dash = e.dash ? `<Pen.DashStyle><DashStyle Dashes="${e.dash.split(/\s+/).map(v => +v / (e.w ?? 1)).join(' ')}" /></Pen.DashStyle>` : '';
    const cap = capName(e.cap);
    pen = `<GeometryDrawing.Pen><Pen Brush="${e.stroke}" Thickness="${e.w ?? 1}" LineJoin="Round" StartLineCap="${cap}" EndLineCap="${cap}"${dash ? '' : ' /'}>${dash ? dash + '</Pen>' : ''}</GeometryDrawing.Pen>`;
  }
  // F1 — правило заливки Nonzero, как в SVG.
  return `          <GeometryDrawing${brush} Geometry="F1 ${e.d}">${pen}</GeometryDrawing>`;
}

const keys = Object.keys(icons);
const xaml = `<!--
  СГЕНЕРИРОВАНО tools/icons/build.mjs из tools/icons/icons.mjs. Не редактировать вручную.
  Ключ ресурса: Pdm.Icon.{Имя}. Размер 16×16, масштабируется без потерь.
-->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options">
${keys.map(k => `
  <DrawingImage x:Key="Pdm.Icon.${k}" po:Freeze="True">
    <DrawingImage.Drawing>
      <DrawingGroup>
          <GeometryDrawing Brush="Transparent" Geometry="M0,0 H16 V16 H0 Z" />
${icons[k].map(xamlDrawing).join('\n')}
      </DrawingGroup>
    </DrawingImage.Drawing>
  </DrawingImage>`).join('\n')}
</ResourceDictionary>
`;
mkdirSync(dirname(xamlPath), { recursive: true });
writeFileSync(xamlPath, xaml.replace(/\n/g, '\r\n'));
console.log(`Icons.xaml: ${keys.length} иконок`);

// ---------- PNG (Playwright) ----------
let chromium;
try {
  chromium = createRequire(import.meta.url)('playwright').chromium;
} catch {
  console.log('Playwright не найден: .ico и лист предпросмотра не обновлены.');
  process.exit(0);
}

const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 1 });

async function png(name, size) {
  await page.setContent(`<html><body style="margin:0;background:transparent">${svg(name, size)}</body></html>`);
  return page.locator('svg').screenshot({ omitBackground: true });
}

// Иконка приложения: PNG-кадры внутри ICO (поддерживается с Windows Vista).
const sizes = [16, 24, 32, 48, 64, 256];
const frames = [];
for (const s of sizes) frames.push(await png('App.PdmLite', s));
const header = Buffer.alloc(6 + 16 * frames.length);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
let offset = header.length;
frames.forEach((f, i) => {
  const o = 6 + i * 16, s = sizes[i];
  header.writeUInt8(s >= 256 ? 0 : s, o); header.writeUInt8(s >= 256 ? 0 : s, o + 1);
  header.writeUInt16LE(1, o + 4); header.writeUInt16LE(32, o + 6);
  header.writeUInt32LE(f.length, o + 8); header.writeUInt32LE(offset, o + 12);
  offset += f.length;
});
mkdirSync(dirname(icoPath), { recursive: true });
writeFileSync(icoPath, Buffer.concat([header, ...frames]));
console.log('PdmLite.ico обновлён');

// Лист предпросмотра: каждая иконка в 16, 32 и 64 px.
const rows = keys.map(k => `
  <tr><td class="k">${k}</td>
    <td>${svg(k, 16)}</td><td>${svg(k, 32)}</td><td>${svg(k, 64)}</td>
    <td class="sel">${svg(k, 16)} <span>Выделено</span></td></tr>`).join('');
await page.setViewportSize({ width: 560, height: 400 });
await page.setContent(`<html><head><style>
  body{margin:0;padding:12px;background:#fff;font:12px "Segoe UI",sans-serif;color:#1e1e1e}
  table{border-collapse:collapse} td{padding:4px 10px;border-bottom:1px solid #e3e3e3;vertical-align:middle}
  td.k{color:#5f5f5f;width:170px} svg{display:block}
  td.sel{background:#cce8f7} td.sel svg{display:inline-block;vertical-align:middle} td.sel span{vertical-align:middle;margin-left:4px}
  th{text-align:left;padding:4px 10px;color:#5f5f5f;font-weight:600;border-bottom:1px solid #c9c9c9}
</style></head><body><table><tr><th>Ключ</th><th>16</th><th>32</th><th>64</th><th>В списке</th></tr>${rows}</table></body></html>`);
mkdirSync(dirname(sheetPath), { recursive: true });
await page.screenshot({ path: sheetPath, fullPage: true });
console.log('docs/images/icons.png обновлён');
await browser.close();
