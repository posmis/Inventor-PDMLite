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

## Планируемая структура решения

```
PdmLite.sln
├─ src/
│  ├─ PdmLite.Core/          # домен, контракты, шина событий, операции
│  ├─ PdmLite.Data/          # EF Core, миграции, PostgreSQL
│  ├─ PdmLite.Hosting/       # загрузчик модулей, DI, настройки, логи
│  ├─ PdmLite.UI.Shared/     # общие WPF-стили, контролы, диалоги
│  ├─ PdmLite.Inventor/      # реализация сервисов через Inventor API
│  ├─ PdmLite.Apprentice/    # реализация сервисов через Apprentice
│  ├─ PdmLite.AddIn/         # хост: плагин Inventor
│  └─ PdmLite.Explorer/      # хост: внешнее приложение
├─ modules/
│  ├─ PdmLite.Modules.ReferenceIndex/
│  ├─ PdmLite.Modules.ProjectTree/
│  ├─ PdmLite.Modules.Properties/
│  ├─ PdmLite.Modules.Preview/
│  └─ PdmLite.Modules.Rename/
├─ tests/
└─ deploy/                   # .addin-манифест, установка
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

## Модули этапа 1

| Модуль | Назначение |
|---|---|
| **ReferenceIndex** | Сканирование проекта, индекс ссылок «кто → на кого», «где используется» |
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
