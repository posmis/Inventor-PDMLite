# Исправления ошибок восстановления и согласованности

Изменения после `7b5666c` закрывают шесть замечаний следующего ревью.

| Проблема | Изменение | Регрессионные проверки |
|---|---|---|
| Неудачный откат скрыт, копии удаляются | LibraryRollbackException содержит первичную ошибку, ошибки восстановления и пути копий. При неполном откате копии не удаляются; исправлены изменение, объединение и безопасная запись свойств | Edit_FailedRollbackKeepsCopiesAndReportsIncompleteRecovery; Merge_FailedAssemblyRollbackKeepsCopiesAndDoesNotHideOriginalFailure |
| Ненайденная ссылка допускает ложный список неиспользуемых | При неразрешённой ссылке поиск считается неполным. AnalysisVersion повышена: старые карточки требуют повторного анализа | UnresolvedMissingReference_DoesNotMarkExistingModelsAsUnused |
| События открытия проектов приходят не в порядке фиксации | Очередь событий заполняется вместе с фиксацией состояния; один издатель доставляет их последовательно без удержания блокировок во время вызова подписчиков | ConcurrentOpen_PublishesInCommitOrderEvenWhenFirstPublisherIsDelayed; ReentrantOpen_DoesNotDeadlockOrDeliverOlderEventLast |
| Ошибка подключения удаляет старую библиотечную привязку | ProjectLibraryUpdate восстанавливает старый путь при отказе Add или проверки. При провале восстановления сообщает обе ошибки и прежний путь | Binding_AddOrVerificationFailureRestoresPreviousPath; Binding_FailedRestoreReportsBothFailuresAndOldPath; Binding_NoPreviousPathCleansPartiallyAddedEntry; Binding_SamePathDoesNotDeleteExistingEntry |
| Ошибка очистки папки объявляет сохранение неуспешным | После фиксации БД очистка считается обслуживанием: её отказ записывается в журнал, сохранение остаётся успешным | Edit_CleanupFailureAfterCommitIsNotReportedAsFailedSave |
| Архив меняется между проверкой и восстановлением | В начале Restore создаётся отдельная копия ZIP, удерживаемая открытым чтением без разрешения записи/удаления. Проверка и извлечение используют только её; журнал сохраняет исходное имя архива | Restore_SourceArchiveChangedDuringSafetyBackupUsesValidatedSnapshot; Restore_SourceArchiveDeletedDuringSafetyBackupUsesValidatedSnapshot; Restore_PruneMayRemoveOriginalAutomaticArchiveButNotRestoreSnapshot |

Новый порядок доставки ProjectOpened допускает возврат конкурентного или повторного
OpenAsync до доставки его события, если другой издатель уже работает. Порядок событий
при этом соответствует порядку фиксации состояния. Блокировка не удерживается при
вызове подписчиков, поэтому повторное открытие из обработчика не создаёт deadlock.

Восстановление теперь не зависит от доступности исходного ZIP после начала операции.
Это также устраняет отказ, когда создание BeforeRestore запускает Prune и удаляет
выбранную старую автоматическую копию. Частная копия хранится в отдельной временной
папке и очищается при завершении, отмене и ошибке.

Добавлены 14 тестовых случаев. Локальный прогон: Core.Tests — 392, Library.Tests — 570,
всего 962 без ошибок и пропусков, с настоящей PostgreSQL. Тест отказа очистки на Linux
использует права каталога; на Windows — открытый дескриптор каталога без FileShare.Delete.

Файловые сбои и временные ZIP проверены реальными операциями файловой системы.
Inventor-привязка тестируется через тот же вынесенный механизм с отказами API;
сам Inventor COM в облачной Linux-среде недоступен. Полный Windows-прогон и реальный
Inventor необходимо оценивать отдельно от локального результата.
