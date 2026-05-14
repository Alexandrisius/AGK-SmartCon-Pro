# ADR-020: Binding-based локализация через LocExtension + TranslationSource

**Статус:** accepted
**Дата:** 2026-05-14
**Supersedes:** ADR-020 (версия от 2026-05-09 — DynamicResource + LocalizationBehavior)

## Контекст

Локализация SmartCon ранее использовала `{DynamicResource Key}` в XAML. Это требовало `Application.Current.Resources` для lookup chain. На net48 в Revit `Application.Current == null` — строки были пустыми.

Костыль `new Application()` в `LanguageManager.Initialize()` крашил Revit у тестеров, когда SmartCon загружался ДО других WPF-плагинов (второй `new Application()` → Dispatcher conflict → краш).

### Root cause

```
DynamicResource lookup chain:
  element.Resources → parent.Resources → ... → Application.Current.Resources
                                                        ↑
                                          NULL на net48 в Revit
```

На net8 (Revit 2025+) Revit сам создаёт `Application.Current` — проблем нет.
На net48 (Revit 2019-2024) Revit — нативное Win32 приложение, WPF Application не создаётся.

## Решение

### 1. LocExtension (MarkupExtension → Binding)

```xml
<!-- Было: -->
<TextBlock Text="{DynamicResource FM_SearchPlaceholder}"/>
<!-- Стало: -->
<TextBlock Text="{loc:Loc FM_SearchPlaceholder}"/>
```

`LocExtension.ProvideValue()` возвращает `BindingExpression` (не строку):
```csharp
public override object ProvideValue(IServiceProvider serviceProvider)
{
    return new Binding($"Item[{_key}]")
    {
        Source = TranslationSource.Instance,
        Mode = BindingMode.OneWay
    }.ProvideValue(serviceProvider);
}
```

Ключ: вызов `.ProvideValue(serviceProvider)` на Binding — иначе WPF получит объект `Binding` вместо `BindingExpression` и упадёт при установке string-свойств.

### 2. TranslationSource (INPC singleton)

```csharp
public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();
    
    public string this[string key] => StringLocalization.GetString(key, _currentLanguage);
    
    public void ChangeLanguage(Language language)
    {
        _currentLanguage = language;
        foreach (var key in StringLocalization.GetAllKeys())
            OnPropertyChanged($"Item[{key}]");
        OnPropertyChanged(string.Empty);
    }
}
```

НЕ зависит от `Application.Current`. Данные в памяти, Binding с явным `Source`.

### 3. Единый источник правды — LocalizationService (Core)

`LocalizationService` — единственный словарь локализации (~475 ключей × 2 языка). Разложен на partial classes по модулям:

| Файл | Содержимое |
|---|---|
| `LocalizationService.cs` | Логика: GetString, SetLanguage, Load/Save, Format |
| `LocalizationService.Keys.Common.cs` | Общие ключи: Btn_, Label_, Tip_, Col_, Tab_, Status_, Msg_, About_, Mapping_, Fitting_, Warn_ |
| `LocalizationService.Keys.PipeConnect.cs` | PipeConnect: Tx_, Pick_, Error_, Chain_, Status_* pipeconnect-specific |
| `LocalizationService.Keys.ProjectManagement.cs` | ProjectManagement: PM_* |
| `LocalizationService.Keys.FamilyManager.cs` | FamilyManager: FM_* |

`StringLocalization` (UI) — тонкая обёртка, делегирует в `LocalizationService`. Хранит только `Keys` класс (константы для backward compat).

### 4. Цепочка вызовов

```
XAML: {loc:Loc Key}
  → LocExtension → Binding("Item[Key]", Source=TranslationSource.Instance)
  → TranslationSource[key] → StringLocalization.GetString(key, lang)
  → LocalizationService.GetString(key, lang)

ViewModel: LanguageManager.GetString(key)
  → StringLocalization.GetString(key, lang)
  → LocalizationService.GetString(key, lang)

Core: LocalizationService.GetString(key)
  → _current dictionary lookup
```

### 5. Что НЕ меняется

- `{DynamicResource BackgroundBrush}` — кисти и стили остаются на DynamicResource (работают через SingletonResources)
- `LanguageManager.GetString()` — API для code-behind и ViewModel
- `StringLocalization.Keys` — константы ключей (backward compat)

### 6. Удалённый код

| Файл | Причина |
|---|---|
| `LocalizationBehavior.cs` | Мёртвый код — AutoRefresh больше не нужен |
| `LanguageManager.GetCurrentStrings()` | Использовался только из LocalizationBehavior |
| `LanguageManager.ApplyLanguage()` | ResourceDictionary больше не мержится в Application.Current |
| `StringLocalization.BuildResourceDictionary()` | ResourceDictionary больше не нужен |
| `new Application()` в LanguageManager | Крашил Revit |
| `StringLocalization.Ru/En` словари | Данные перенесены в LocalizationService |

## Последствия

**Плюсы:**
- Работает на net48 и net8 одинаково — нет зависимости от `Application.Current`
- Runtime-переключение языка мгновенное — INotifyPropertyChanged обновляет все Bindings
- Простой XAML: `{loc:Loc Key}` вместо `{DynamicResource Key}`
- Нет утечек памяти (нет WeakReference tracking)
- Нет краша от `new Application()`
- Единый словарь — нет дублирования ключей между Core и UI
- Декомпозиция по модулям — легко добавлять новый модуль

**Минусы:**
- Для VM-данных (дерево категорий, статус-строки) нужна подписка на `LanguageChanged` — но это не изменилось

## Альтернативы (отклонённые)

1. **`new Application()`** — крашил Revit при загрузке до других WPF-плагинов
2. **WPFLocalizationExtension (NuGet)** — внешняя зависимость, может конфликтовать в Revit
3. **Custom ResourceDictionary с INPC** — сложнее, чем Binding-based подход
4. **Resx + `x:Static`** — не поддерживает runtime-переключение языка

## Связанные инварианты

- **I-10**: MVVM строго. Локализация полностью в XAML через MarkupExtension.
- **I-01**: `LanguageManager` не вызывает Revit API.
- **I-09**: LocalizationService — чистый `Dictionary<string, string>`, без WPF-зависимостей.

## Связанные файлы

- `src/SmartCon.Core/Services/LocalizationService.cs` — логика
- `src/SmartCon.Core/Services/LocalizationService.Keys.Common.cs` — общие ключи
- `src/SmartCon.Core/Services/LocalizationService.Keys.PipeConnect.cs` — PipeConnect ключи
- `src/SmartCon.Core/Services/LocalizationService.Keys.ProjectManagement.cs` — PM ключи
- `src/SmartCon.Core/Services/LocalizationService.Keys.FamilyManager.cs` — FM ключи
- `src/SmartCon.UI/StringLocalization.cs` — обёртка + Keys класс
- `src/SmartCon.UI/Localization/TranslationSource.cs` — INPC singleton
- `src/SmartCon.UI/Localization/LocExtension.cs` — MarkupExtension
- `src/SmartCon.UI/LanguageManager.cs` — фасад для ViewModel
