﻿﻿﻿# CareerCopilot ðŸš€ðŸ“„

CareerCopilot es una soluciÃ³n backend automatizada desarrollada en **C# y .NET 10** orientada a la prospecciÃ³n inteligente de empleo. Analiza ofertas frente a un perfil tÃ©cnico verificado, genera un **CV completo adaptado a cada oferta en JSON** con **Gemini** y lo compila dinÃ¡micamente a **PDF de una sola pÃ¡gina A4** con **Typst**. Todo se notifica en tiempo real mediante un bot de **Telegram**.

## ðŸ—ï¸ Arquitectura

```text
Fuentes (Adzuna / InfoJobs / Feeds RSS) â†’ Scraping + NormalizaciÃ³n â†’ SQLite (deduplicaciÃ³n)
â†’ GeminiScorerService (evaluaciÃ³n + CvDocument JSON) â†’ CvSanitizer (guardarraÃ­l anti-alucinaciones)
â†’ CvCompilerService (Typst, ajuste por prioridad y tipografÃ­a hasta 1 pÃ¡gina) â†’ TelegramNotifierService (alertas + PDF)
```

CaracterÃ­sticas clave:
- **CV dinÃ¡mico por oferta.** Gemini devuelve `CvDocument` completo (headline, summary y secciones tipo `entries`/`texts`). No hay plantilla fija de contenido, solo maquetaciÃ³n.
- **Ajuste obligatorio a 1 pÃ¡gina A4.** `CvCompilerService` aplica un escalÃ³n de recorte por `priority` (1=imprescindible â†’ 3=secundario) y desciende el tamaÃ±o de letra hasta conseguir 1 pÃ¡gina. Si no cabe, devuelve `null` y no envÃ­a PDF.
- **GuardarraÃ­l anti-alucinaciones.** `CvSanitizer` valida contra el perfil Ãºnico (`prompts/profile.md`); elimina tokens tÃ©cnicos no respaldados y los reporta en el mensaje de Telegram.
- **SemÃ¡foro de pipeline.** `Worker` protege contra ejecuciones concurrentes (`_pipelineLock`) para evitar duplicados o sobrecargas en Gemini/Typst.
- **Filtro de ubicaciÃ³n configurable.** Acepta ofertas con ubicaciÃ³n incluida en `LocationFilter.AcceptedLocations` o con palabras remotas (`RemoteKeywords`); por defecto rechaza fuera de Madrid salvo remoto vÃ¡lido.

## ðŸ› ï¸ Stack

- **Runtime:** C# 10 / .NET 10 / ASP.NET Core (Hosted Service)
- **IA:** Google Gemini REST API con fallback entre modelos y reintentos acotados ante `429`/`503` (respeta `Retry-After`)
- **DocumentaciÃ³n:** Typst CLI (`typst --version`) + PdfPig (validaciÃ³n de pÃ¡ginas)
- **Persistencia:** SQLite (Microsoft.Data.Sqlite)
- **Bot:** Telegram.Bot v19
- **Otros:** System.ServiceModel.Syndication, Microsoft.AspNetCore.OpenApi

## âš™ï¸ ConfiguraciÃ³n (`appsettings.json`)

La configuraciÃ³n se organiza en `BotConfig`. Usa `appsettings.example.json` como plantilla. El perfil profesional **no** va dentro del JSON (se carga desde disco).

```json
{
  "BotConfig": {
    "GeminiApiKey": "TU_GEMINI_API_KEY",
    "TelegramBotToken": "TU_TELEGRAM_BOT_TOKEN",
    "TelegramChatId": TU_CHAT_ID,
    "MinScoreThreshold": 60,
    "CheckIntervalMinutes": 60,
    "CandidateProfileFile": "prompts/profile.md",
    "Candidate": {
      "FullName": "Nombre Apellidos",
      "Headline": "Desarrollador Backend .NET / C#",
      "Email": "correo@ejemplo.com",
      "Phone": "+00 000 00 00 00",
      "City": "Ciudad",
      "Availability": "Disponible inmediatamente",
      "PdfFileNameTemplate": "CV_{job}_{name}",
      "OutputDir": "GeneratedCVs",
      "Links": [
        { "Label": "github.com/tu-usuario", "Url": "https://github.com/tu-usuario" },
        { "Label": "linkedin.com/in/tu-perfil", "Url": "https://linkedin.com/in/tu-perfil" }
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

**Notas importantes:**
- `CandidateProfileFile` apunta a `prompts/profile.md` (copiado a salida). Este es el **Ãºnico origen de verdad** para validar tecnologÃ­as; `CvSanitizer` rechaza cualquier token no respaldado.
- `Candidate.PdfFileNameTemplate` admite tokens `{job}` (slug de puesto) y `{name}` (slug de nombre). El nombre de archivo nunca contiene espacios.
- Las credenciales (`GeminiApiKey`, `TelegramBotToken`) son sensibles. No versionar `appsettings.json`. `appsettings.example.json` es la plantilla documentada.

## ðŸ§  Perfil del candidato

El perfil completo se encuentra en [`prompts/profile.md`](CareerCopilot/prompts/profile.md). `CandidateProfileProvider` lo carga al arranque (fallback a `BotConfig.CandidateProfile` si falta) y lo tokeniza para alimentar el guardarraÃ­l. El prompt de Gemini recibe este perfil Ã­ntegro y la instrucciÃ³n explÃ­cita de **no inventar tecnologÃ­as, proyectos ni certificaciones no presentes**.

### Anclas del CV (empresa, stack, enlaces y descripción)

Al final de `profile.md` hay un bloque `<!-- cv-anchors -->` con JSON. `CvCompilerService` lo inyecta sobre el documento que devuelve Gemini antes de maquetar:

- `experience` y `projects` fijan `org`, `dates`, `stack` y `url` cuando el modelo no los aporta.
- `fallback` se usa como viñeta de reserva cuando el modelo devuelve el bloque sin `bullets`.
- `points` es el fondo de puntos de cada entrada, uno por competencia. De ahí se eligen las viñetas.
- Los proyectos que **no** estén en el perfil se descartan, y los del perfil aparecen siempre y en ese orden, sin importar qué modelo responda.

Motivo: con el tier gratuito el único modelo con cuota puede devolver las secciones `entries` con el campo `title` como único contenido. Con las anclas, empresa, fechas, stack, enlace y descripción ya no dependen del modelo.

### Viñetas elegidas por oferta

La experiencia es la sección más importante del CV, así que va siempre la primera, con 5 viñetas, y los proyectos con 3 (BotConfig.ExperienceBullets y BotConfig.ProjectBullets).

Las viñetas se eligen en dos capas:

1. **Gemini** elige del array `points` del perfil los que mejor encajen con la oferta.
2. Si el modelo no devuelve `bullets` o no llega al número pedido, `CvCompilerService` los completa puntuando por las palabras que comparte con el título y la descripción de la oferta, y se queda con los mejores.

La segunda capa no consume cuota y es determinista, así que los cinco puntos de EPAM **no son los mismos en todos los CV**: para una oferta de DevOps sale primero Azure DevOps y CI/CD, y para una de SQL sale Dapper y la optimización de Informix.

El recorte a una página también respeta esa prioridad: cuando hay que apretar, se recortan primero las viñetas de los proyectos y la experiencia es lo último que se toca.

### CV local sin Gemini (/cv <id> local)

El bloque de anclas incluye además un objeto profile con summary, skills, education y languages, todo copiado del perfil. Con eso LocalCvBuilder monta un CV completo sin llamar a Gemini, útil cuando la cuota diaria está agotada:

- /cv <id> local o /cvlocal <id>: fuerza el modo local de una oferta ya registrada.
- /cvlocal <título> || <descripción>: genera el CV de una oferta pegada, para cuando no hay cuota y nunca llegó a registrarse.
- /cv <id> sin local: si Gemini no responde, cae a modo local automáticamente y avisa.

La experiencia y los proyectos se dejan vacíos a propósito en el documento local: los rellena ApplyAnchors igual que cuando el documento viene del modelo, así los dos caminos comparten la lógica que elige las viñetas.

El CV local **no se envía solo** a Telegram: sin evaluación del modelo no hay base para decidir a quién escribir. En su lugar el mensaje enseña qué palabras clave de RequiredKeywords aparecen en la oferta y cuáles no, y avisa de que el texto no está adaptado.

Si el bloque falta o su JSON es inválido, se registra un aviso y el CV se genera tal cual lo devuelva el modelo, sin fallar.

## 📖 Comandos de Telegram


| Comando | DescripciÃ³n |
|---|---|
| `/start` / `/help` | Muestra ayuda y comandos disponibles. |
| `/status` | Estado del servicio, ofertas procesadas, pendientes y CVs pendientes de regenerar. |
| `/run` | Fuerza un ciclo inmediato (scraping + evaluaciÃ³n). Respeta el semÃ¡foro de pipeline. |
| `/cv <id>` | Regenera y envÃ­a el CV para una oferta procesada (por ID interno). Usa HTML y envÃ­a mensaje + PDF por separado para mÃ¡xima robustez. |
| `/unmark <id>` | Marca una oferta procesada como no procesada para que vuelva a evaluarse. |
| `/stats` | EstadÃ­sticas de base de datos (totales, procesadas, score medio). |
| `/addreq <keyword>` | AÃ±ade palabra clave obligatoria (case-insensitive). |
| `/remreq <keyword>` | Elimina palabra clave obligatoria. |
| `/addexc <keyword>` | AÃ±ade palabra clave excluyente. |
| `/remexc <keyword>` | Elimina palabra clave excluyente. |
| `/listfilters` | Lista filtros activos (required/excluded). |

## ðŸ§ª Pruebas de humo (Typst + guardarraÃ­l)

Para validar que el compilador genera **exactamente 1 pÃ¡gina A4** y que el guardarraÃ­l funciona sin false positives, hay un proyecto de humo en `C:\Users\espin\AppData\Local\Temp\opencode\CvSmoke\`. EjecuciÃ³n:

```powershell
dotnet run --project "C:\Users\espin\AppData\Local\Temp\opencode\CvSmoke\CvSmoke.csproj" --no-build
```

Salida esperada: `1 pÃ¡gina(s)`. PdfPig confirma el recuento de pÃ¡ginas. Las violaciones detectadas deben corresponder Ãºnicamente a tecnologÃ­as ausentes del perfil (ej. `terraform`, `kafka`...).

## ðŸš€ EjecuciÃ³n

### Requisitos previos
- .NET 10 SDK
- [Typst CLI](https://github.com/typst/typst/releases) en `PATH` (`typst --version`)

### Local
```powershell
cd "C:\Users\espin\Desktop\Programacion\CareerCopilot\CareerCopilot"
cp appsettings.example.json appsettings.json  # editar credenciales
dotnet build CareerCopilot.slnx
dotnet run --project CareerCopilot
```

### Docker
El proyecto mantiene compatibilidad con Docker multi-stage (binario independiente + Typst). Consultar `Dockerfile` para detalles.

## ðŸ“Œ Notas de diseÃ±o

- **Un Ãºnico prompt, una Ãºnica respuesta.** Gemini devuelve `evaluation` + `cv` completo en JSON estricto (response schema). No se hacen llamadas por partes.
- **Fuente Ãºnica de verdad.** `prompts/profile.md` es la base para generar y para validar. Cualquier ajuste al perfil debe hacerse ahÃ­.
- **No hay alucinaciones toleradas.** Si una tecnologÃ­a no aparece en el perfil, el `CvSanitizer` la elimina. El CV jamÃ¡s inventa certificaciones.
- **Estrictamente 1 pÃ¡gina.** La decisiÃ³n de no enviar PDF cuando no cabe en A4 es deliberada (calidad sobre cantidad).
- **Sin commits automÃ¡ticos.** Nunca se hace `git commit` sin solicitud explÃ­cita del usuario.
- **RotaciÃ³n de credenciales.** Durante el rediseÃ±o aparecieron claves en texto plano en la conversaciÃ³n: se recomienda **rotar** `GeminiApiKey` y `TelegramBotToken` al terminar el uso de desarrollo.

## Licencia

MIT License. Ver [LICENSE](LICENSE) si existe.

