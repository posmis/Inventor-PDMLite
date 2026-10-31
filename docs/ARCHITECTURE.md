# Архитектура PdmLite

## Принципы

1. **Ядро не знает об Inventor и о конкретных модулях.** Все контракты — в `PdmLite.Core`.
2. **Модули не ссылаются друг на друга.** Общение — через интерфейсы ядра и шину событий.
3. **Один набор модулей — два хоста:** плагин Inventor и внешнее приложение Explorer.
4. **Inventor API изолирован** в адаптере — ядро и модули тестируются без Inventor.
5. **Массовые операции** (переименование и т.п.) — пошаговые, с резервной копией и откатом.

## Компоненты

```
          ┌──────────────────────┐      ┌──────────────────────┐
 Хосты:   │ PdmLite.AddIn        │      │ PdmLite.Explorer     │
          │ (внутри Inventor)    │      │ (WPF-приложение)     │
          └──────────┬───────────┘      └──────────┬───────────┘
                     │   загружают одни и те же модули
                     ▼                             ▼
          ┌───────────────────────────────────────────────────┐
          │ Модули: ProjectTree, Properties, ReferenceIndex,  │
          │         Rename, Preview, …                        │
          └───────────────────────┬───────────────────────────┘
                                  ▼
          ┌───────────────────────────────────────────────────┐
          │ PdmLite.Core — домен, контракты, события, операции│
          └───────────┬───────────────────────┬───────────────┘
                      ▼                       ▼
          PdmLite.Inventor          PdmLite.Apprentice
          (Inventor API)            (Apprentice API)
                      └───────────┬───────────┘
                                  ▼
                           PdmLite.Data (EF Core, PostgreSQL)
```

## Структура решения

```
PdmLite.sln
├─ src/
│  ├─ PdmLite.Core/          # домен, контракты, шина событий, правила имён
│  ├─ PdmLite.Link/          # связь плагин ⇄ Explorer
│  ├─ PdmLite.Hosting/       # загрузчик модулей, DI, настройки, журналы
│  ├─ PdmLite.UI.Shared/     # общие WPF-стили, размещение панелей, меню команд
│  ├─ PdmLite.AddIn/         # загрузчик плагина (его видит Inventor)
│  ├─ PdmLite.AddIn.Core/    # хост: плагин Inventor (в изолированном контексте)
│  ├─ PdmLite.Explorer/      # хост: внешнее приложение
│  ├─ PdmLite.Data/          # (план) EF Core, PostgreSQL
│  ├─ PdmLite.Inventor/      # (план) iProperties и ссылки через Inventor API
│  └─ PdmLite.Apprentice/    # (план) то же через Apprentice
├─ modules/
│  ├─ PdmLite.Modules.ProjectTree/
│  ├─ PdmLite.Modules.Properties/
│  ├─ PdmLite.Modules.InventorLink/   # только Explorer
│  ├─ PdmLite.Modules.References/     # связи: где используется, состав, индекс проекта
│  ├─ PdmLite.Modules.Preview/        # (план)
│  ├─ PdmLite.Modules.Rename/         # переименование и перемещение с переносом ссылок
│  └─ PdmLite.Modules.Scripts/        # внешние утилиты
├─ tests/
└─ deploy/                   # .addin-манифест, install.ps1
```

## Контракт модуля

```csharp
public interface IPdmModule
{
    ModuleInfo Info { get; }                      // Id, имя, версия, зависимости, SupportedHosts
    void ConfigureServices(IServiceCollection s); // регистрация своих сервисов
    void Initialize(IModuleContext ctx);          // подписки на события, регистрация UI
    void Shutdown();
}
```

- Включение/отключение — через `modules.json`; хост проверяет зависимости.
- UI модуль регистрирует через `IUiRegistry` (кнопки ленты, пункты контекстного меню, панели).
- Возможности хоста — через `IHostCapabilities` (`CanEditFiles`, `HasActiveDocument`, …):
  модуль сам скрывает недоступные действия.

## Загрузка плагина в Inventor

Inventor 2025 загружает плагины .NET 8 в общий контекст сборок, где уже есть его собственные
версии библиотек (например, Microsoft.Extensions.* 6.0). Чтобы версии плагина не конфликтовали
с ними, плагин разделён на две части:

```
<папка плагина>\
  PdmLite.AddIn.dll         загрузчик без зависимостей — его указывает PdmLite.addin
  core\
    PdmLite.AddIn.Core.dll  плагин
    PdmLite.*.dll, Microsoft.Extensions.*.dll, Serilog*.dll …  — свои версии
```

Загрузчик создаёт отдельный `AssemblyLoadContext` (`AddInLoadContext`) и загружает в него
плагин по его `.deps.json`. Общими с Inventor остаются только Inventor API и сборки .NET/WPF.
Вызовы `Activate`/`Deactivate` загрузчик передаёт плагину через интерфейс
`ApplicationAddInServer` из Inventor API.

## Связь плагина и Explorer

Два процесса связаны **именованными каналами Windows** (`PdmLite.Link`), сообщения — строки JSON.
Канал создаётся для текущего пользователя (`PipeOptions.CurrentUserOnly`), имя:
`PdmLite.{Inventor|Explorer}.{пользователь}`.

```
┌───────────── Inventor ─────────────┐            ┌──────────── PdmLite Explorer ───────────┐
│ PdmLite.AddIn                      │            │ модуль InventorLink                     │
│  InventorLinkServer  ◄─────────────┼── запросы ─┤  InventorConnection (переподключение)  │
│   ping, getStatus, openDocument    │            │   двойной щелчок → openDocument         │
│                      ──────────────┼── события ►│   события → шина (ActiveDocumentChanged)│
│   activeDocumentChanged,           │            │                                         │
│   documentSaved, documentClosed    │            │ ExplorerLinkServer                      │
│                                    │            │   openProject, revealDocument           │
│  «Показать в Explorer» ────────────┼── запрос ─►│   (и для второго экземпляра Explorer)   │
│   (или запуск Explorer с --reveal) │            │                                         │
└────────────────────────────────────┘            └─────────────────────────────────────────┘
```

- Explorer работает и без Inventor: связь восстанавливается автоматически, когда Inventor запускается.
- Плагин находит Explorer по пути, который Explorer записывает в `%AppData%\PdmLite\settings.json`
  при каждом запуске (или в папке установки `%LOCALAPPDATA%\PdmLite\Explorer`).
- Explorer — единственный экземпляр: повторный запуск передаёт аргументы работающему окну.
- Inventor API вызывается только в главном потоке Inventor (`IUiDispatcher`).
- Общие данные (документы, связи, журнал) появятся в PostgreSQL; канал — для оперативного
  взаимодействия («открой», «покажи», «что сейчас активно»).

## Модули этапа 1

| Модуль | Назначение |
|---|---|
| **References** ✔ | Индекс ссылок проекта в PostgreSQL (`project.files`, `project.file_references`, сервис `PdmLite.Index`): «из чего состоит», «где используется» (прямо и на всех уровнях), битые ссылки |
| **ProjectTree** | Дерево проекта: папки и структура изделия; `DockableWindow` в Inventor, главное окно в Explorer |
| **Properties** | Карточка документа; соответствие полей PDM и iProperties настраивается |
| **Preview** | Миниатюры, просмотр PDF чертежей |
| **Rename** | Переименование файлов и iProperties с перепривязкой ссылок и откатом |

### Операция переименования

```
Validate → BuildPlan → Preview (диалог) → Backup → Execute → UpdateIndex → Done
                                                     │ ошибка
                                                     ▼
                                                  Rollback
```

## Модель данных (этап 1, черновик)

```
Users          (Id, Login, PasswordHash, Role, IsActive)
Projects       (Id, Name, IpjPath, RootFolder)
Documents      (Id, ProjectId, Path, FileName, Type, Designation, Name, LastModified, Hash)
DocumentRefs   (ParentId, ChildId, RefType: Component | DrawingModel | Derived)
OperationLog   (Id, Date, UserId, Operation, Payload JSON, Result)
```

Расширяется без ломки схемы: `Versions`, `Lifecycle`, `Locks`, `Permissions`.

## Технические особенности

- Inventor API вызывается только из главного потока (`InventorDispatcher`).
- Тяжёлое сканирование — в фоне, с прогрессом и отменой.
- Apprentice требует установленного Inventor или Inventor View.
- Хранилище файлов скрыто за `IFileStore`, чтобы позже перейти на хранилище с версиями
  (check-in / check-out) без переделки модулей.

## Порядок реализации

1. Core, Hosting, каркасы AddIn и Explorer.
2. Data (PostgreSQL), адаптеры Inventor и Apprentice.
3. ReferenceIndex.
4. ProjectTree.
5. Properties, Preview.
6. Rename.
7. Тесты, установщик, документация.

## Индекс ссылок проекта

- Сканирование (Explorer, Apprentice): документы `.ipt/.iam/.idw/.dwg/.ipn` папки проекта без `OldVersions`;
  у каждого — обозначение, наименование и ссылки (`FileDescriptor`). Повторно читаются только файлы с другим
  размером или датой; пропавшие удаляются из индекса. Остановить можно в любой момент — прочитанное сохраняется пачками.
- При открытии уже проиндексированного проекта Explorer тихо дочитывает изменения в фоне.
- Пути сравниваются без учёта регистра и вида разделителя (`ReferenceIndexService.Key`).
- Панель «Связи» читает только базу — работает и в Inventor.
- Основа для переименования и обновления сборок: какие документы затронет изменение файла.


## Переименование и перемещение

Модуль `PdmLite.Modules.Rename` (окно и меню), логика — `PdmLite.Index/Operations` (без UI, с тестами):

- `FileOperationRequests` — правило имён (`FileNameBuilder`), связанные документы, списки «было → станет»
  для переименования и перемещения, iProperties.
- `ProjectFileOperations` — план и выполнение:
  1. **План** (только чтение): проверки (внутри проекта, имя свободно, нет недопустимых символов и
     двойных имён, длина пути, «только чтение») и кто ссылается на переносимые файлы — по индексу ссылок
     (перед этим он дочитывается) или, без базы, чтением всех документов проекта.
  2. **Выполнение**: документы, загруженные в Inventor, должны быть закрыты (`IOpenDocuments`: в Inventor
     они сохраняются, закрываются и после операции открываются снова; из Explorer — список через связь,
     закрывает пользователь) → копии изменяемых на месте документов и журнал — в
     `%LOCALAPPDATA%\PdmLite\operations\<время>` → файлы копируются по новым путям (старые пока на месте,
     ссылки на них находятся) → в каждом документе-родителе ссылки заменяются (Apprentice
     `FileDescriptor.ReplaceReference`) и **проверяются** чтением → пишутся iProperties → старые файлы удаляются,
     опустевшие папки убираются, индекс ссылок обновляется.
  3. Сбой до удаления старых файлов — **откат**: новые копии удаляются, изменённые документы
     восстанавливаются из копий.
