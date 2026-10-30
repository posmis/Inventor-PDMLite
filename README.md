# PdmLite

Мини-PDM для Autodesk Inventor 2025: плагин (add-in) для Inventor и внешнее приложение
**PdmLite Explorer** для просмотра проекта.

> Статус: **этап 1, шаг 1 — каркас.** Реализованы дерево проекта, карточка файла,
> связь плагина и Explorer. Код собирается и покрыт тестами, но **в реальном Inventor ещё
> не запускался** — первая проверка на рабочем месте. iProperties, PostgreSQL и переименование —
> следующие шаги.

## Что реализовано

**Плагин в Inventor**
- Вкладка **PdmLite** на ленте (во всех средах): «Дерево проекта», «Показать в Explorer», «Обновить проект».
- Закрепляемое окно PdmLite: дерево активного проекта Inventor (папки и файлы
  `.ipt/.iam/.idw/.dwg/.ipn`), поиск, карточка выбранного файла.
- Активный документ Inventor выделяется в дереве; двойной щелчок открывает документ.
- При смене проекта Inventor дерево перестраивается.

**PdmLite Explorer**
- Открытие проекта: папка или файл `.ipj`; недавние проекты; F5 — обновить.
- То же дерево и та же карточка, что в плагине (общие модули).
- Строка состояния: связь с Inventor и активный документ.
- Двойной щелчок открывает документ **в уже запущенном Inventor**; если Inventor не запущен,
  файл открывается через Windows.
- «Сервис → Открыть активный проект Inventor».
- Справка → Модули: какие модули загружены.

**Связь между ними**: «Показать в Explorer» в Inventor открывает Explorer (или передаёт
команду уже запущенному) и выделяет активный документ. Подробнее — в [архитектуре](docs/ARCHITECTURE.md#связь-плагина-и-explorer).

## Сборка и установка

Требуется Windows, **.NET 8 SDK** и **Inventor 2025** (для сборки плагина нужна
`Autodesk.Inventor.Interop.dll` из папки Inventor).

```powershell
# сборка и установка для текущего пользователя (Inventor и Explorer должны быть закрыты)
powershell -ExecutionPolicy Bypass -File deploy\install.ps1
```

После установки запустите Inventor: появится вкладка **PdmLite**. Explorer:
`%LOCALAPPDATA%\PdmLite\Explorer\PdmLite.Explorer.exe`.
Журналы: `%LOCALAPPDATA%\PdmLite\logs`. Удаление: `deploy\uninstall.ps1`.

Разработка:

```powershell
dotnet build PdmLite.sln
dotnet test
```

Если Inventor установлен не в стандартную папку:
`dotnet build -p:InventorInteropPath="D:\...\Autodesk.Inventor.Interop.dll"`.

## Структура

```
src/
  PdmLite.Core         ядро: домен, контракты, шина событий, правила имён (без Inventor и WPF)
  PdmLite.Link         связь плагин ⇄ Explorer (именованные каналы, JSON)
  PdmLite.Hosting      загрузка модулей, DI, журналы, настройки
  PdmLite.UI.Shared    общие WPF-компоненты, тема оформления и иконки
  PdmLite.AddIn        хост: плагин Inventor
  PdmLite.Explorer     хост: внешнее приложение
modules/
  PdmLite.Modules.ProjectTree    дерево проекта
  PdmLite.Modules.Properties     карточка документа
  PdmLite.Modules.InventorLink   связь Explorer с Inventor
tests/
  PdmLite.Core.Tests
tools/icons/           источник иконок и генератор (Icons.xaml, .ico, предпросмотр)
deploy/                манифест плагина, установка
```

Модули включаются и отключаются в `modules.json` рядом с плагином и Explorer.

## Стек

| Что | Чем |
|---|---|
| Платформа | .NET 8, C# |
| CAD | Autodesk Inventor 2025 (Inventor API), Apprentice Server (Inventor View) |
| UI | WPF + MVVM (CommunityToolkit.Mvvm) |
| БД | PostgreSQL + EF Core (Npgsql) — следующий шаг |
| DI / журналы | Microsoft.Extensions.DependencyInjection, Serilog |
| Тесты | xUnit |

## Документация

- [Архитектура](docs/ARCHITECTURE.md)
- [Стиль оформления и иконки](docs/STYLE.md)
- [Принятые решения и открытые вопросы](docs/DECISIONS.md)
