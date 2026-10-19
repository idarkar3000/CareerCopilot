# CareerCopilot

Bot que busca ofertas de trabajo, las puntúa con Gemini y avisa por Telegram.
Si una oferta pasa el mínimo de puntos, monta un CV de una página en PDF con
Typst y lo manda adjunto.

## Stack

- C# / .NET 10
- SQLite (Microsoft.Data.Sqlite)
- API de Gemini
- Typst + PdfPig para el PDF
- Telegram.Bot

## Estructura

- `CareerCopilot/` → servicio principal (worker + bot).
- `CareerCopilot/Endpoints/` → rutas HTTP (`/` y `/health`).
- `CareerCopilot/Models/` → modelos de dominio y config.
- `CareerCopilot/Repositories/` → acceso a datos (SQLite).
- `CareerCopilot/Services/` → scraping, scoring, sanitizer, compilador de CV, notificaciones.
- `CareerCopilot/Workers/` → `CareerCopilotWorker`, el proceso en segundo plano.
- `tests/CareerCopilot.Tests/` → tests con xUnit.

## Cómo funciona

1. El worker consulta varias fuentes (Tecnoempleo, LinkedIn, Adzuna, InfoJobs, Remotive y feeds RSS).
2. Descarta por filtros locales (palabras requeridas/excluidas y ubicación).
3. Gemini puntúa la oferta y devuelve el CV.
4. `CvSanitizer` quita lo que no esté en el perfil.
5. `CvCompilerService` compila el PDF (si no cabe en una página, no lo envía).
6. Se avisa por Telegram.

## Ejecutar

Necesitas el SDK de .NET 10 y la CLI de `typst` en el `PATH`.

```bash
dotnet restore
dotnet build
dotnet run --project CareerCopilot
dotnet test
```

## Comandos de Telegram

- `/start` — ayuda.
- `/run` — lanza un ciclo ya mismo.
- `/scan <término>` — búsqueda puntual.
- `/cv <id>` — regenera el CV de una oferta.
- `/status` y `/stats` — estado y métricas.
- `/addjob`, `/deljob`, `/threshold`, `/filters` — ajustes rápidos.

## Config

Todo va bajo la clave `BotConfig` (mira `appsettings.example.json`). El perfil
profesional se lee de `prompts/profile.md`, que es la referencia para validar
el CV. Las claves (`GeminiApiKey`, `TelegramBotToken`, etc.) mejor en variables
de entorno o `appsettings.Development.json`, no las subas al repo.
