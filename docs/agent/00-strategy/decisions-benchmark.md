# Бенчмарк decision-моделей OpenRouter (2026-11-18)

Проверка JEV-аналогов (Decisions API) на кейсах SmartCon Agent: маршрутизация
инструментов (Choice по каталогу из 22 инструментов) и risk-gate (Noul
«безопасно ли применить без подтверждения»).

## Методика

- Endpoint: `POST https://openrouter.ai/api/alpha/decisions`, ключ — общий
  OpenRouter (`SMARTCON_OPENROUTER_API_KEY`), в отчётах не сохраняется.
- 16 русских routing-запросов (переименование, подрезка, видимость, шаблон,
  копирование настроек, параметры, undo/discard, план из нескольких шагов).
- 10 risk-кейсов: 5 безопасных (3–12 элементов, reversible) и 5 опасных
  (900–5000 элементов, destructive, необратимые).
- Метрики: top-1 routing accuracy (допустимые альтернативы), strict accuracy,
  separation = mean(p(safe)) − mean(p(unsafe)), misclassified risk, latency
  p50/p95, фактическая стоимость из `usage.cost`.
- 27 моделей из каталога `output_modalities=decisions`; 26–27 запросов на модель.

## Результаты (лидеры)

| Модель | Routing | Strict | Separation | Risk err | p50 | Стоимость 26 запросов |
|---|---|---|---|---|---|---|
| `morph/reflex` | 100 % | 94 % | 1.000 | 0 | 392 ms | $0.00042 |
| `microsoft/microsoft-decision-1.1` | 100 % | 93 % | 1.000 | 0 | 366 ms | $0.00032 |
| `microsoft/microsoft-decision-1` | 100 % | 94 % | 0.999 | 0 | 379 ms | $0.00031 |
| `respan/span-02` | 100 % | 94 % | 0.997 | 0 | 862 ms | $0.00010 |
| `nace-ai/drex-max` | 100 % | 100 % | 0.997 | 0 | 373 ms | $0.00064 |
| **`perplexity/pplx-decider-v1.3:free`** | **100 %** | **100 %** | **0.990** | **0** | **451 ms** | **$0** |
| `~perplexity/pplx-decider-latest` (v1.2) | 100 % | 100 % | 0.973 | 0 | 375 ms | $0.00017 |
| `liquid/d1-ultra` | 100 % | 100 % | 0.941 | 0 | 289 ms | $0.00028 |
| `cloudflare/clef` | 100 % | 94 % | 0.930 | 0 | 553 ms | $0.00319 |

Аутсайдеры: `upstage/solar-decide-flash` 56 % routing, `jaredpalmer/kev-4b` 81 %,
`togethercomputer/tev1-4b-experimental` 0 % (отвечает, но стабильно другая
семантика categories), `baseten/*`, `cloudflare/clairic` — провайдеры не отвечали.

## Недоступно из региона

- `typesafe/jev-1.13`, `typesafe/jev-1.14`, `~typesafe/jev-latest` — HTTP 451
  «Typesafe is not available in your region» (нужен VPN; остаётся альтернативным
  провайдером в `AgentDecisionsSettings`).
- `openai/gpt-6-luna-decisions` — HTTP 502 (провайдер вернул ошибку).
- `google/gemini-decision-1` — HTTP 400 «User location is not supported».

## Вывод

Дефолт production остаётся `perplexity/pplx-decider-v1.3:free`: на наших кейсах
он уже на потолке точности (100 %/100 %, risk separation 0.990) и бесплатен.
Платные модели не дают выигрыша в точности на этом наборе; лучшие кандидаты как
fallback при недоступности free-модели:

1. `respan/span-02` — самый дешёвый из лидеров, но выше p50.
2. `morph/reflex` — лучший separation, приемлемая латентность.
3. `nace-ai/drex-max` — 100 % strict, дороже.
4. `~perplexity/pplx-decider-latest` — та же семья, платная v1.2 (внимание:
   alias сейчас указывает на v1.2, а не v1.3).

Для смены модели достаточно `SMARTCON_DECIDER_MODEL=<slug>` (Endpoint/provider
остаются OpenRouter). Порог auto (`DecisionsActionRiskGate.AutoThreshold=0.85`)
на этом наборе разделяет safe/unsafe без ошибок у всех лидеров; при переходе на
другую модель порог нужно перекалибровать.

## Воспроизведение

Кейсы и скрипт бенчмарка выполнялись вне репозитория (Node.js, `fetch`,
`--dns-result-order=ipv4first`); ключ берётся из `secrets.env` и в вывод не
попадает. Логи и `bench.json` — в `%TEMP%\smartcon-decider-bench\`. Повторный
прогон занимает ~2–3 минуты и стоит < $0.01 на всём наборе моделей.
