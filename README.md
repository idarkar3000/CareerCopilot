# CareerCopilot 🚀📄

CareerCopilot es una solución backend automatizada desarrollada en **C# y .NET 10** orientada a la prospección inteligente de empleo. Analiza ofertas frente a un perfil técnico verificado, genera un **CV completo adaptado a cada oferta en JSON** con **Gemini** y lo compila dinámicamente a un **PDF de una sola página A4** con **Typst**. Todo se notifica en tiempo real mediante un bot de **Telegram**.

## 🏗️ Arquitectura

```text
Fuentes (Adzuna / InfoJobs / Feeds RSS / LinkedIn / Tecnoempleo)
       │
       ▼
Scraping + Normalización
       │
       ▼
SQLite (deduplicación y persistencia)
       │
       ▼
GeminiScorerService (evaluación técnica + CvDocument JSON)
       │
       ▼
CvSanitizer (guardarraíl anti-alucinaciones contra profile.md)
       │
       ▼
CvCompilerService (Typst, escalonado por prioridad y tipografía hasta 1 página A4)
       │
       ▼
TelegramNotifierService (alertas en tiempo real + CV en PDF adjunto)
```

### Características clave:
- **CV dinámico por oferta:** Gemini devuelve un `CvDocument` completo (`headline`, `summary` y secciones estructuradas de tipo `entries`/`texts`). No existe una plantilla fija de texto, únicamente maquetación adaptativa.
- **Ajuste estricto a 1 página A4:** `CvCompilerService` aplica un escalón de recorte por `priority` (1 = imprescindible → 3 = secundario) y ajusta de forma adaptativa el tamaño de fuente. Si el documento excede una página, devuelve `null` y no emite un PDF defectuoso.
- **Guardarraíl anti-alucinaciones:** `CvSanitizer` valida el contenido generado contra el perfil único (`prompts/profile.md`), descartando cualquier token o tecnología no respaldada y notificándolo en el mensaje de Telegram.
- **Semáforo de pipeline:** `Worker` gestiona la concurrencia (`_pipelineLock`) para evitar ejecuciones simultáneas, duplicados o sobrecargas de cuota en Gemini y Typst.
- **Filtro geográfico configurable:** Acepta ofertas cuya ubicación coincida con `LocationFilter.AcceptedLocations` o que incluyan términos de teletrabajo en `RemoteKeywords`; descarta ofertas fuera de Madrid salvo que sean explícitamente remotas.

---

## 🛠️ Stack Tecnológico

- **Runtime & Framework:** C# 10 / .NET 10 / ASP.NET Core (`IHostedService` / `BackgroundService`)
- **Inteligencia Artificial:** Google Gemini REST API (fallback entre modelos y reintentos ante códigos `429`/`503` respetando cabeceras `Retry-After`)
- **Generación Documental:** Typst CLI + PdfPig (validación estricta del número de páginas)
- **Persistencia:** SQLite (`Microsoft.Data.Sqlite`)
- **Integraciones:** Telegram.Bot SDK
- **Consumo Web:** `HttpClient`, `System.ServiceModel.Syndication`

---

## ⚙️ Configuración (`appsettings.json`)

Toda la configuración se centraliza bajo la clave `BotConfig`. Utiliza `appsettings.example.json` como plantilla base. El perfil profesional no reside en el JSON, sino que se carga directamente desde disco.

```json
{
  "BotConfig": {
    "GeminiApiKey": "TU_GEMINI_API_KEY",
    "TelegramBotToken": "TU_TELEGRAM_BOT_TOKEN",
    "TelegramChatId": 0,
    "MinScoreThreshold": 60,
    "CheckIntervalMinutes": 60,
    "CandidateProfileFile": "prompts/profile.md",
    "Candidate": {
      "FullName": "Adrián Espínola Gumiel",
      "Headline": "Desarrollador Backend .NET / C#",
      "Email": "correo@ejemplo.com",
      "Phone": "+34 000 00 00 00",
      "City": "Madrid",
      "Availability": "Inmediata",
      "PdfFileNameTemplate": "CV_{job}_{name}",
      "OutputDir": "GeneratedCVs",
      "Links": [
        { "Label": "[github.com/tu-usuario](https://github.com/tu-usuario)", "Url": "[https://github.com/tu-usuario](https://github.com/tu-usuario)" },
        { "Label": "[linkedin.com/in/tu-perfil](https://linkedin.com/in/tu-perfil)", "Url": "[https://linkedin.com/in/tu-perfil](https://linkedin.com/in/tu-perfil)" }
      ]
    },
    "LocationFilter": {
      "AcceptedLocations": [ "madrid" ],
      "RemoteKeywords": [ "remoto", "teletrabajo", "remote", "full remote", "100% remoto" ]
    },
    "RequiredKeywords": [ "c#", ".net", "dotnet", "asp.net" ],
    "ExcludedKeywords": [ "senior", "sr.", "sr ", "lead", "principal", "staff", "architect", "tech lead", "manager", "head of", "director" ],
    "SearchQueries": [ "c# junior", ".net junior", "programador c# junior", "desarrollador .net junior" ],
    "Feeds": []
  }
}
```

> **Notas de seguridad:**
> - `CandidateProfileFile` apunta al archivo `prompts/profile.md`. Es el **único origen de verdad** para validar competencias técnicas; `CvSanitizer` eliminará cualquier tecnología no declarada en este fichero.
> - `Candidate.PdfFileNameTemplate` acepta los tokens `{job}` (slug del puesto) y `{name}` (slug del nombre normalizado sin espacios).
> - Nunca subas `appsettings.json` al repositorio si contiene claves reales (`GeminiApiKey`, `TelegramBotToken`). Emplea variables de entorno o Secret Manager en entornos productivos.

---

## 🧠 Perfil del Candidato

El perfil profesional completo reside en [`prompts/profile.md`](CareerCopilot/prompts/profile.md). `CandidateProfileProvider` lo procesa al arrancar y genera los tokens de referencia para el guardarraíl. El prompt enviado a Gemini incluye este perfil íntegro junto con directrices estrictas de evaluación.

### Anclas del CV (bloque `<!-- cv-anchors -->`)
Al final de `profile.md` se define un bloque JSON estructurado que `CvCompilerService` inyecta sobre el documento devuelto por Gemini antes de procesar el PDF:

- **Estructura base:** Fija datos corporativos inmutables (`org`, `dates`, `stack` y `url`) en `experience` y `projects` cuando el modelo no los suministra completos.
- **Viñetas de reserva (`fallback`):** Entradas por defecto si el modelo devuelve listas vacías.
- **Banco de competencias (`points`):** Conjunto cerrado de logros y tareas verificadas del candidato de donde se seleccionan las viñetas.
- **Aislamiento:** Cualquier proyecto o experiencia ajena al perfil queda terminantemente excluida.

### Selección de viñetas adaptadas
La experiencia profesional previa es la sección prioritaria del CV (5 viñetas asignadas por defecto), seguida por los proyectos destacados (3 viñetas):

1. **Selección semántica con IA:** Gemini escoge del array `points` los hitos que mayor afinidad guarden con los requisitos de la vacante.
2. **Fallback determinista:** Si el modelo no suministra viñetas suficientes, `CvCompilerService` puntúa los puntos disponibles según la frecuencia de términos compartidos con la oferta y completa el cupo sin consumir cuota adicional.

Al ajustar el documento al límite estricto de una página A4, la regla de recorte prioriza siempre la experiencia laboral frente a los proyectos personales.

### Generación local sin conexión a Gemini (`/cvlocal`)
El bloque de anclas incluye un objeto base completo (`summary`, `skills`, `education`, `languages`). Con ello, `LocalCvBuilder` puede compilar un CV válido sin invocar la API de Gemini:

- `/cv <id> local` o `/cvlocal <id>`: Fuerza el renderizado local de una oferta registrada en base de datos.
- `/cvlocal <título> || <descripción>`: Genera el CV a partir de texto arbitrario pegado directamente en Telegram.
- Fallback automático: Si la llamada a Gemini agota cuota o falla, el bot recurre a este modo local y lo especifica en el aviso.

---

## 📖 Comandos de Telegram

| Comando | Descripción |
|---|---|
| `/start` / `/help` | Muestra el panel de ayuda y los comandos disponibles. |
| `/status` | Estado operativo del worker, recuento de ofertas procesadas y pendientes. |
| `/run` | Dispara un ciclo completo inmediato (scraping + evaluación). |
| `/scan <término>` | Rastreo puntual para un término de búsqueda sin guardarlo en el histórico. |
| `/cv <id>` | Regenera y envía el CV maquetado para una oferta registrada por su ID. |
| `/cvlocal <id>` | Genera el CV en modo determinista local sin consumir API de IA. |
| `/unmark <id>` | Desmarca una oferta para permitir su reevaluación. |
| `/stats` | Métricas generales de base de datos (ofertas, puntuación media, descartes). |
| `/addjob <término>` | Añade un término de búsqueda recurrente al worker. |
| `/deljob <término>` | Elimina un término de búsqueda recurrente. |
| `/addreq <keyword>` | Añade una palabra clave técnica obligatoria. |
| `/remreq <keyword>` | Elimina una palabra clave obligatoria. |
| `/addexc <keyword>` | Añade una palabra clave de exclusión. |
| `/remexc <keyword>` | Elimina una palabra clave de exclusión. |
| `/filters` | Consulta la lista activa de palabras obligatorias y excluidas. |
| `/threshold <0-100>` | Modifica el umbral mínimo de puntuación para generar CV y avisar. |

---

## 🧪 Pruebas de Humo (Typst + Guardarraíl)

Para validar que la plantilla Typst compila exactamente en **1 página A4** y verificar la ausencia de falsos positivos en el sanitizador, se puede ejecutar el proyecto de test:

```powershell
dotnet run --project "CvSmoke/CvSmoke.csproj"
```

El validador inspecciona el PDF resultante con `PdfPig` y confirma que el recuento sea exactamente igual a `1`.

---

## 🚀 Despliegue y Ejecución

### Requisitos previos
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Typst CLI](https://github.com/typst/typst/releases) instalado y accesible en la variable de entorno `PATH` (`typst --version`).

### Ejecución Local
```powershell
# Acceder al directorio del proyecto
cd CareerCopilot

# Restaurar dependencias y compilar
dotnet build

# Ejecutar el servicio
dotnet run
```

### Contenedores (Docker)
El servicio está preparado para empaquetarse en arquitecturas Linux basadas en imágenes base de ASP.NET Core junto con la descarga del binario estático de `typst`.

---

## 📌 Principios de Diseño

- **Contrato JSON estricto:** Gemini responde bajo un esquema estructurado validado (`responseSchema`), evitando parseos frágiles de texto libre.
- **Tolerancia cero a las alucinaciones:** Ningún dato no presente explícitamente en el perfil del candidato llega al documento final.
- **Diseño a 1 sola página:** Un CV de longitud superior a una cara es descartado por el compilador para mantener un estándar profesional.
- **Resiliencia de red:** Backoff exponencial y rotación ordenada entre modelos de Gemini ante errores de cuota o saturación (`429`, `503`).

---

## 📄 Licencia

Este proyecto está bajo la Licencia [MIT](LICENSE). Siéntete libre de utilizarlo, modificarlo y adaptarlo a tu propio flujo de búsqueda técnica.
