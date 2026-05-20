# CI/CD — GitHub Actions

## Workflow-ы

| Файл | Триггер | Что делает |
|---|---|---|
| `build.yml` | push в main, PR, tags `v*` | Smart CI: если `src/**` не менялся — skip за 30 сек, иначе полная сборка 5 конфигураций + тесты |
| `codeql.yml` | push в main, еженедельно | Security scanning (C#). НЕ запускается на PR — информационный, не блокирует merge |
| `stale.yml` | ежедневно | Закрывает issues/PR без активности 30+ дней |

## Branch Protection на main

Ветка `main` защищена:
- Обязательны 2 umbrella-checks: `build-success` + `test-success` (всегда завершаются, даже если src не менялся)
- Review **не требуется** (solo-мейнтейнер не может одобрить свой PR — ограничение GitHub)
- `CODEOWNERS` файл существует для документации владения кодом и автоматических review-реквестов контрибьюторам
- Linear history (no merge commits) — только squash-merge
- Force push запрещён
- `enforce_admins: false` — владелец может bypass при необходимости
- **ВСЕГДА через PR** — по конвенции (AGENTS.md), даже если GitHub технически позволяет push напрямую
- При появлении контрибьюторов — пересмотреть review policy

## Workflow слияния feature → main

**Единственный допустимый путь: feature branch → PR → squash-merge через GitHub UI.**

**Если агент находится на `main` и пользователь просит изменить код:**
1. НЕ менять файлы на main
2. Создать feature-ветку: `git checkout -b feature/название`
3. Менять код, коммитить, создавать PR — всё на feature-ветке

**КРИТИЧЕСКОЕ ПРАВИЛО: НЕ создавать PR без явного запроса пользователя**

Если пользователь просит «коммит», «сохранить», «запушить» — агент делает только
`git add` → `git commit` → `git push` в **текущую ветку**. Никаких PR, squash-merge,
удаления веток и перехода на main — если пользователь НЕ просил «вмёржить в main».

Когда пользователь просит «закоммитить и вмёржить в main», агент **обязан**:

1. **Коммит** в feature-ветку (обычный commit, не amend)
2. **Push** feature-ветки в remote: `git push -u origin <branch>`
3. **Создать PR** через `gh pr create`:
   ```bash
   gh pr create --title "feat: описание" --base main --head <branch>
   ```
4. **Сообщить пользователю** ссылку на PR
5. **Дождаться** зелёных CI-чеков (`build-success` + `test-success`)
6. **Squash-merge** через `gh pr merge --squash` (НЕ merge, НЕ rebase)
7. **Удалить локальную feature-ветку**: `git checkout main && git pull && git branch -d <branch>`
   (remote-ветка удаляется автоматически GitHub — `delete_branch_on_merge: true`)

**Формат заголовка PR:** `feat: ...` | `fix: ...` | `chore: ...` | `docs: ...` (Conventional Commits)

**ЗАПРЕЩЕНО:**
- `git checkout main && git merge ...` — прямой merge в main
- `git push --force origin main` — force push на main (только при ликвидации мусора через API)
- `git config core.autocrlf ...` — менять глобальные настройки git
- Обход Branch Protection через `gh api` — только при крайней необходимости (ликвидация мусорных коммитов), с обязательным восстановлением

**Если CI упал в PR:**
- Не мёржить. Исправить на feature-ветке, push, ждать повторного CI.

## Dependabot — что ЗАПРЕЩЕНО обновлять

`dependabot.yml` уже игнорирует опасные пакеты. **Если агент меняет `Directory.Packages.props` — проверить:**
- `Microsoft.Extensions.DependencyInjection` — **ЗАМОРОЖЕН на 8.x** (9.0+ дропнул net48)
- `System.Text.Json` — **ЗАМОРОЖЕН на 8.x** (9.0+ может сломать net48)
- `Nice3point.Revit.Api.*` — версия определяется динамически из конфигурации

## Если CI упал

1. Прочитать лог: `gh run view <run_id> --log-failed`
2. 99% причин: `EnforceCodeStyleInBuild=true` + какой-то анализатор стал error
3. Решение: добавить severity override в `.editorconfig` (НЕ отключать `EnforceCodeStyleInBuild`)
4. НЕ трогать `Directory.Build.props` без крайней необходимости

## Release — как работает

1. **Локально:** `tools\release.bat` → `release.ps1` (инкремент версии, билд, тест, publish, ZIP, Inno Setup, git tag, push, GitHub Release)
2. **CI:** `build.yml` срабатывает на tag `v*` → валидирует сборку + тесты (не создаёт релиз)
3. **Единственный создатель релизов:** `release.ps1` — он же формирует changelog и загружает архивы
