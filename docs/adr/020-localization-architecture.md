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

### 3. Что НЕ меняется

- `{DynamicResource BackgroundBrush}` — кисти и стили остаются на DynamicResource (работают через SingletonResources)
- `LanguageManager.GetString()` — API для code-behind и ViewModel
- `LocalizationService` (Core) — строки для Revit API (Tx_, Status_, Error_*)
- `StringLocalization.Keys` — константы ключей

### 4. Удалённый код

| Файл | Причина |
|---|---|
| `LocalizationBehavior.cs` | Мёртвый код — AutoRefresh больше не нужен |
| `LanguageManager.GetCurrentStrings()` | Использовался только из LocalizationBehavior |
| `LanguageManager.ApplyLanguage()` | ResourceDictionary больше не мержится в Application.Current |
| `StringLocalization.BuildResourceDictionary()` | ResourceDictionary больше не нужен |
| `new Application()` в LanguageManager | Крашил Revit |

## Последствия

**Плюсы:**
- Работает на net48 и net8 одинаково — нет зависимости от `Application.Current`
- Runtime-переключение языка мгновенное — INotifyPropertyChanged обновляет все Bindings
- Простой XAML: `{loc:Loc Key}` вместо `{DynamicResource Key}`
- Нет утечек памяти (нет WeakReference tracking)
- Нет краша от `new Application()`

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

## Связанные файлы

- `src/SmartCon.UI/Localization/TranslationSource.cs`
- `src/SmartCon.UI/Localization/LocExtension.cs`
- `src/SmartCon.UI/LanguageManager.cs`
- `src/SmartCon.UI/StringLocalization.cs`
- `src/SmartCon.Core/Services/LocalizationService.cs`
