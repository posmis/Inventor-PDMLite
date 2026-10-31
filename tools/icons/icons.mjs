// Единый источник иконок PdmLite.
// Сетка 16×16, координаты как в SVG. Из этого файла build.mjs генерирует:
//   - src/PdmLite.UI.Shared/Themes/Icons.xaml (WPF DrawingImage),
//   - иконку приложения Explorer (.ico),
//   - лист предпросмотра docs/images/icons.png.
//
// Элемент: { d, fill, stroke, w, dash, cap }.
//   d      — геометрия в синтаксисе SVG path (поддерживается и WPF);
//   fill   — заливка; stroke/w — контур и его толщина;
//   dash   — штрих "2 1"; cap — окончания линий: "round" | "square".

// ---- Палитра иконок: плоский стиль Inventor 2025 (светлые заливки, тонкий контур, синий и оранжевый акценты) ----
export const C = {
  ink: '#4A4A4A',        // основной цвет линий команд
  inkSoft: '#8A8A8A',
  sheet: '#FFFFFF',
  sheetLine: '#7A7A7A',
  sheetText: '#B5B5B5',
  blue: '#0696D7',       // акцент команд (синий Autodesk)
  blueLight: '#E3F1FC', blueMid: '#9CCBF0', blueDark: '#4C9BDB', blueEdge: '#2B6CA3',
  amberLight: '#FFE9C7', amberMid: '#F8BC67', amberDark: '#E8902A', amberEdge: '#A35F0C',
  orange: '#F28A1A',
  folder: '#F2C14E', folderFront: '#F8D57E', folderEdge: '#C1912A',
  green: '#3AA655', red: '#E04B3A', gray: '#A0A0A0',
  app: '#0F6CBD', appEdge: '#0A4F8C',
};

// ---- Построители ----
const n = v => +v.toFixed(2);

/** Изометрический куб (плоские грани, контур в тон): (x, y) — верхняя вершина, s — полуширина; высота 2s. */
function cube(x, y, s, col) {
  const h = s / 2;
  return [
    { d: `M${n(x)},${n(y)} L${n(x + s)},${n(y + h)} L${n(x)},${n(y + s)} L${n(x - s)},${n(y + h)} Z`, fill: col.top, stroke: col.edge, w: 0.9 },
    { d: `M${n(x - s)},${n(y + h)} L${n(x)},${n(y + s)} L${n(x)},${n(y + 2 * s)} L${n(x - s)},${n(y + 1.5 * s)} Z`, fill: col.left, stroke: col.edge, w: 0.9 },
    { d: `M${n(x)},${n(y + s)} L${n(x + s)},${n(y + h)} L${n(x + s)},${n(y + 1.5 * s)} L${n(x)},${n(y + 2 * s)} Z`, fill: col.right, stroke: col.edge, w: 0.9 },
  ];
}
const BLUE = { top: C.blueLight, left: C.blueMid, right: C.blueDark, edge: C.blueEdge };
const AMBER = { top: C.amberLight, left: C.amberMid, right: C.amberDark, edge: C.amberEdge };

/** Лист с загнутым углом. */
function sheet(x1 = 3.5, y1 = 1.5, x2 = 12.5, y2 = 14.5, fold = 3) {
  return [
    { d: `M${x1},${y1} H${x2 - fold} L${x2},${y1 + fold} V${y2} H${x1} Z`, fill: C.sheet, stroke: C.sheetLine, w: 1 },
    { d: `M${x2 - fold},${y1} V${y1 + fold} H${x2} Z`, fill: '#E6E6E6', stroke: C.sheetLine, w: 1 },
  ];
}

/** Папка (закрытая): задник и передняя часть. */
function folder(dx = 0, dy = 0) {
  const t = (s) => s.replace(/(-?\d+(\.\d+)?),(-?\d+(\.\d+)?)/g, (_, a, __, b) => `${n(+a + dx)},${n(+b + dy)}`);
  return [
    { d: t('M1.5,3 H6 L7.5,4.5 H14.5 V13.5 H1.5 Z'), fill: C.folder, stroke: C.folderEdge, w: 0.9 },
    { d: t('M1.5,6 H14.5 V13.5 H1.5 Z'), fill: C.folderFront, stroke: C.folderEdge, w: 0.9 },
  ];
}

/** Подложка под значок-модификатор в углу (чтобы он читался поверх основной иконки). */
const badge = (x, y) => ({ d: `M${x},${y} H${x + 7.5} V${y + 7.5} H${x} Z`, fill: '#FFFFFF' });

/** Стрелка «внутрь» ↙ в левом нижнем углу. */
const arrowIn = [
  badge(0, 8.5),
  { d: 'M6.2,9.8 L1.8,14.2', stroke: C.blue, w: 1.6, cap: 'round' },
  { d: 'M1.6,10.2 V14.4 H5.8', stroke: C.blue, w: 1.6, cap: 'round' },
];

/** Стрелка «наружу» ↗ в правом нижнем углу. */
const arrowOut = [
  badge(8.5, 8.5),
  { d: 'M9.8,14.2 L14.2,9.8', stroke: C.blue, w: 1.6, cap: 'round' },
  { d: 'M10.2,9.6 H14.4 V13.8', stroke: C.blue, w: 1.6, cap: 'round' },
];

/** Зелёный «плюс» в правом нижнем углу. */
const plus = [
  { d: 'M15.5,12 A3.5,3.5 0 1 1 8.5,12 A3.5,3.5 0 1 1 15.5,12 Z', fill: C.green },
  { d: 'M12,10 V14 M10,12 H14', stroke: '#FFFFFF', w: 1.4, cap: 'round' },
];

const circle = (cx, cy, r) => `M${cx + r},${cy} A${r},${r} 0 1 1 ${cx - r},${cy} A${r},${r} 0 1 1 ${cx + r},${cy} Z`;

// ---- Иконки ----
export const icons = {
  // Документы
  'Doc.Part': [...cube(8, 1.5, 6.5, BLUE)],
  'Doc.Assembly': [...cube(5.5, 1.5, 4.5, BLUE), ...cube(10.5, 5.5, 4.5, AMBER)],
  'Doc.Presentation': [
    { d: 'M6,9 L10.5,4.8', stroke: C.inkSoft, w: 1, dash: '1.5 1' },
    ...cube(11.5, 0.5, 3.5, AMBER),
    ...cube(5.5, 6, 4.5, BLUE),
  ],
  'Doc.Drawing': [
    { d: 'M1.5,2.5 H14.5 V13.5 H1.5 Z', fill: C.sheet, stroke: C.sheetLine, w: 1 },
    { d: 'M8.5,10.5 H14.5 V13.5 H8.5 Z', fill: C.blueLight, stroke: C.sheetLine, w: 1 },
    { d: 'M3.5,4.5 H7.5 V8.5 H3.5 Z', stroke: C.blue, w: 1 },
    { d: 'M9,4.5 H12.5 V8 H9 Z', stroke: C.blue, w: 1 },
  ],
  'Doc.Project': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,6 H8.5 M4.5,8 H9.5 M4.5,10 H7', stroke: C.sheetText, w: 1 },
    ...cube(11.5, 7.5, 3.5, BLUE),
  ],
  'Doc.Unknown': [
    ...sheet(),
    { d: 'M5.5,7 H10.5 M5.5,9 H10.5 M5.5,11 H8.5', stroke: C.sheetText, w: 1 },
  ],
  'Folder': [...folder()],
  'Folder.Open': [
    { d: 'M1.5,3 H6 L7.5,4.5 H13.5 V7 H1.5 Z', fill: C.folder, stroke: C.folderEdge, w: 0.9 },
    { d: 'M1.5,13.5 L3.8,7 H15.5 L13.2,13.5 Z', fill: C.folderFront, stroke: C.folderEdge, w: 0.9 },
    { d: 'M1.5,3 V13.5', stroke: C.folderEdge, w: 0.9 },
  ],

  // Команды
  'Cmd.Refresh': [
    { d: 'M13.2,7 A5.3,5.3 0 0 0 3.6,4.6', stroke: C.blue, w: 1.6, cap: 'round' },
    { d: 'M2.8,9 A5.3,5.3 0 0 0 12.4,11.4', stroke: C.blue, w: 1.6, cap: 'round' },
    { d: 'M2.6,1.8 L2.8,5.6 L6.6,5 Z', fill: C.blue },
    { d: 'M13.4,14.2 L13.2,10.4 L9.4,11 Z', fill: C.blue },
  ],
  'Cmd.Search': [
    { d: circle(6.5, 6.5, 4.5), fill: C.blueLight, stroke: C.ink, w: 1.3 },
    { d: 'M9.9,9.9 L14.2,14.2', stroke: C.ink, w: 2, cap: 'round' },
  ],
  'Cmd.CollapseAll': [
    { d: 'M4.5,1.5 H14.5 V11.5 H4.5 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 1 },
    { d: 'M1.5,4.5 H11.5 V14.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M4,9.5 H9', stroke: C.blue, w: 1.6, cap: 'round' },
  ],
  'Cmd.FollowActive': [
    { d: circle(8, 8, 4.5), stroke: C.ink, w: 1.2 },
    { d: 'M8,0.8 V3.8 M8,12.2 V15.2 M0.8,8 H3.8 M12.2,8 H15.2', stroke: C.ink, w: 1.2, cap: 'round' },
    { d: circle(8, 8, 1.9), fill: C.orange },
  ],
  'Cmd.Open': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,6 H8.5 M4.5,8 H8', stroke: C.sheetText, w: 1 },
    ...arrowOut,
  ],
  'Cmd.OpenFolder': [
    { d: 'M1.5,3 H6 L7.5,4.5 H13.5 V7 H1.5 Z', fill: C.folder, stroke: C.folderEdge, w: 0.9 },
    { d: 'M1.5,13.5 L3.8,7 H15.5 L13.2,13.5 Z', fill: C.folderFront, stroke: C.folderEdge, w: 0.9 },
    { d: 'M1.5,3 V13.5', stroke: C.folderEdge, w: 0.9 },
  ],
  'Cmd.OpenProjectFile': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,6 H8.5 M4.5,8 H9.5 M4.5,10 H7', stroke: C.sheetText, w: 1 },
    ...cube(11.5, 7.5, 3.5, BLUE),
  ],
  'Cmd.Recent': [
    { d: circle(8, 8, 6.5), fill: '#FFFFFF', stroke: C.ink, w: 1.2 },
    { d: 'M8,4.2 V8 L10.8,9.8', stroke: C.blue, w: 1.6, cap: 'round' },
  ],
  'Cmd.ShowInWindows': [...folder(0, 0), ...arrowOut],
  'Cmd.CopyPath': [
    { d: 'M5.5,1.5 H12 L14.5,4 V11.5 H5.5 Z', fill: '#F2F2F2', stroke: C.inkSoft, w: 1 },
    { d: 'M1.5,4.5 H8 L10.5,7 V14.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M3.5,9.5 H8.5 M3.5,11.5 H7', stroke: C.blue, w: 1.1, cap: 'round' },
  ],
  'Cmd.Rename': [
    { d: 'M1.5,5.5 H10.5 V10.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M3.5,8 H7.5', stroke: C.blue, w: 1.4, cap: 'round' },
    { d: 'M9.3,12.8 L13.9,3.9 L15.5,4.8 L10.9,13.7 L9,14.6 Z', fill: C.orange, stroke: C.amberEdge, w: 0.9 },
  ],
  'Cmd.Move': [
    ...folder(),
    { d: 'M4,10.2 H10', stroke: '#FFFFFF', w: 3.2, cap: 'round' },
    { d: 'M4,10.2 H9.8', stroke: C.blue, w: 1.6, cap: 'round' },
    { d: 'M9,7.4 L12.4,10.2 L9,13 Z', fill: C.blue },
  ],
  'Cmd.ProjectTree': [
    { d: 'M1.5,1.5 H7 V5.5 H1.5 Z', fill: C.folderFront, stroke: C.folderEdge, w: 0.9 },
    { d: 'M4,5.5 V12.8 H8.5 M4,8.5 H8.5', stroke: C.inkSoft, w: 1 },
    { d: 'M8.5,6.5 H14.5 V10.5 H8.5 Z', fill: C.blueLight, stroke: C.blueEdge, w: 0.9 },
    { d: 'M8.5,10.8 H14.5 V14.8 H8.5 Z', fill: C.amberLight, stroke: C.amberEdge, w: 0.9 },
  ],
  'Cmd.RevealInExplorer': [
    { d: 'M1.5,2.5 H13.5 V12.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M1.5,2.5 H13.5 V5 H1.5 Z', fill: C.app, stroke: C.appEdge, w: 1 },
    { d: 'M3,7.2 H6 M3,9.2 H7.5', stroke: C.sheetText, w: 1 },
    ...arrowOut,
  ],
  'Cmd.InventorProject': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,6 H8.5 M4.5,8 H9.5', stroke: C.sheetText, w: 1 },
    ...cube(11.5, 7.5, 3.5, BLUE),
    ...arrowIn,
  ],
  'Cmd.Modules': [
    { d: 'M1.5,1.5 H7 V7 H1.5 Z', fill: C.blueLight, stroke: C.blueEdge, w: 0.9 },
    { d: 'M9,1.5 H14.5 V7 H9 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 0.9 },
    { d: 'M1.5,9 H7 V14.5 H1.5 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 0.9 },
    { d: 'M9,9 H14.5 V14.5 H9 Z', fill: C.blueDark, stroke: C.blueEdge, w: 0.9 },
  ],
  'Cmd.Info': [
    { d: circle(8, 8, 6.5), fill: C.blue },
    { d: 'M7.1,7 H8.9 V12 H7.1 Z', fill: '#FFFFFF' },
    { d: circle(8, 4.6, 1), fill: '#FFFFFF' },
  ],

  // Библиотека
  'Cmd.Library': [
    { d: 'M1.5,2.5 H4.5 V14.5 H1.5 Z', fill: C.blueMid, stroke: C.blueEdge, w: 0.9 },
    { d: 'M5.5,2.5 H8.5 V14.5 H5.5 Z', fill: C.amberMid, stroke: C.amberEdge, w: 0.9 },
    { d: 'M9.6,3.6 L12.5,2.8 L15.2,13.6 L12.3,14.4 Z', fill: C.blueLight, stroke: C.blueEdge, w: 0.9 },
    { d: 'M2.3,5 H3.7 M6.3,5 H7.7', stroke: '#FFFFFF', w: 1 },
  ],
  'Cmd.LibraryAdd': [
    { d: 'M1.5,2.5 H4.5 V14.5 H1.5 Z', fill: C.blueMid, stroke: C.blueEdge, w: 0.9 },
    { d: 'M5.5,2.5 H8.5 V14.5 H5.5 Z', fill: C.amberMid, stroke: C.amberEdge, w: 0.9 },
    ...plus,
  ],
  'Cmd.AddFiles': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,6 H8.5 M4.5,8 H8', stroke: C.sheetText, w: 1 },
    ...plus,
  ],
  'Cmd.AddFolder': [...folder(), ...plus],
  'Cmd.Exclude': [
    { d: circle(8, 8, 6.3), fill: '#FFFFFF', stroke: C.red, w: 1.4 },
    { d: 'M3.6,12.4 L12.4,3.6', stroke: C.red, w: 1.6, cap: 'round' },
  ],
  'Cmd.Include': [
    { d: 'M2.5,8.5 L6.5,12.5 L13.5,4', stroke: C.green, w: 2.2, cap: 'round' },
  ],
  'Cmd.Check': [
    { d: 'M1.5,2.5 H10.5 V13.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M3.5,5.5 H8.5 M3.5,8 H8.5 M3.5,10.5 H6', stroke: C.sheetText, w: 1 },
    { d: circle(11.8, 11.8, 3.7), fill: C.green },
    { d: 'M10,11.9 L11.3,13.2 L13.6,10.6', stroke: '#FFFFFF', w: 1.3, cap: 'round' },
  ],
  'Cmd.Settings': [
    { d: 'M8,1.5 L9.3,1.7 L9.6,3.3 L10.8,3.8 L12.1,2.8 L13.2,3.9 L12.2,5.2 L12.7,6.4 L14.3,6.7 L14.5,8 L14.3,9.3 L12.7,9.6 L12.2,10.8 L13.2,12.1 L12.1,13.2 L10.8,12.2 L9.6,12.7 L9.3,14.3 L8,14.5 L6.7,14.3 L6.4,12.7 L5.2,12.2 L3.9,13.2 L2.8,12.1 L3.8,10.8 L3.3,9.6 L1.7,9.3 L1.5,8 L1.7,6.7 L3.3,6.4 L3.8,5.2 L2.8,3.9 L3.9,2.8 L5.2,3.8 L6.4,3.3 L6.7,1.7 Z', fill: '#D9D9D9', stroke: C.ink, w: 0.9 },
    { d: circle(8, 8, 2.3), fill: C.blue },
  ],
  'Cmd.Database': [
    { d: 'M2.5,4 V12 A5.5,2 0 0 0 13.5,12 V4 Z', fill: C.blueMid, stroke: C.blueEdge, w: 0.9 },
    { d: 'M13.5,4 A5.5,2 0 1 1 2.5,4 A5.5,2 0 1 1 13.5,4 Z', fill: C.blueLight, stroke: C.blueEdge, w: 0.9 },
    { d: 'M2.5,8 A5.5,2 0 0 0 13.5,8', stroke: C.blueEdge, w: 0.9 },
  ],

  // Состояния
  'State.Info': [
    { d: circle(8, 8, 6.5), fill: C.blue },
    { d: 'M7.2,7 H8.8 V11.5 H7.2 Z', fill: '#FFFFFF' },
    { d: circle(8, 4.8, 0.95), fill: '#FFFFFF' },
  ],
  'State.Ok': [
    { d: circle(8, 8, 6.5), fill: C.green },
    { d: 'M4.8,8.2 L7,10.4 L11.2,5.8', stroke: '#FFFFFF', w: 1.8, cap: 'round' },
  ],
  'State.Online': [
    { d: circle(8, 8, 4.5), fill: C.green },
  ],
  'State.Offline': [
    { d: circle(8, 8, 4.3), fill: '#FFFFFF', stroke: C.gray, w: 1.3 },
  ],
  'State.Warning': [
    { d: 'M8,1.5 L15,14.5 H1 Z', fill: '#FFC83D', stroke: '#C68A00', w: 0.9 },
    { d: 'M7.2,5.5 H8.8 V10 H7.2 Z M7.2,11.2 H8.8 V12.8 H7.2 Z', fill: '#3B3B3B' },
  ],
  'State.Error': [
    { d: circle(8, 8, 6.5), fill: C.red },
    { d: 'M5.5,5.5 L10.5,10.5 M10.5,5.5 L5.5,10.5', stroke: '#FFFFFF', w: 1.8, cap: 'round' },
  ],

  // Приложение
  'App.PdmLite': [
    { d: 'M3,0.5 H13 A2.5,2.5 0 0 1 15.5,3 V13 A2.5,2.5 0 0 1 13,15.5 H3 A2.5,2.5 0 0 1 0.5,13 V3 A2.5,2.5 0 0 1 3,0.5 Z', fill: C.app },
    { d: 'M8,2.6 L13,5.1 L8,7.6 L3,5.1 Z', fill: '#FFFFFF' },
    { d: 'M3,5.1 L8,7.6 V13.4 L3,10.9 Z', fill: '#CFE3F7' },
    { d: 'M8,7.6 L13,5.1 V10.9 L8,13.4 Z', fill: '#8FC0EC' },
  ],
};
