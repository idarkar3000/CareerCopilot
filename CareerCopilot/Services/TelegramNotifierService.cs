using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using CareerCopilot.Models;

namespace CareerCopilot.Services;

public class TelegramNotifierService
{
    private readonly TelegramBotClient _botClient;
    private readonly BotConfig _config;
    private readonly JobDatabase _db;
    private readonly ILogger<TelegramNotifierService> _logger;
    private readonly GeminiScorerService _scorer;
    private readonly CvCompilerService _cvCompiler;
    private readonly Func<string?, Task>? _triggerScanAction;

    private bool _isReceiving = false;
    private readonly object _lock = new();

    // Estado para distinguir un 409 Conflict transitorio (solape de deploy) de uno persistente
    // (otra instancia real corriendo con el mismo token).
    private int _consecutiveConflicts;
    private DateTime? _firstConflictAt;
    private static readonly TimeSpan ConflictEscalationWindow = TimeSpan.FromSeconds(90);

    public TelegramNotifierService(
        BotConfig config,
        JobDatabase db,
        ILogger<TelegramNotifierService> logger,
        GeminiScorerService scorer,
        CvCompilerService cvCompiler,
        Func<string?, Task>? triggerScanAction = null)
    {
        _config = config;
        _db = db;
        _logger = logger;
        _scorer = scorer;
        _cvCompiler = cvCompiler;
        _triggerScanAction = triggerScanAction;
        _botClient = new TelegramBotClient(_config.TelegramBotToken);
    }

    /// <summary>
    /// Antes se llamaba StartReceiving (síncrono). Ahora es async porque primero
    /// verifica/borra un posible webhook residual antes de arrancar el long polling.
    /// Actualiza el punto de llamada (Worker.cs / Program.cs) a:
    ///   await telegramNotifier.StartReceivingAsync(ct);
    /// </summary>
    public async Task StartReceivingAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_isReceiving)
            {
                _logger.LogWarning("StartReceiving invocado, pero el bot ya está recibiendo actualizaciones. Ignorando llamada duplicada.");
                return;
            }
            _isReceiving = true;
        }

        try
        {
            // Defensivo: si quedó un webhook configurado de una prueba anterior (setWebhook),
            // compite con el long polling y provoca conflictos espurios en getUpdates.
            // dropPendingUpdates evita procesar de golpe mensajes acumulados mientras el bot
            // estuvo caído (por ejemplo, durante un redeploy).
            await _botClient.DeleteWebhookAsync(dropPendingUpdates: true, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo verificar/borrar el webhook de Telegram antes de iniciar el polling (no crítico, se continúa).");
        }

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message }
        };

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: ct
        );

        _logger.LogInformation("Escuchador de comandos de Telegram iniciado.");
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } messageText } message) return;
        if (message.Chat.Id != _config.TelegramChatId) return;

        var parts = messageText.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        if (command.Contains('@')) command = command.Split('@')[0];
        var argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (command)
        {
            case "/test":
                await bot.SendTextMessageAsync(message.Chat.Id, "🧪 Probando Gemini API + Typst + PDF...", cancellationToken: ct);

                var mockJob = new JobOffer(
                    "mock_" + Guid.NewGuid().ToString("N")[..6],
                    "Junior .NET Backend Developer",
                    "Empresa de Prueba Tech",
                    "https://es.linkedin.com/jobs/view/4155609388",
                    "Buscamos desarrollador Junior .NET C# con conocimientos en ASP.NET Core, Entity Framework y SQL Server en Madrid.",
                    DateTime.UtcNow
                );

                var eval = await _scorer.EvaluateAsync(mockJob, ct);
                if (eval == null)
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "❌ Error: Gemini devolvió nulo. Revisa la API Key o logs.", cancellationToken: ct);
                    return;
                }

                var pdf = await _cvCompiler.GeneratePdfAsync(mockJob, eval, ct);
                await SendNotificationAsync(mockJob, eval, pdf, ct);
                break;

            case "/run":
                await bot.SendTextMessageAsync(message.Chat.Id, "🚀 Disparando ciclo completo de búsqueda y análisis...", cancellationToken: ct);
                if (_triggerScanAction != null)
                {
                    _ = Task.Run(async () => await _triggerScanAction(null), ct);
                }
                else
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "ℹ️ Acción de rastreo inmediato en segundo plano.", cancellationToken: ct);
                }
                break;

            case "/scan":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "⚠️ Uso: `/scan <término>`\nEjemplo: `/scan wpf developer`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                await bot.SendTextMessageAsync(message.Chat.Id, $"🔎 Escaneando vacantes para: *{EscapeMarkdown(argument)}*...", parseMode: ParseMode.Markdown, cancellationToken: ct);
                if (_triggerScanAction != null)
                {
                    _ = Task.Run(async () => await _triggerScanAction(argument), ct);
                }
                break;

            case "/addjob":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "⚠️ Uso: `/addjob <término>`\nEjemplo: `/addjob c# junior`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                _db.AddSearchQuery(argument);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Añadido a las búsquedas: *{EscapeMarkdown(argument)}*", parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "/removejob":
            case "/deljob":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "⚠️ Uso: `/removejob <término>`\nEjemplo: `/removejob c# junior`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                var deleted = _db.RemoveSearchQuery(argument);
                var reply = deleted
                    ? $"🗑️ Eliminado de las búsquedas: *{EscapeMarkdown(argument)}*"
                    : $"⚠️ No se encontró: *{EscapeMarkdown(argument)}*";
                await bot.SendTextMessageAsync(message.Chat.Id, reply, parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "/listjobs":
            case "/jobs":
                var queries = _db.GetSearchQueries();
                if (queries.Count == 0)
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "📭 No hay términos de búsqueda activos.", cancellationToken: ct);
                    return;
                }
                var listText = "📋 *Términos de búsqueda activos:*\n" + string.Join("\n", queries.Select(q => $"• `{EscapeMarkdown(q)}`"));
                await bot.SendTextMessageAsync(message.Chat.Id, listText, parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "/addrequired":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "⚠️ Uso: `/addrequired <palabra>`\nEjemplo: `/addrequired .net`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                var reqKey = argument.ToLowerInvariant();
                if (!_config.RequiredKeywords.Contains(reqKey))
                {
                    _config.RequiredKeywords.Add(reqKey);
                    await bot.SendTextMessageAsync(message.Chat.Id, $"🔒 Palabra requerida añadida: *{EscapeMarkdown(reqKey)}*", parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                else
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, $"ℹ️ *{EscapeMarkdown(reqKey)}* ya estaba en la lista de requeridas.", parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                break;

            case "/removerequired":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "⚠️ Uso: `/removerequired <palabra>`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                var remReq = argument.ToLowerInvariant();
                var reqRemoved = _config.RequiredKeywords.Remove(remReq);
                await bot.SendTextMessageAsync(message.Chat.Id, reqRemoved
                    ? $"🔓 Palabra requerida retirada: *{EscapeMarkdown(remReq)}*"
                    : $"⚠️ No se encontró: *{EscapeMarkdown(remReq)}*", parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "/addexcluded":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "⚠️ Uso: `/addexcluded <palabra>`\nEjemplo: `/addexcluded senior`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                var excKey = argument.ToLowerInvariant();
                if (!_config.ExcludedKeywords.Contains(excKey))
                {
                    _config.ExcludedKeywords.Add(excKey);
                    await bot.SendTextMessageAsync(message.Chat.Id, $"🚫 Palabra de exclusión añadida: *{EscapeMarkdown(excKey)}*", parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                else
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, $"ℹ️ *{EscapeMarkdown(excKey)}* ya estaba en la lista de exclusiones.", parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                break;

            case "/removeexcluded":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "⚠️ Uso: `/removeexcluded <palabra>`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                var remExc = argument.ToLowerInvariant();
                var excRemoved = _config.ExcludedKeywords.Remove(remExc);
                await bot.SendTextMessageAsync(message.Chat.Id, excRemoved
                    ? $"✅ Palabra de exclusión eliminada: *{EscapeMarkdown(remExc)}*"
                    : $"⚠️ No se encontró: *{EscapeMarkdown(remExc)}*", parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "/filters":
                var reqs = _config.RequiredKeywords.Any() ? string.Join(", ", _config.RequiredKeywords.Select(r => $"`{EscapeMarkdown(r)}`")) : "_Ninguna_";
                var excs = _config.ExcludedKeywords.Any() ? string.Join(", ", _config.ExcludedKeywords.Select(e => $"`{EscapeMarkdown(e)}`")) : "_Ninguna_";
                var filterMsg = $"⚙️ *Filtros de Palabras Clave:*\n\n🔒 *Obligatorias:* {reqs}\n🚫 *Excluidas:* {excs}";
                await bot.SendTextMessageAsync(message.Chat.Id, filterMsg, parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "/threshold":
                if (int.TryParse(argument, out var score) && score >= 0 && score <= 100)
                {
                    _config.MinScoreThreshold = score;
                    await bot.SendTextMessageAsync(message.Chat.Id, $"🎯 Umbral mínimo de afinidad actualizado a: *{score}/100*", parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                else
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, $"⚠️ Introduce un valor entero de 0 a 100.\nUmbral actual: *{_config.MinScoreThreshold}/100*", parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                break;

            case "/status":
                var activeQueries = _db.GetSearchQueries();
                var statusMsg =
                    $"📊 *Estado del Bot:*\n\n" +
                    $"• *Búsquedas activas:* {activeQueries.Count}\n" +
                    $"• *Keywords obligatorias:* {_config.RequiredKeywords.Count}\n" +
                    $"• *Keywords excluidas:* {_config.ExcludedKeywords.Count}\n" +
                    $"• *Umbral Gemini:* {_config.MinScoreThreshold}/100\n" +
                    $"• *Intervalo:* cada {_config.CheckIntervalMinutes} min";
                await bot.SendTextMessageAsync(message.Chat.Id, statusMsg, parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "/help":
            case "/start":
                var helpText = @"🤖 *Panel de Control - CareerCopilot*

*Operativa de Búsqueda:*
• `/run` — Disparar rastreo completo inmediatamente.
• `/scan <término>` — Buscar ofertas puntuales sin añadirlas a la lista recurrente.
• `/test` — Probar pipeline con Gemini y Typst compilando un CV ficticio.

*Gestión de Búsquedas:*
• `/addjob <término>` — Añadir término de búsqueda recurrente.
• `/removejob <término>` — Eliminar término de búsqueda recurrente.
• `/listjobs` — Listar todos los términos de búsqueda activos.

*Filtros y Puntuación:*
• `/addrequired <palabra>` — Añadir palabra técnica obligatoria (.net, c#).
• `/removerequired <palabra>` — Quitar palabra obligatoria.
• `/addexcluded <palabra>` — Añadir palabra a descartar (senior, lead).
• `/removeexcluded <palabra>` — Quitar palabra de descarte.
• `/filters` — Ver las palabras obligatorias y excluidas activas.
• `/threshold <0-100>` — Modificar corte de afinidad para generar CV.

*General:*
• `/status` — Resumen general de métricas y filtros.
• `/help` — Mostrar esta ayuda.";
                await bot.SendTextMessageAsync(message.Chat.Id, helpText, parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            default:
                await bot.SendTextMessageAsync(message.Chat.Id, "❓ Comando no reconocido. Usa `/help` para ver la lista de comandos disponibles.", parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        if (ex is ApiRequestException { ErrorCode: 409 })
        {
            _consecutiveConflicts++;
            _firstConflictAt ??= DateTime.UtcNow;
            var elapsed = DateTime.UtcNow - _firstConflictAt.Value;

            if (elapsed < ConflictEscalationWindow)
            {
                _logger.LogWarning(
                    "Conflicto 409 de Telegram getUpdates (intento {N}, {Elapsed:F0}s transcurridos). " +
                    "Probablemente solape de instancias durante un redeploy; debería resolverse solo.",
                    _consecutiveConflicts, elapsed.TotalSeconds);
            }
            else
            {
                _logger.LogError(
                    "Conflicto 409 de Telegram getUpdates persiste desde hace {Elapsed:F0}s ({N} intentos). " +
                    "Esto normalmente indica que hay OTRA instancia real usando el mismo TELEGRAM_BOT_TOKEN " +
                    "(otro deploy activo en Render, una ejecución en local, o un webhook activo). Revisa procesos duplicados.",
                    elapsed.TotalSeconds, _consecutiveConflicts);
            }

            // Backoff progresivo (máx. 30s) para no martillear la API de Telegram mientras el
            // conflicto se resuelve por sí solo; el propio receptor de Telegram.Bot reintentará
            // después de este delay.
            return Task.Delay(TimeSpan.FromSeconds(Math.Min(5 * _consecutiveConflicts, 30)), ct);
        }

        // Cualquier error que no sea un 409 reinicia el contador de conflictos.
        _consecutiveConflicts = 0;
        _firstConflictAt = null;

        _logger.LogError(ex, "Error en Telegram Polling.");
        return Task.CompletedTask;
    }

    public async Task SendNotificationAsync(JobOffer job, EvaluationResult eval, string? pdfPath, CancellationToken ct)
    {
        var message = $@"🎯 *NUEVA OFERTA COMPATIBLE* ({eval.Score}/100)

🏢 *Empresa:* {EscapeMarkdown(job.Company)}
💼 *Puesto:* {EscapeMarkdown(job.Title)}

✅ *Puntos Fuertes:*
{string.Join("\n", eval.Strengths.Select(s => $"• {EscapeMarkdown(s)}"))}

⚠️ *A revisar:*
{string.Join("\n", eval.Concerns.Select(c => $"• {EscapeMarkdown(c)}"))}
";

        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithUrl("🌐 Abrir Vacante para Postular", job.Link)
        });

        try
        {
            await _botClient.SendTextMessageAsync(
                chatId: _config.TelegramChatId,
                text: message,
                parseMode: ParseMode.Markdown,
                replyMarkup: inlineKeyboard,
                cancellationToken: ct
            );

            if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
            {
                await using var stream = System.IO.File.OpenRead(pdfPath);
                await _botClient.SendDocumentAsync(
                    chatId: _config.TelegramChatId,
                    document: InputFile.FromStream(stream, Path.GetFileName(pdfPath)),
                    caption: "📄 CV adaptado y listo para adjuntar.",
                    cancellationToken: ct
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enviar notificación de Telegram.");
        }
    }

    private static string EscapeMarkdown(string text)
    {
        return text.Replace("_", "\\_")
                   .Replace("*", "\\*")
                   .Replace("[", "\\[").Replace("]", "\\]")
                   .Replace("`", "\\`");
    }
}