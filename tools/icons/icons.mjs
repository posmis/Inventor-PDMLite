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

// ---- Палитра иконок (приглушённые цвета, близкие к Inventor) ----
export const C = {
  ink: '#3B3B3B',        // основной цвет линий команд
  inkSoft: '#6E6E6E',
  sheet: '#FFFFFF',
  sheetLine: '#6F6F6F',
  sheetText: '#A9A9A9',
  blue: '#2F6DB5',       // акцент команд
  blueLight: '#D4E4F6', blueMid: '#86AEDD', blueDark: '#4A7DBA', blueEdge: '#24497A',
  amberLight: '#F7DE9C', amberMid: '#E9B850', amberDark: '#C9921F', amberEdge: '#7A5410',
  folder: '#E2AE3E', folderFront: '#F4CD6A', folderEdge: '#A87A1C',
  green: '#2E8540', red: '#C42B1C', gray: '#9A9A9A',
  app: '#2B5C93', appEdge: '#1D4170',
};

// ---- Построители ----
const n = v => +v.toFixed(2);

/** Изометрический куб: (x, y) — верхняя вершина, s — полуширина; высота куба 2s. */
function cube(x, y, s, col) {
  const h = s / 2;
  return [
    { d: `M${n(x)},${n(y)} L${n(x + s)},${n(y + h)} L${n(x)},${n(y + s)} L${n(x - s)},${n(y + h)} Z`, fill: col.top, stroke: col.edge, w: 1 },
    { d: `M${n(x - s)},${n(y + h)} L${n(x)},${n(y + s)} L${n(x)},${n(y + 2 * s)} L${n(x - s)},${n(y + 1.5 * s)} Z`, fill: col.left, stroke: col.edge, w: 1 },
    { d: `M${n(x)},${n(y + s)} L${n(x + s)},${n(y + h)} L${n(x + s)},${n(y + 1.5 * s)} L${n(x)},${n(y + 2 * s)} Z`, fill: col.right, stroke: col.edge, w: 1 },
  ];
}
const BLUE = { top: C.blueLight, left: C.blueMid, right: C.blueDark, edge: C.blueEdge };
const AMBER = { top: C.amberLight, left: C.amberMid, right: C.amberDark, edge: C.amberEdge };

/** Лист с загнутым углом. */
function sheet(x1 = 3.5, y1 = 1.5, x2 = 12.5, y2 = 14.5, fold = 3) {
  return [
    { d: `M${x1},${y1} H${x2 - fold} L${x2},${y1 + fold} V${y2} H${x1} Z`, fill: C.sheet, stroke: C.sheetLine, w: 1 },
    { d: `M${x2 - fold},${y1} V${y1 + fold} H${x2}`, stroke: C.sheetLine, w: 1 },
  ];
}

/** Папка (закрытая). */
function folder(dx = 0, dy = 0) {
  const t = (s) => s.replace(/(-?\d+(\.\d+)?),(-?\d+(\.\d+)?)/g, (_, a, __, b) => `${n(+a + dx)},${n(+b + dy)}`);
  return [
    { d: t('M1.5,3.5 L6,3.5 L7.5,5 L14.5,5 L14.5,13.5 L1.5,13.5 Z'), fill: C.folder, stroke: C.folderEdge, w: 1 },
    { d: t('M1.5,6.5 L14.5,6.5 L14.5,13.5 L1.5,13.5 Z'), fill: C.folderFront, stroke: C.folderEdge, w: 1 },
  ];
}

/** Стрелка «внутрь» ↙ в левом нижнем углу (поверх основной иконки). */
const arrowIn = [
  { d: 'M0.5,8.5 H7.5 V15.5 H0.5 Z', fill: '#FFFFFF' },
  { d: 'M6,10 L2,14', stroke: C.blue, w: 1.6, cap: 'round' },
  { d: 'M1.6,10.6 V14.4 H5.4', stroke: C.blue, w: 1.6, cap: 'round' },
];

/** Стрелка «наружу» ↗ в правом нижнем углу (поверх основной иконки). */
const arrowOut = [
  { d: 'M8.5,8.5 H15.5 V15.5 H8.5 Z', fill: '#FFFFFF' },
  { d: 'M10,14 L14,10', stroke: C.blue, w: 1.6, cap: 'round' },
  { d: 'M10.6,9.6 H14.4 V13.4', stroke: C.blue, w: 1.6, cap: 'round' },
];

// ---- Иконки ----
export const icons = {
  // Документы
  'Doc.Part': [...cube(8, 1.5, 6.5, BLUE)],
  'Doc.Assembly': [...cube(5.5, 1.5, 4.5, BLUE), ...cube(10.5, 5.5, 4.5, AMBER)],
  'Doc.Presentation': [
    { d: 'M5.5,9 L10.5,4.5', stroke: C.inkSoft, w: 1, dash: '1.5 1' },
    ...cube(11.5, 0.5, 3.5, AMBER),
    ...cube(5.5, 6, 4.5, BLUE),
  ],
  'Doc.Drawing': [
    { d: 'M1.5,2.5 H14.5 V13.5 H1.5 Z', fill: C.sheet, stroke: C.sheetLine, w: 1 },
    { d: 'M2.5,3.5 H13.5 V12.5 H2.5 Z', stroke: C.sheetText, w: 1 },
    { d: 'M8.5,10 H13.5 V12.5 H8.5 Z', fill: C.blueDark },
    { d: 'M4,5 H8 V9 H4 Z', stroke: C.blue, w: 1 },
    { d: 'M9.5,5 H12 V8 H9.5 Z', stroke: C.blue, w: 1 },
  ],
  'Doc.Project': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,5.5 H8.5 M4.5,7.5 H9.5 M4.5,9.5 H7', stroke: C.sheetText, w: 1 },
    ...cube(11.5, 7.5, 3.5, BLUE),
  ],
  'Doc.Unknown': [
    ...sheet(),
    { d: 'M5.5,6.5 H10.5 M5.5,8.5 H10.5 M5.5,10.5 H8.5', stroke: C.sheetText, w: 1 },
  ],
  'Folder': [...folder()],
  'Folder.Open': [
    { d: 'M1.5,3.5 L6,3.5 L7.5,5 L13.5,5 L13.5,7.5 L1.5,13.5 Z', fill: C.folder, stroke: C.folderEdge, w: 1 },
    { d: 'M1.5,13.5 L4,7.5 L15.5,7.5 L13,13.5 Z', fill: C.folderFront, stroke: C.folderEdge, w: 1 },
  ],

  // Команды
  'Cmd.Refresh': [
    { d: 'M13,8 A5,5 0 1 1 11.54,4.46', stroke: C.blue, w: 1.6, cap: 'round' },
    { d: 'M13.3,6 L9.6,5.2 L12.4,2.4 Z', fill: C.blue, stroke: C.blue, w: 0.6 },
  ],
  'Cmd.Search': [
    { d: 'M10.5,6.5 A4,4 0 1 1 2.5,6.5 A4,4 0 1 1 10.5,6.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1.4 },
    { d: 'M9.6,9.6 L14,14', stroke: C.ink, w: 2, cap: 'round' },
  ],
  'Cmd.CollapseAll': [
    { d: 'M4.5,1.5 H14.5 V11.5 H4.5 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 1 },
    { d: 'M1.5,4.5 H11.5 V14.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M4,9.5 H9', stroke: C.blue, w: 1.6, cap: 'round' },
  ],
  'Cmd.FollowActive': [
    { d: 'M12.5,8 A4.5,4.5 0 1 1 3.5,8 A4.5,4.5 0 1 1 12.5,8 Z', stroke: C.ink, w: 1.2 },
    { d: 'M8,0.5 V4 M8,12 V15.5 M0.5,8 H4 M12,8 H15.5', stroke: C.ink, w: 1.2 },
    { d: 'M9.75,8 A1.75,1.75 0 1 1 6.25,8 A1.75,1.75 0 1 1 9.75,8 Z', fill: C.blue },
  ],
  'Cmd.Open': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,5.5 H8.5 M4.5,7.5 H8.5', stroke: C.sheetText, w: 1 },
    ...arrowOut,
  ],
  'Cmd.OpenFolder': [
    { d: 'M1.5,3.5 L6,3.5 L7.5,5 L13.5,5 L13.5,7.5 L1.5,13.5 Z', fill: C.folder, stroke: C.folderEdge, w: 1 },
    { d: 'M1.5,13.5 L4,7.5 L15.5,7.5 L13,13.5 Z', fill: C.folderFront, stroke: C.folderEdge, w: 1 },
  ],
  'Cmd.OpenProjectFile': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,5.5 H8.5 M4.5,7.5 H9.5 M4.5,9.5 H7', stroke: C.sheetText, w: 1 },
    ...cube(11.5, 7.5, 3.5, BLUE),
  ],
  'Cmd.Recent': [
    { d: 'M14.5,8 A6.5,6.5 0 1 1 1.5,8 A6.5,6.5 0 1 1 14.5,8 Z', fill: '#FFFFFF', stroke: C.ink, w: 1.2 },
    { d: 'M8,4 V8 L10.8,9.8', stroke: C.blue, w: 1.5, cap: 'round' },
  ],
  'Cmd.ShowInWindows': [...folder(0, 0), ...arrowOut],
  'Cmd.CopyPath': [
    { d: 'M5.5,1.5 H12 L14.5,4 V11.5 H5.5 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 1 },
    { d: 'M1.5,4.5 H8 L10.5,7 V14.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M3.5,9.5 H8.5 M3.5,11.5 H7', stroke: C.blue, w: 1 },
  ],
  'Cmd.ProjectTree': [
    { d: 'M2.5,1.5 H7.5 V5.5 H2.5 Z', fill: C.folderFront, stroke: C.folderEdge, w: 1 },
    { d: 'M5,5.5 V13 H8.5 M5,8.5 H8.5', stroke: C.inkSoft, w: 1 },
    { d: 'M8.5,6.5 H14.5 V10.5 H8.5 Z', fill: C.blueLight, stroke: C.blueEdge, w: 1 },
    { d: 'M8.5,11 H14.5 V15 H8.5 Z', fill: C.amberLight, stroke: C.amberEdge, w: 1 },
  ],
  'Cmd.RevealInExplorer': [
    { d: 'M1.5,2.5 H12.5 V12.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M1.5,2.5 H12.5 V5 H1.5 Z', fill: C.app, stroke: C.appEdge, w: 1 },
    { d: 'M3,7 H6 M3,9 H8 M3,11 H6', stroke: C.sheetText, w: 1 },
    ...arrowOut,
  ],
  'Cmd.InventorProject': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,5.5 H8.5 M4.5,7.5 H9.5', stroke: C.sheetText, w: 1 },
    ...cube(11.5, 7.5, 3.5, BLUE),
    ...arrowIn,
  ],
  'Cmd.Modules': [
    { d: 'M1.5,1.5 H7 V7 H1.5 Z', fill: C.blueLight, stroke: C.blueEdge, w: 1 },
    { d: 'M9,1.5 H14.5 V7 H9 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 1 },
    { d: 'M1.5,9 H7 V14.5 H1.5 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 1 },
    { d: 'M9,9 H14.5 V14.5 H9 Z', fill: C.blueDark, stroke: C.blueEdge, w: 1 },
  ],
  'Cmd.Info': [
    { d: 'M14.5,8 A6.5,6.5 0 1 1 1.5,8 A6.5,6.5 0 1 1 14.5,8 Z', fill: C.blue },
    { d: 'M7.1,7 H8.9 V12 H7.1 Z', fill: '#FFFFFF' },
    { d: 'M9,4.6 A1,1 0 1 1 7,4.6 A1,1 0 1 1 9,4.6 Z', fill: '#FFFFFF' },
  ],

  // Библиотека
  'Cmd.Library': [
    { d: 'M1.5,2.5 H4.5 V14.5 H1.5 Z', fill: C.blueLight, stroke: C.blueEdge, w: 1 },
    { d: 'M5.5,2.5 H8.5 V14.5 H5.5 Z', fill: C.amberLight, stroke: C.amberEdge, w: 1 },
    { d: 'M9.6,3.6 L12.5,2.8 L15.2,13.6 L12.3,14.4 Z', fill: C.blueMid, stroke: C.blueEdge, w: 1 },
    { d: 'M2.5,5 H3.5 M6.5,5 H7.5', stroke: C.ink, w: 1 },
  ],
  'Cmd.LibraryAdd': [
    { d: 'M1.5,2.5 H4.5 V14.5 H1.5 Z', fill: C.blueLight, stroke: C.blueEdge, w: 1 },
    { d: 'M5.5,2.5 H8.5 V14.5 H5.5 Z', fill: C.amberLight, stroke: C.amberEdge, w: 1 },
    { d: 'M8.5,7.5 H15.5 V15.5 H8.5 Z', fill: '#FFFFFF' },
    { d: 'M12,9 V14 M9.5,11.5 H14.5', stroke: C.green, w: 1.8, cap: 'round' },
  ],
  'Cmd.AddFiles': [
    ...sheet(2.5, 1.5, 11.5, 14.5),
    { d: 'M4.5,5.5 H8.5 M4.5,7.5 H8.5', stroke: C.sheetText, w: 1 },
    { d: 'M8.5,8.5 H15.5 V15.5 H8.5 Z', fill: '#FFFFFF' },
    { d: 'M12,9.5 V14.5 M9.5,12 H14.5', stroke: C.green, w: 1.8, cap: 'round' },
  ],
  'Cmd.AddFolder': [
    ...folder(),
    { d: 'M8.5,8.5 H15.5 V15.5 H8.5 Z', fill: '#FFFFFF' },
    { d: 'M12,9.5 V14.5 M9.5,12 H14.5', stroke: C.green, w: 1.8, cap: 'round' },
  ],
  'Cmd.Exclude': [
    { d: 'M14.5,8 A6.5,6.5 0 1 1 1.5,8 A6.5,6.5 0 1 1 14.5,8 Z', fill: '#FFFFFF', stroke: C.inkSoft, w: 1.2 },
    { d: 'M3.6,12.4 L12.4,3.6', stroke: C.red, w: 1.6, cap: 'round' },
  ],
  'Cmd.Include': [
    { d: 'M2.5,8.5 L6.5,12.5 L13.5,4', stroke: C.green, w: 2, cap: 'round' },
  ],
  'Cmd.Check': [
    { d: 'M1.5,2.5 H10.5 V13.5 H1.5 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
    { d: 'M3.5,5.5 H8.5 M3.5,8 H8.5 M3.5,10.5 H6.5', stroke: C.sheetText, w: 1 },
    { d: 'M8,11 L10.5,13.5 L15,7.5', stroke: C.green, w: 1.8, cap: 'round' },
  ],
  'Cmd.Settings': [
    { d: 'M8,1.5 L9.3,1.7 L9.6,3.3 L10.8,3.8 L12.1,2.8 L13.2,3.9 L12.2,5.2 L12.7,6.4 L14.3,6.7 L14.5,8 L14.3,9.3 L12.7,9.6 L12.2,10.8 L13.2,12.1 L12.1,13.2 L10.8,12.2 L9.6,12.7 L9.3,14.3 L8,14.5 L6.7,14.3 L6.4,12.7 L5.2,12.2 L3.9,13.2 L2.8,12.1 L3.8,10.8 L3.3,9.6 L1.7,9.3 L1.5,8 L1.7,6.7 L3.3,6.4 L3.8,5.2 L2.8,3.9 L3.9,2.8 L5.2,3.8 L6.4,3.3 L6.7,1.7 Z', fill: '#E8E8E8', stroke: C.ink, w: 1 },
    { d: 'M10.25,8 A2.25,2.25 0 1 1 5.75,8 A2.25,2.25 0 1 1 10.25,8 Z', fill: '#FFFFFF', stroke: C.ink, w: 1 },
  ],
  'Cmd.Database': [
    { d: 'M2.5,4 V12 A5.5,2 0 0 0 13.5,12 V4 Z', fill: C.blueLight, stroke: C.blueEdge, w: 1 },
    { d: 'M13.5,4 A5.5,2 0 1 1 2.5,4 A5.5,2 0 1 1 13.5,4 Z', fill: '#FFFFFF', stroke: C.blueEdge, w: 1 },
    { d: 'M2.5,8 A5.5,2 0 0 0 13.5,8', stroke: C.blueEdge, w: 1 },
  ],
  'State.Info': [
    { d: 'M14.5,8 A6.5,6.5 0 1 1 1.5,8 A6.5,6.5 0 1 1 14.5,8 Z', fill: '#FFFFFF', stroke: C.blue, w: 1.2 },
    { d: 'M7.2,7 H8.8 V11.5 H7.2 Z', fill: C.blue },
    { d: 'M8.9,4.8 A0.9,0.9 0 1 1 7.1,4.8 A0.9,0.9 0 1 1 8.9,4.8 Z', fill: C.blue },
  ],
  'State.Ok': [
    { d: 'M14.5,8 A6.5,6.5 0 1 1 1.5,8 A6.5,6.5 0 1 1 14.5,8 Z', fill: C.green },
    { d: 'M4.8,8.2 L7,10.4 L11.2,5.8', stroke: '#FFFFFF', w: 1.8, cap: 'round' },
  ],

  // Состояния
  'State.Online': [
    { d: 'M12.5,8 A4.5,4.5 0 1 1 3.5,8 A4.5,4.5 0 1 1 12.5,8 Z', fill: C.green, stroke: '#1F5E2C', w: 1 },
  ],
  'State.Offline': [
    { d: 'M12.5,8 A4.5,4.5 0 1 1 3.5,8 A4.5,4.5 0 1 1 12.5,8 Z', fill: '#FFFFFF', stroke: C.gray, w: 1.2 },
  ],
  'State.Warning': [
    { d: 'M8,1.5 L15,14.5 H1 Z', fill: '#F2B713', stroke: '#9A6F00', w: 1 },
    { d: 'M7.2,5.5 H8.8 V10 H7.2 Z M7.2,11.2 H8.8 V12.8 H7.2 Z', fill: C.ink },
  ],
  'State.Error': [
    { d: 'M14.5,8 A6.5,6.5 0 1 1 1.5,8 A6.5,6.5 0 1 1 14.5,8 Z', fill: C.red },
    { d: 'M5.5,5.5 L10.5,10.5 M10.5,5.5 L5.5,10.5', stroke: '#FFFFFF', w: 1.8, cap: 'round' },
  ],

  // Приложение
  'App.PdmLite': [
    { d: 'M2.5,0.5 H13.5 A2,2 0 0 1 15.5,2.5 V13.5 A2,2 0 0 1 13.5,15.5 H2.5 A2,2 0 0 1 0.5,13.5 V2.5 A2,2 0 0 1 2.5,0.5 Z', fill: C.app, stroke: C.appEdge, w: 1 },
    { d: 'M8,2.5 L13,5 L8,7.5 L3,5 Z', fill: '#FFFFFF' },
    { d: 'M3,5 L8,7.5 V13.5 L3,11 Z', fill: '#BFD3EC' },
    { d: 'M8,7.5 L13,5 V11 L8,13.5 Z', fill: '#8FB1DC' },
  ],
};
