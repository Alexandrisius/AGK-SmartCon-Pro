# ADR-021: Pre-release Versioning and Beta Release Strategy

## Status

accepted

## Context

SmartCon выпускает stable релизы из ветки `main` с семантическим версионированием (`v1.7.0`, `v1.8.0`).
Однако возникла потребность публиковать beta-версии из feature-веток для раннего тестирования желающими пользователями.

Требования:
- Beta-версии должны быть отделены от stable releases
- Пользователи должны иметь выбор: получать только stable или включать beta
- Updater должен корректно сравнивать версии с pre-release суффиксами (`1.8.0-beta.1`)
- CI должен собирать и тестировать pre-release теги

## Decision

### 1. SemVer Pre-release для тегов

Используем Semantic Versioning 2.0.0 pre-release суффиксы:
- Stable: `v1.8.0`
- Beta: `v1.8.0-beta.1`, `v1.8.0-beta.2`
- RC: `v1.8.0-rc.1`

**Правила сравнения:**
- `1.8.0-beta.1` < `1.8.0-beta.2` < `1.8.0`
- Stable всегда новее pre-release той же версии
- Pre-release сравниваются по частям: числа как числа, строки лексикографически

### 2. GitHub Releases — флаг `--prerelease`

Beta-релизы создаются с флагом `--prerelease` в `gh release create`:
- Не помечаются как "latest release"
- Видны на странице Releases с ярлыком "Pre-release"
- Пользователи с `IncludePrerelease=false` их не получают

### 3. Ручной выпуск beta из feature-веток

Beta-релизы выпускаются **вручную** через `release.ps1 -Prerelease`:
- Не обновляют `Version.txt`
- Автоматически определяют следующий номер (`beta.1`, `beta.2`...)
- Коммит в Version.txt пропускается
- Тег создаётся на текущем коммите feature-ветки

### 4. Собственный SemVersion парсер

Вместо внешней зависимости создан `SemVersion` в `SmartCon.Core.Models`:
- Без внешних NuGet-пакетов
- Поддерживает `net48` и `net8.0`
- Regex-based парсинг: `^(?\u003cmajor\u003e...)\.(?\u003cminor\u003e...)\.(?\u003cpatch\u003e...)(?:-(?\u003cprerelease\u003e...))?(?:\+(?\u003cmetadata\u003e...))?$`
- Реализует `IComparable\u003cSemVersion\u003e` с правильным SemVer precedence

### 5. Настройка IncludePrerelease

`UpdateSettings` расширен полем `IncludePrerelease` (default: `false`):
- Хранится в `update-settings.json`
- UI: чекбокс в AboutView
- `GitHubUpdateService.CheckForUpdateAsync()` фильтрует pre-release если флаг выключен
- `FetchAllAssetsFromLatestRelease` использует `/releases/latest` для stable, `/releases?per_page=1` для prerelease

### 6. CI/CD

`build.yml` расширен триггерами для pre-release тегов:
```yaml
tags: ['v*', 'v*-alpha*', 'v*-beta*', 'v*-rc*']
```

## Consequences

**Плюсы:**
- Чёткое разделение stable и beta каналов
- Пользователи контролируют, какие обновления получать
- Простой ручной процесс выпуска beta
- Нет внешних зависимостей для semver

**Минусы:**
- Нужно следить за максимальным размером `update-settings.json` (не добавлять слишком много полей)
- SemVersion не поддерживает произвольно длинные numeric identifiers (ограничение `int.TryParse`)

## References

- [SemVer 2.0.0](https://semver.org/)
- [GitHub Releases — Pre-releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)
- `tools/release.ps1` — параметры `-Prerelease`, `-PrereleaseLabel`
- `src/SmartCon.Core/Models/SemVersion.cs`
