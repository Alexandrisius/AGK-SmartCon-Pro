# FamilyManager — Role-Based Access Control (RBAC)

> **Статус:** Утверждён
> **Дата:** 2026-05-15
> **Ветка:** `feature/fm-role-based-access`
> **ADR:** ADR-019 (будет создан)

---

## 1. Контекст и проблема

FamilyManager — модуль управления библиотекой семейств Revit с SQLite-каталогом. Текущая реализация **однопользовательская**: все подключившиеся к БД имеют полный доступ (импорт, редактирование, удаление, управление категориями).

**Цель:** добавить ролевую модель доступа, привязанную к конкретной БД, чтобы:
- проектировщики (Engineer) могли только просматривать каталог и загружать семейства в проект
- BIM-мастера (BimMaster) могли импортировать, редактировать, управлять категориями
- владелец БД (Owner) мог управлять пользователями и их ролями

**Ключевые требования:**
- opensource-дружелюбность — никаких серверов, аккаунтов, паролей
- корпоративная применимость — быстрое развёртывание на сетевых папках
- каждая БД — независимая «песочница» со своими пользователями
- любой пользователь может создать свою БД и стать её Owner

---

## 2. Принятые решения

### 2.1. Идентификация пользователя

| Аспект | Решение |
|---|---|
| Идентификатор | `"{Environment.UserName}@{Environment.MachineName}"` |
| DisplayName | Revit `Application.Username` (из API) |
| Обоснование | Работает оффлайн, без сервера, без логина, уникально в рамках сетевого окружения |

Формат: `ivan@WORKSTATION-01` — username Windows + имя машины.

**Почему не Autodesk LoginUserId:**
- Требует логин в Autodesk 360 (не всегда доступен)
- Ограничивает аудиторию (не у всех есть аккаунт)
- Добавляет зависимость от сети

### 2.2. Модель приглашения

**Auto-register** — при подключении к БД новый пользователь автоматически получает роль `Engineer`.

Flow:
1. Owner создаёт БД → становится Owner
2. Owner публикует путь к папке БД (сетевая папка, облако, USB)
3. Другой пользователь подключается через «Подключить БД»
4. Система автоматически создаёт запись `db_users` с ролью `Engineer`
5. Owner видит нового пользователя в панели «Профиль» → может повысить до BimMaster

### 2.3. Безопасность

Без паролей, доверяем локальной идентификации. Для каталога семейств (не банковская система) этого достаточно. Owner может заблокировать нежелательного пользователя (ban).

---

## 3. Доменная модель

### 3.1. Новые модели — `SmartCon.Core/Models/FamilyManager/`

#### `DbUserRole.cs` — enum

```csharp
public enum DbUserRole
{
    Owner = 0,
    BimMaster = 1,
    Engineer = 2
}
```

#### `DbUserStatus.cs` — enum

```csharp
public enum DbUserStatus
{
    Active = 0,
    Banned = 1
}
```

#### `DbUser.cs` — record

```csharp
public sealed record DbUser(
    string UserId,
    string DisplayName,
    DbUserRole Role,
    DbUserStatus Status,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset LastSeenAtUtc
);
```

#### `UserIdentity.cs` — record

```csharp
public sealed record UserIdentity(
    string UserId,
    string DisplayName,
    string MachineName,
    string UserName
);
```

### 3.2. Обновление `DatabaseConnection.cs`

Добавить свойства:

```csharp
public sealed record DatabaseConnection(
    string Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAtUtc,
    DbUserRole? CurrentUserRole = null,
    string? OwnerIdentity = null
);
```

### 3.3. Новые интерфейсы — `SmartCon.Core/Services/Interfaces/`

#### `IUserIdentityService.cs`

```csharp
public interface IUserIdentityService
{
    UserIdentity GetCurrentUser();
}
```

#### `IDbUserRepository.cs`

```csharp
public interface IDbUserRepository
{
    Task<DbUser?> GetUserAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<DbUser>> GetAllUsersAsync(CancellationToken ct = default);
    Task<DbUser> GetOrCreateUserAsync(UserIdentity identity, CancellationToken ct = default);
    Task<bool> UpdateUserRoleAsync(string userId, DbUserRole role, CancellationToken ct = default);
    Task<bool> UpdateUserStatusAsync(string userId, DbUserStatus status, CancellationToken ct = default);
    Task<bool> RemoveUserAsync(string userId, CancellationToken ct = default);
    Task<int> GetUserCountAsync(CancellationToken ct = default);
}
```

#### `IDbAccessControlService.cs`

```csharp
public interface IDbAccessControlService
{
    Task<DbUserRole> GetCurrentUserRoleAsync(CancellationToken ct = default);
    Task<DbUser> GetCurrentUserAsync(CancellationToken ct = default);
    bool CanImport { get; }
    bool CanEdit { get; }
    bool CanManageUsers { get; }
    bool CanLoadToProject { get; }
    bool IsOwner { get; }
    bool IsBanned { get; }
    Task RefreshCurrentUserAsync(CancellationToken ct = default);
}
```

---

## 4. SQLite Schema v7

### 4.1. Новая таблица `db_users`

```sql
CREATE TABLE IF NOT EXISTS db_users (
    user_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'Engineer',
    status TEXT NOT NULL DEFAULT 'Active',
    joined_at_utc TEXT NOT NULL,
    last_seen_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_db_users_role ON db_users (role);
CREATE INDEX IF NOT EXISTS ix_db_users_status ON db_users (status);
```

### 4.2. Обновление `database_meta`

```sql
ALTER TABLE database_meta ADD COLUMN owner_identity TEXT;
```

### 4.3. Миграция v7

В `LocalCatalogMigrator` добавить миграцию:
- Создать таблицу `db_users` + индексы
- Добавить `owner_identity` в `database_meta`
- Для существующих БД: текущий пользователь автоматически становится Owner

---

## 5. Реализации сервисов

### 5.1. `RevitUserIdentityService` — SmartCon.Revit

```
Расположение: SmartCon.Revit/FamilyManager/RevitUserIdentityService.cs
Зависимости: IRevitContext
Логика:
  UserId      = $"{Environment.UserName}@{Environment.MachineName}"
  DisplayName = _revitContext.Application.Username
  Fallback    = Environment.UserName (если Revit context недоступен)
```

### 5.2. `LocalDbUserRepository` — SmartCon.FamilyManager

```
Расположение: SmartCon.FamilyManager/Services/LocalCatalog/LocalDbUserRepository.cs
Зависимости: LocalCatalogDatabase, IUserIdentityService
```

**Логика `GetOrCreateUserAsync`:**
1. SELECT по `user_id`
2. Если найден и `status = Banned` → бросить `DbAccessDeniedException`
3. Если найден и `status = Active` → UPDATE `last_seen_at_utc`, вернуть DbUser
4. Если не найден → INSERT с `role = Engineer, status = Active`

### 5.3. `DbAccessControlService` — SmartCon.FamilyManager

```
Расположение: SmartCon.FamilyManager/Services/DbAccessControlService.cs
Зависимости: IDbUserRepository, IUserIdentityService
```

**Логика:**
- Кэширует `DbUser` для текущей БД
- `RefreshCurrentUserAsync()` — invalidate кэша при смене БД
- `IsBanned` → все `CanXxx` возвращают `false`
- Правила:
  - `CanImport` = Owner || BimMaster
  - `CanEdit` = Owner || BimMaster
  - `CanManageUsers` = Owner
  - `CanLoadToProject` = true (все роли, если не Banned)

---

## 6. UI: Toolbar

### 6.1. Текущий layout (6 колонок)

```
[ComboBox БД ▼] [+][✓][✗]  [      Поиск...        ] [Import File ▼]
   Col 0 (150)   C1 C2 C3        Col 4 (*)            Col 5 (Auto)
```

### 6.2. Новый layout (7 колонок)

```
[ComboBox БД ▼] [+][✓][✗]  [     Поиск...      ] [Import File ▼] [👤]
   Col 0 (150)   C1 C2 C3      Col 4 (*)          Col 5 (Auto)   Col 6 (Auto)
```

**Изменения:**

1. Добавить `ColumnDefinition Width="Auto"` для Col 6
2. Import area (Col 5) — Visibility привязан к `CanImport`:
   - Engineer: скрыт, поиск расширяется на всё пространство
   - Owner/BimMaster: виден
3. Profile кнопка (Col 6) — всегда видна, квадратная 28x26, SecondaryButton, иконка аватара

```xml
<!-- Col 5: Import — скрыт для Engineer -->
<Grid Grid.Column="5"
      Visibility="{Binding CanImport, Converter={StaticResource BoolToVis}}">
    ... существующий split-button Import ...
</Grid>

<!-- Col 6: Profile button -->
<Button Grid.Column="6"
        Command="{Binding OpenProfileCommand}"
        Width="28" Height="26"
        Style="{DynamicResource SecondaryButton}"
        Padding="0" ToolTip="Профиль">
    <Viewbox Width="12" Height="12" Stretch="Uniform">
        <Path Data="M 6,0 C 7.66,0 9,1.34 9,3 C 9,4.66 7.66,6 6,6
                     C 4.34,6 3,4.66 3,3 C 3,1.34 4.34,0 6,0 Z
                     M 6,7.5 C 9.31,7.5 12,8.84 12,10.5 L 12,12 L 0,12
                     L 0,10.5 C 0,8.84 2.69,7.5 6,7.5 Z"
              Fill="{DynamicResource TextPrimaryBrush}"/>
    </Viewbox>
</Button>
```

**Результат для Engineer:**
```
[ComboBox БД ▼] [+][✓][✗]  [           Поиск...               ] [👤]
```

**Результат для Owner/BimMaster:**
```
[ComboBox БД ▼] [+][✓][✗]  [     Поиск...      ] [Import File ▼] [👤]
```

---

## 7. UI: Profile Dialog

### 7.1. Окно `ProfileView.xaml`

**Тип:** `DialogWindowBase` (фирменный стиль SmartCon)
**Размеры:** Width=420, Height=520, ResizeMode=CanResize
**Ресурсы:** `ui:SingletonResources` (тема, стили)

### 7.2. Структура layout

```
Grid Margin="16"
├── Row 0 (Auto)  — Карточка текущего пользователя
├── Row 1 (Auto)  — Заголовок секции (если Owner)
├── Row 2 (*)     — DataGrid пользователей (если Owner)
└── Row 3 (Auto)  — [Закрыть] SecondaryButton, HorizontalAlignment=Right
```

### 7.3. Карточка текущего пользователя (Row 0)

```
Border (Background=SurfaceBrush, CornerRadius=6, BorderBrush=BorderAltBrush,
        BorderThickness=1, Margin=0,0,0,12)
  Grid (2 columns)
    Col 0:  Круг с инициалами
            - Width=40, Height=40, CornerRadius=20
            - Background=AccentBrush (#2979FF)
            - Text: Initials (1-2 буквы DisplayName), Foreground=White, Bold
    Col 1:  StackPanel Margin=12,0,0,0
            - TextBlock: DisplayName (Bold, TextPrimaryBrush)
            - TextBlock: "Роль: BIM-Мастер" (TextSecondaryBrush)
            - TextBlock: "ivan@WORKSTATION-01" (TextMutedBrush, FontSize=11)
```

### 7.4. Управление пользователями (Row 1-2, только для Owner)

**Заголовок (Row 1):**
```
StackPanel Margin=0,0,0,8
  TextBlock "Пользователи базы" (Bold, TextPrimaryBrush)
  TextBlock "{UserCount} пользователей" (TextMutedBrush, FontSize=11)
```

**DataGrid (Row 2):**

```
Border (Background=SurfaceBrush, CornerRadius=6, BorderBrush=BorderAltBrush)
  DataGrid
    - HeadersVisibility=None
    - BorderThickness=0
    - Row border: 0,0,0,1 BorderAltBrush
    - SelectionMode=Single
    - CanUserAddRows=false, CanUserDeleteRows=false
```

| Колонка | Ширина | Содержимое |
|---|---|---|
| Пользователь | * | DisplayName (Bold) + MachineId мелким шрифтом ниже |
| Роль | Auto | ComboBox: Owner (заблокирован) / BimMaster / Engineer |
| Доступ | Auto | ToggleButton: иконка замка (открыт/закрыт) — ban/unban |
| Действие | Auto | GhostButton с иконкой корзины — удалить из БД |

**Строка Owner — особая:**
- ComboBox Role: `IsEnabled=false`
- Кнопка удаления: `Visibility=Collapsed`
- Маленькая иконка короны (★) рядом с DisplayName

### 7.5. Для не-Owner (Row 1-2)

```
StackPanel VerticalAlignment=Center HorizontalAlignment=Center Margin=0,32,0,0
  TextBlock "Ваша роль: Инженер" (TextSecondaryBrush, FontSize=14)
  TextBlock "Доступны просмотр и загрузка семейств" (TextMutedBrush, Margin=0,4,0,0)
```

### 7.6. Кнопка (Row 3)

```
SecondaryButton "Закрыть" (MinWidth=80, HorizontalAlignment=Right, Margin=0,12,0,0)
```

Все изменения (роль, бан, удаление) применяются мгновенно — без кнопки «Сохранить».

---

## 8. UI: Status Bar — индикатор роли

Обновление status bar (Row 2) в `FamilyManagerPaneControl.xaml`:

Добавить колонку справа:
```xml
<TextBlock Grid.Column="3"
           Text="{Binding CurrentRoleDisplay}"
           Foreground="{DynamicResource TextMutedBrush}"
           VerticalAlignment="Center"
           Margin="8,0,0,0"
           FontSize="11"/>
```

`CurrentRoleDisplay` в ViewModel:
- Owner → "Владелец"
- BimMaster → "BIM-Мастер"
- Engineer → "Инженер"

---

## 9. UI: Диалог «Доступ запрещён»

При попытке заблокированного пользователя подключиться к БД:

```
DialogWindowBase
  StackPanel Margin=16
    TextBlock "Владелец базы данных \"{dbName}\" ограничил вам доступ."
              (TextPrimaryBrush, FontSize=14)
    TextBlock "Обратитесь к владельцу для получения разрешения."
              (TextSecondaryBrush, Margin=0,8,0,0)
    TextBlock "Владелец: {ownerDisplayName}"
              (TextMutedBrush, Margin=0,4,0,0)
  PrimaryButton "OK" (HorizontalAlignment=Right)
```

---

## 10. ViewModel: ProfileViewModel

### 10.1. Свойства

```csharp
[ObservableProperty] private DbUser _currentUser;
[ObservableProperty] private UserIdentity _currentUserIdentity;
[ObservableProperty] private bool _isOwner;
[ObservableProperty] private int _userCount;
[ObservableProperty] private string _initials;
[ObservableProperty] private ObservableCollection<DbUserItem> _users;
```

### 10.2. `DbUserItem` — row model для DataGrid

```csharp
public sealed class DbUserItem
{
    public string UserId { get; init; }
    public string DisplayName { get; init; }
    public string MachineId { get; init; }
    public DbUserRole Role { get; set; }
    public DbUserStatus Status { get; set; }
    public bool IsOwner { get; init; }
    public bool CanEditRole { get; init; }
    public bool CanDelete { get; init; }
    public IAsyncRelayCommand<DbUserRole> ChangeRoleCommand { get; }
    public IAsyncRelayCommand ToggleBanCommand { get; }
    public IAsyncRelayCommand DeleteUserCommand { get; }
}
```

### 10.3. Команды

| Команда | Действие |
|---|---|
| `ChangeRoleCommand` | `IDbUserRepository.UpdateUserRoleAsync` |
| `ToggleBanCommand` | Переключает `Active ↔ Banned` через `UpdateUserStatusAsync` |
| `DeleteUserCommand` | Подтверждение + `RemoveUserAsync` |
| `CloseCommand` | Закрыть окно |

---

## 11. Интеграция CanExecute в FamilyManagerMainViewModel

### 11.1. Новые свойства

```csharp
[ObservableProperty] private DbUserRole _currentUserRole;
[ObservableProperty] private bool _canImport;
[ObservableProperty] private bool _canEdit;
[ObservableProperty] private bool _canManageUsers;
[ObservableProperty] private string _currentRoleDisplay;
```

### 11.2. Новая команда

```csharp
[RelayCommand]
private async Task OpenProfileAsync(CancellationToken ct)
{
    // Создать ProfileViewModel через factory
    // Показать ProfileView через IDialogService
}
```

### 11.3. Обновление CanExecute-проверок

| Команда | Текущий gate | Новый gate |
|---|---|---|
| `ImportFilesCommand` | — | `CanImport` |
| `ImportFolderCommand` | — | `CanImport` |
| `ImportFileToCategoryCommand` | `selectedNode is CategoryNode` | `CanImport && selectedNode is CategoryNode` |
| `ImportFolderToCategoryCommand` | `selectedNode is CategoryNode` | `CanImport && selectedNode is CategoryNode` |
| `ImportDataForCategoryCommand` | — | `CanImport` |
| `ExtractTypesCommand` | — | `CanImport` |
| `ImportDataCommand` | — | `CanImport` |
| `EditMetadataCommand` | `SelectedItem != null` | `CanEdit && SelectedItem != null` |
| `UpdateFamilyCommand` | `SelectedItem != null` | `CanEdit && SelectedItem != null` |
| `DeleteFamilyCommand` | `SelectedItem != null` | `CanEdit && SelectedItem != null` |
| `OpenCategoryEditorCommand` | — | `CanEdit` |
| `DropFamilyCommand` | type check | `CanEdit && type check` |
| `DeleteDatabaseCommand` | — | `CanManageUsers` (только Owner) |
| `LoadToProjectCommand` | `CanLoadToProject` | без изменений |
| `LoadAndPlaceCommand` | `CanLoadToProject` | без изменений |
| `OpenProfileCommand` | — | всегда доступна |
| `DisconnectDatabaseCommand` | — | без изменений |

### 11.4. Обновление при смене БД

При `ActiveDatabaseChanged` → вызвать `RefreshCurrentUserAsync()` → обновить `CanImport`, `CanEdit`, `CanManageUsers`, `CurrentRoleDisplay`.

### 11.5. Контекстное меню TreeView

Пункты Import/Edit/Delete — Visibility привязан к `CanEdit`.
Пункты Load — видны всегда.

---

## 12. DI-регистрация

**`ServiceRegistrar.cs`:**

```csharp
services.AddSingleton<IUserIdentityService, RevitUserIdentityService>();
services.AddSingleton<IDbUserRepository, LocalDbUserRepository>();
services.AddSingleton<IDbAccessControlService, DbAccessControlService>();
```

**Обновление `FamilyManagerViewModelFactory`:**
- Добавить `IDbAccessControlService` в параметры `Create()`

**Обновление `IFamilyManagerDialogService`:**
- Добавить `ShowProfile(ProfileViewModel viewModel)` или использовать существующий pattern

---

## 13. Матрица прав (финальная)

| Операция | Owner | BimMaster | Engineer |
|---|---|---|---|
| Поиск/просмотр каталога | ✅ | ✅ | ✅ |
| Загрузка семейства в проект | ✅ | ✅ | ✅ |
| Экспорт метаданных | ✅ | ✅ | ✅ |
| Кнопка Import (toolbar) | ✅ видна | ✅ видна | 🚫 скрыта |
| Импорт файлов/папок | ✅ | ✅ | 🚫 |
| Редактирование метаданных | ✅ | ✅ | 🚫 |
| Управление категориями | ✅ | ✅ | 🚫 |
| Обновление версий | ✅ | ✅ | 🚫 |
| Экстракция данных | ✅ | ✅ | 🚫 |
| Управление ассетами | ✅ | ✅ | 🚫 |
| Удаление семейств | ✅ | ✅ | 🚫 |
| Управление пользователями | ✅ | 🚫 | 🚫 |
| Блокировка пользователей | ✅ | 🚫 | 🚫 |
| Удаление БД | ✅ | 🚫 | 🚫 |
| Профиль (просмотр) | ✅ | ✅ | ✅ |
| Подключение к БД | ✅ | ✅ | ✅ (auto-register) |
| Отключение БД | ✅ | ✅ | ✅ |

---

## 14. Edge Cases

1. **Существующие БД (schema < v7)**: миграция v7 делает первого открывшего пользователя Owner
2. **Единственный Owner**: нельзя понизить роль или удалить; Owner не может забанить сам себя
3. **Заблокированный пользователь**: при подключении — диалог «Доступ запрещён», соединение не создаётся в реестре
4. **Auto-register заблокированного**: `GetOrCreateUserAsync` бросает `DbAccessDeniedException` если `status = Banned`
5. **Multi-DB**: каждая БД — своя таблица `db_users`, независимые права
6. **Создание БД**: создатель автоматически Owner; `owner_identity` записывается в `database_meta`
7. **DisconnectDatabase**: Engineer может отключить БД от своего реестра (без удаления файлов)
8. **Контекстное меню**: Import/Edit/Delete скрываются для Engineer через Visibility binding
9. **Пустая БД (только Owner)**: Profile показывает только текущего пользователя

---

## 15. Фазы реализации

| # | Фаза | Ключевые файлы | Оценка |
|---|---|---|---|
| **P1** | ADR-019 + модели Core | `DbUserRole.cs`, `DbUserStatus.cs`, `DbUser.cs`, `UserIdentity.cs`, `IUserIdentityService.cs`, `IDbUserRepository.cs`, `IDbAccessControlService.cs` | 1 день |
| **P2** | Schema v7 + миграция | `FamilyCatalogSql.cs`, `LocalCatalogMigrator.cs` | 0.5 дня |
| **P3** | Сервисы | `RevitUserIdentityService.cs`, `LocalDbUserRepository.cs`, `DbAccessControlService.cs` | 1 день |
| **P4** | DI + DatabaseManager | `ServiceRegistrar.cs`, `DatabaseManager.cs`, `FamilyManagerViewModelFactory.cs` | 0.5 дня |
| **P5** | ViewModel: CanExecute + Profile | `FamilyManagerMainViewModel.cs`, `ProfileViewModel.cs`, `DbUserItem.cs` | 1 день |
| **P6** | UI: Toolbar + Profile Dialog | `FamilyManagerPaneControl.xaml`, `ProfileView.xaml(.cs)` | 1.5 дня |
| **P7** | Тесты | `DbAccessControlServiceTests`, `LocalDbUserRepositoryTests`, ViewModel tests | 1 день |
| **P8** | Документация | `docs/domain/models.md`, `docs/domain/interfaces.md`, ADR-019 | 0.5 дня |

**Итого: ~7 дней**

---

## 16. Новые файлы (полный список)

### SmartCon.Core
```
Models/FamilyManager/DbUserRole.cs
Models/FamilyManager/DbUserStatus.cs
Models/FamilyManager/DbUser.cs
Models/FamilyManager/UserIdentity.cs
Services/Interfaces/IUserIdentityService.cs
Services/Interfaces/IDbUserRepository.cs
Services/Interfaces/IDbAccessControlService.cs
```

### SmartCon.Revit
```
FamilyManager/RevitUserIdentityService.cs
```

### SmartCon.FamilyManager
```
Services/LocalCatalog/LocalDbUserRepository.cs
Services/DbAccessControlService.cs
ViewModels/ProfileViewModel.cs
ViewModels/DbUserItem.cs
Views/ProfileView.xaml
Views/ProfileView.xaml.cs
```

### SmartCon.Tests
```
FamilyManager/Rbac/DbAccessControlServiceTests.cs
FamilyManager/Rbac/LocalDbUserRepositoryTests.cs
FamilyManager/Rbac/ProfileViewModelTests.cs
```

---

## 17. Изменяемые файлы (полный список)

### SmartCon.Core
```
Models/FamilyManager/DatabaseConnection.cs          — добавить CurrentUserRole, OwnerIdentity
```

### SmartCon.FamilyManager
```
Services/LocalCatalog/FamilyCatalogSql.cs           — SQL для db_users + owner_identity
Services/LocalCatalog/LocalCatalogMigrator.cs       — миграция v7
Services/LocalCatalog/DatabaseManager.cs            — интеграция с IDbUserRepository
ViewModels/FamilyManagerMainViewModel.cs            — CanExecute, CanImport, CanEdit
ViewModels/FamilyManagerMainViewModel.Import.cs     — CanImport gates
ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs — CanEdit gates
ViewModels/FamilyManagerMainViewModel.Database.cs   — DeleteDatabase → CanManageUsers
Views/FamilyManagerPaneControl.xaml                 — Col 6 Profile, Visibility Import
Services/FamilyManagerDialogService.cs              — ShowProfile
Services/IFamilyManagerViewModelFactory.cs          — новый параметр
Services/FamilyManagerViewModelFactory.cs           — новый параметр
```

### SmartCon.App
```
DI/ServiceRegistrar.cs                              — регистрация новых сервисов
```

### SmartCon.Tests
```
FamilyManager/Repository/TempCatalogFixture.cs      — обновить для schema v7
```
