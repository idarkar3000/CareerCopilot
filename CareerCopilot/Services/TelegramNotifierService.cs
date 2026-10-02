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
    private readonly Func<IManualActions> _manualActions;

    private bool _isReceiving;
    private readonly object _lock = new();

    private int _consecutiveConflicts;
    private DateTime? _firstConflictAt;
    private static readonly TimeSpan ConflictEscalationWindow = TimeSpan.FromSeconds(90);

    /// <summary>Tiempo máximo que se deja correr un escaneo lanzado a mano desde el chat.</summary>
    private static readonly TimeSpan ManualScanTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Separa el título de la descripción en "/cvlocal Título || Descripción".</summary>
    private const string TitleDescriptionSeparator = "||";

    public TelegramNotifierService(
        BotConfig config,
        JobDatabase db,
        ILogger<TelegramNotifierService> logger,
        GeminiScorerService scorer,
        CvCompilerService cvCompiler,
        Func<IManualActions> manualActions)
    {
        _config = config;
        _db = db;
        _logger = logger;
        _scorer = scorer;
        _cvCompiler = cvCompiler;
        _manualActions = manualActions;
        _botClient = new TelegramBotClient(_config.TelegramBotToken);
    }

    /// <summary>Empieza el long polling. Borra antes el webhook si quedó puesto.</summary>
    public async Task StartReceivingAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_isReceiving)
            {
                _logger.LogWarning("StartReceivingAsync invocado, pero el bot ya recibe actualizaciones. Se ignora la llamada duplicada.");
                return;
            }
            _isReceiving = true;
        }

        try
        {
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

        var chatId = message.Chat.Id;
        var parts = messageText.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        if (command.Contains('@')) command = command.Split('@')[0];
        var argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (command)
        {
            case "/test":
                await HandleTestAsync(bot, chatId, ct);
                break;

            case "/run":
                await HandleRunAsync(bot, chatId, ct);
                break;

            case "/scan":
                await HandleScanAsync(bot, chatId, argument, ct);
                break;

            case "/cv":
                await HandleCvAsync(bot, chatId, argument, false, ct);
                break;

            case "/cvlocal":
                await HandleCvAsync(bot, chatId, argument, true, ct);
                break;

            case "/unmark":
                await HandleUnmarkAsync(bot, chatId, argument, ct);
                break;

            case "/stats":
                await HandleStatsAsync(bot, chatId);
                break;

            case "/addjob":
                if (await RequireArgument(bot, chatId, argument, "/addjob &lt;término&gt;", "c# junior", ct)) return;
                _db.AddSearchQuery(argument);
                await SendHtmlAsync(bot, chatId, $"✅ Añadido a las búsquedas: <code>{Html(argument)}</code>", ct);
                break;

            case "/removejob":
            case "/deljob":
                if (await RequireArgument(bot, chatId, argument, "/removejob &lt;término&gt;", "c# junior", ct)) return;
                var deleted = _db.RemoveSearchQuery(argument);
                await SendHtmlAsync(bot, chatId, deleted
                    ? $"🗑️ Eliminado de las búsquedas: <code>{Html(argument)}</code>"
                    : $"⚠️ No se encontró: <code>{Html(argument)}</code>", ct);
                break;

            case "/listjobs":
            case "/jobs":
                var queries = _db.GetSearchQueries();
                if (queries.Count == 0)
                {
                    await SendAsync(bot, chatId, "📭 No hay términos de búsqueda activos.", ct);
                    return;
                }
                var listText = "📋 <b>Términos de búsqueda activos:</b>\n" +
                               string.Join("\n", queries.Select(q => $"• <code>{Html(q)}</code>"));
                await SendHtmlAsync(bot, chatId, listText, ct);
                break;

            case "/addrequired":
                if (await RequireArgument(bot, chatId, argument, "/addrequired &lt;palabra&gt;", ".net", ct)) return;
                var reqKey = argument.ToLowerInvariant();
                var alreadyRequired = _config.RequiredKeywords.Contains(reqKey);
                if (!alreadyRequired) _config.RequiredKeywords.Add(reqKey);
                await SendHtmlAsync(bot, chatId, !alreadyRequired
                    ? $"🔒 Palabra requerida añadida: <code>{Html(reqKey)}</code>"
                    : $"ℹ️ <code>{Html(reqKey)}</code> ya estaba en la lista de requeridas.", ct);
                break;

            case "/removerequired":
                if (await RequireArgument(bot, chatId, argument, "/removerequired &lt;palabra&gt;", null, ct)) return;
                var remReq = argument.ToLowerInvariant();
                var reqRemoved = _config.RequiredKeywords.Remove(remReq);
                await SendHtmlAsync(bot, chatId, reqRemoved
                    ? $"🔓 Palabra requerida retirada: <code>{Html(remReq)}</code>"
                    : $"⚠️ No se encontró: <code>{Html(remReq)}</code>", ct);
                break;

            case "/addexcluded":
                if (await RequireArgument(bot, chatId, argument, "/addexcluded &lt;palabra&gt;", "senior", ct)) return;
                var excKey = argument.ToLowerInvariant();
                var alreadyExcluded = _config.ExcludedKeywords.Contains(excKey);
                if (!alreadyExcluded) _config.ExcludedKeywords.Add(excKey);
                await SendHtmlAsync(bot, chatId, !alreadyExcluded
                    ? $"🚫 Palabra de exclusión añadida: <code>{Html(excKey)}</code>"
                    : $"ℹ️ <code>{Html(excKey)}</code> ya estaba en la lista de exclusiones.", ct);
                break;

            case "/removeexcluded":
                if (await RequireArgument(bot, chatId, argument, "/removeexcluded &lt;palabra&gt;", null, ct)) return;
                var remExc = argument.ToLowerInvariant();
                var excRemoved = _config.ExcludedKeywords.Remove(remExc);
                await SendHtmlAsync(bot, chatId, excRemoved
                    ? $"✅ Palabra de exclusión eliminada: <code>{Html(remExc)}</code>"
                    : $"⚠️ No se encontró: <code>{Html(remExc)}</code>", ct);
                break;

            case "/filters":
                var reqs = _config.RequiredKeywords.Count > 0
                    ? string.Join(", ", _config.RequiredKeywords.Select(r => $"<code>{Html(r)}</code>"))
                    : "<i>Ninguna</i>";
                var excs = _config.ExcludedKeywords.Count > 0
                    ? string.Join(", ", _config.ExcludedKeywords.Select(e => $"<code>{Html(e)}</code>"))
                    : "<i>Ninguna</i>";
                var accepted = _config.LocationFilter.AcceptedLocations;
                var locations = accepted.Count > 0
                    ? string.Join(", ", accepted.Select(l => $"<code>{Html(l)}</code>"))
                    : "<i>cualquiera</i>";
                await SendHtmlAsync(bot, chatId,
                    "⚙️ <b>Filtros activos</b>\n\n" +
                    $"🔒 <b>Obligatorias:</b> {reqs}\n" +
                    $"🚫 <b>Excluidas:</b> {excs}\n" +
                    $"📍 <b>Ubicaciones:</b> {locations} (o remoto)", ct);
                break;

            case "/threshold":
                if (int.TryParse(argument, out var score) && score is >= 0 and <= 100)
                {
                    _config.MinScoreThreshold = score;
                    await SendHtmlAsync(bot, chatId, $"🎯 Umbral mínimo de afinidad actualizado a: <b>{score}/100</b>", ct);
                }
                else
                {
                    await SendHtmlAsync(bot, chatId,
                        $"⚠️ Introduce un valor entero de 0 a 100.\nUmbral actual: <b>{_config.MinScoreThreshold}/100</b>", ct);
                }
                break;

            case "/status":
                var activeQueries = _db.GetSearchQueries();
                var manual = ResolveManual();
                await SendHtmlAsync(bot, chatId,
                    "📊 <b>Estado del bot</b>\n\n" +
                    $"• <b>Búsquedas activas:</b> {activeQueries.Count}\n" +
                    $"• <b>Keywords obligatorias:</b> {_config.RequiredKeywords.Count}\n" +
                    $"• <b>Keywords excluidas:</b> {_config.ExcludedKeywords.Count}\n" +
                    $"• <b>Umbral Gemini:</b> {_config.MinScoreThreshold}/100\n" +
                    $"• <b>Intervalo:</b> cada {_config.CheckIntervalMinutes} min\n" +
                    $"• <b>Escaneo en curso:</b> {(manual.IsBusy ? Html(manual.BusyOrigin) : "ninguno")}", ct);
                break;

            case "/help":
            case "/start":
                await SendHtmlAsync(bot, chatId, BuildHelpText(), ct);
                break;

            default:
                await SendHtmlAsync(bot, chatId,
                    "❓ Comando no reconocido. Usa <code>/help</code> para ver la lista de comandos.", ct);
                break;
        }
    }

    private IManualActions ResolveManual()
    {
        try
        {
            return _manualActions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo resolver el pipeline manual.");
            return NullManualActions.Instance;
        }
    }

    private async Task HandleTestAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await SendAsync(bot, chatId, "🧪 Iniciando prueba: Gemini API + Typst + PDF...", ct);

        var mockJob = new JobOffer(
            "mock_" + Guid.NewGuid().ToString("N")[..6],
            "Junior .NET Backend Developer",
            "Empresa de Prueba Tech",
            "https://es.linkedin.com/jobs/view/4155609388",
            "Buscamos desarrollador Junior .NET C# con conocimientos en ASP.NET Core, Entity Framework y SQL Server en Madrid.",
            DateTime.UtcNow,
            "Madrid",
            "Madrid",
            false);

        var eval = await _scorer.EvaluateAsync(mockJob, ct);
        if (eval == null)
        {
            await SendAsync(bot, chatId, "❌ Error en el test: Gemini devolvió nulo. Revisa la API Key, la cuota o los logs.", ct);
            return;
        }

        var pdf = await _cvCompiler.GeneratePdfAsync(mockJob, eval, ct);
        if (string.IsNullOrEmpty(pdf))
        {
            await SendHtmlAsync(bot, chatId,
                "⚠️ Gemini evaluó correctamente, pero el CV no compiló a una sola página. Revisa que <b>typst</b> esté en el PATH.",
                ct);
            return;
        }

        await SendNotificationAsync(mockJob, eval, pdf, ct);

        var sanitizerNote = eval.SanitizerViolations.Count > 0
            ? $"\n⚠️ Guardarraíl: {eval.SanitizerViolations.Count} token(es) sin respaldo en tu perfil fueron descartados."
            : "\n🛡️ Guardarraíl: ningún token sin respaldo en el CV.";

        await SendHtmlAsync(bot, chatId,
            $"✅ <b>Test completado</b>: Gemini ({eval.Score}/100) + Typst + Telegram funcionan.{sanitizerNote}", ct);
    }

    private async Task HandleRunAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        var manual = ResolveManual();
        if (manual.IsBusy)
        {
            await SendHtmlAsync(bot, chatId,
                $"⏳ Ya hay un escaneo en marcha (<b>{Html(manual.BusyOrigin)}</b>). Espera a que termine antes de lanzar otro.", ct);
            return;
        }

        await SendAsync(bot, chatId, "🚀 Disparando ciclo completo de búsqueda y análisis en segundo plano...", ct);
        _ = RunInBackgroundAsync(chatId, query: null);
    }

    private async Task HandleScanAsync(ITelegramBotClient bot, long chatId, string argument, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            await RequireArgument(bot, chatId, argument, "/scan &lt;término&gt;", "wpf developer", ct);
            return;
        }

        var manual = ResolveManual();
        if (manual.IsBusy)
        {
            await SendHtmlAsync(bot, chatId,
                $"⏳ Ya hay un escaneo en marcha (<b>{Html(manual.BusyOrigin)}</b>). Espera a que termine antes de lanzar otro.", ct);
            return;
        }

        await SendHtmlAsync(bot, chatId, $"🔎 Escaneando vacantes para: <b>{Html(argument)}</b>...", ct);
        _ = RunInBackgroundAsync(chatId, argument);
    }

    /// <summary>Dispara el escaneo en segundo plano y avisa cuando acaba.</summary>
    private async Task RunInBackgroundAsync(long chatId, string? query)
    {
        var manual = ResolveManual();
        using var timeoutCts = new CancellationTokenSource(ManualScanTimeout);

        try
        {
            var ok = string.IsNullOrWhiteSpace(query)
                ? await manual.RunPipelineAsync(timeoutCts.Token)
                : await manual.RunAdHocScanAsync(query, timeoutCts.Token);

            if (!ok)
            {
                await SendHtmlAsync(_botClient, chatId,
                    $"⚠️ El escaneo no se ejecutó: ya había otro en marcha (<b>{Html(manual.BusyOrigin)}</b>).");
                return;
            }

            var what = string.IsNullOrWhiteSpace(query) ? "ciclo completo" : $"escaneo de <b>{Html(query)}</b>";
            var stats = _db.GetStats(_config.MinScoreThreshold);
            await SendHtmlAsync(_botClient, chatId,
                $"✅ Fin del {what}.\nOfertas registradas: <b>{stats.Total}</b> · por encima del umbral: <b>{stats.Matches}</b> · media: <b>{stats.AverageScore}</b>/100.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("El escaneo disparado manualmente (/run o /scan) se canceló por timeout o apagado de la app.");
            await SendHtmlAsync(_botClient, chatId, "⏱️ El escaneo manual se canceló por timeout (15 min). Revisa los logs.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando el escaneo disparado manualmente (/run o /scan).");
            await SendHtmlAsync(_botClient, chatId, "❌ Error ejecutando el escaneo. Revisa los logs.");
        }
    }

    /// <summary>Regenera el CV de una oferta. forceLocal equivale a escribir "local" detrás del argumento.</summary>
    private async Task HandleCvAsync(ITelegramBotClient bot, long chatId, string argument, bool forceLocal, CancellationToken ct)
    {
        if (await RequireArgument(bot, chatId, argument, "/cv &lt;id | parte del título | empresa | url&gt; [local]", "li_4155609388", ct)) return;

        // "local" al final fuerza el CV sin Gemini, para cuando no queda cuota
        var localOnly = forceLocal;
        var query = argument;
        if (!localOnly && query.TrimEnd().EndsWith(" local", StringComparison.OrdinalIgnoreCase))
        {
            localOnly = true;
            query = query.TrimEnd()[..^" local".Length].Trim();
        }

        // "/cvlocal Titulo || Descripcion" para una oferta que pegues tú y no esté registrada
        if (query.Contains(TitleDescriptionSeparator, StringComparison.Ordinal))
        {
            await GenerateFromPastedOfferAsync(bot, chatId, query, ct);
            return;
        }

        if (query.Length == 0)
        {
            await SendHtmlAsync(bot, chatId, "⚠️ Falta la oferta. Uso: <code>/cv &lt;id&gt;</code> o <code>/cv &lt;id&gt; local</code>.", ct);
            return;
        }

        var offer = _db.FindProcessedOffer(query, out var previousScore);
        if (offer == null)
        {
            await SendHtmlAsync(bot, chatId,
                $"🔍 No encuentro ninguna oferta registrada que coincida con <code>{Html(query)}</code>. " +
                "Prueba con el identificador de la oferta, usa <code>/stats</code>, o pégala directamente: " +
                "<code>/cvlocal Título || descripción</code>.", ct);
            return;
        }

        await SendHtmlAsync(bot, chatId,
            localOnly
                ? $"📄 Generando el CV local de <b>{Html(offer.Title)}</b> (sin Gemini)...\n<i>{Html(offer.Company)}</i>"
                : $"📄 Regenerando el CV para <b>{Html(offer.Title)}</b> (registrada con {previousScore}/100)...\n<i>{Html(offer.Company)}</i>", ct);

        var result = await ResolveManual().RegenerateCvAsync(offer, ct, localOnly);

        await SendHtmlAsync(bot, chatId, result.Message, ct);

        if (result.Ok && !string.IsNullOrEmpty(result.PdfPath) && System.IO.File.Exists(result.PdfPath))
        {
            var caption = result.IsLocal
                ? $"📄 CV para {offer.Title} (generado sin IA)."
                : $"📄 CV adaptado para {offer.Title} ({result.Score}/100).";
            await SendDocumentSafeAsync(chatId, result.PdfPath, caption);
        }
    }

    /// <summary>
    /// Genera el CV de una oferta que el usuario pega, para cuando no hay cuota y la oferta nunca
    /// llegó a registrarse. Se usa como título y descripción de la oferta, sin tocar la base.
    /// </summary>
    private async Task GenerateFromPastedOfferAsync(ITelegramBotClient bot, long chatId, string argument, CancellationToken ct)
    {
        var parts = argument.Split(TitleDescriptionSeparator, 2, StringSplitOptions.TrimEntries);
        var title = parts[0];
        var description = parts.Length > 1 ? parts[1] : string.Empty;

        if (title.Length == 0 || description.Length == 0)
        {
            await SendHtmlAsync(bot, chatId,
                "⚠️ Formato: <code>/cvlocal Título de la oferta || Descripción completa de la oferta</code>.", ct);
            return;
        }

        var offer = new JobOffer(
            Guid.NewGuid().ToString("N")[..12], title, "", "", description,
            DateTime.UtcNow, "", "", false);

        await SendHtmlAsync(bot, chatId, $"📄 Generando el CV local de <b>{Html(title)}</b> (sin Gemini)...", ct);

        var result = await ResolveManual().RegenerateCvAsync(offer, ct, localOnly: true);

        await SendHtmlAsync(bot, chatId, result.Message, ct);

        if (result.Ok && !string.IsNullOrEmpty(result.PdfPath) && System.IO.File.Exists(result.PdfPath))
        {
            await SendDocumentSafeAsync(chatId, result.PdfPath, $"📄 CV para {title} (generado sin IA).");
        }
    }

    private async Task HandleUnmarkAsync(ITelegramBotClient bot, long chatId, string argument, CancellationToken ct)
    {
        if (await RequireArgument(bot, chatId, argument, "/unmark &lt;id | parte del título | url&gt;", "li_4155609388", ct)) return;

        var offer = _db.FindProcessedOffer(argument, out _);
        if (offer == null)
        {
            await SendHtmlAsync(bot, chatId,
                $"🔍 No encuentro ninguna oferta registrada que coincida con <code>{Html(argument)}</code>.", ct);
            return;
        }

        var removed = _db.RemoveProcessed(offer.Id);
        await SendHtmlAsync(bot, chatId, removed
            ? $"🧹 <b>{Html(offer.Title)}</b> vuelve a estar pendiente: se reevaluará en el próximo ciclo o con <code>/scan</code>."
            : "⚠️ No se pudo eliminar el registro.", ct);
    }

    private async Task HandleStatsAsync(ITelegramBotClient bot, long chatId)
    {
        var stats = _db.GetStats(_config.MinScoreThreshold);
        await SendHtmlAsync(bot, chatId,
            "📈 <b>Estadísticas</b>\n\n" +
            $"• Ofertas evaluadas: <b>{stats.Total}</b>\n" +
            $"• Por encima del umbral ({_config.MinScoreThreshold}): <b>{stats.Matches}</b>\n" +
            $"• Media de afinidad: <b>{stats.AverageScore}</b>/100\n" +
            $"• Últimas 24 h: <b>{stats.Last24h}</b>\n\n" +
            "<i>Regenera el CV de cualquiera de ellas con <code>/cv &lt;id&gt;</code>.</i>",
            CancellationToken.None);
    }

    /// <summary>Devuelve true si faltaba el argumento (ya se ha enviado el mensaje de uso).</summary>
    private async Task<bool> RequireArgument(
        ITelegramBotClient bot,
        long chatId,
        string argument,
        string usage,
        string? example,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(argument)) return false;

        var text = $"⚠️ Uso: <code>{usage}</code>";
        if (!string.IsNullOrEmpty(example)) text += $"\nEjemplo: <code>{example}</code>";

        await SendHtmlAsync(bot, chatId, text, ct);
        return true;
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

            // Espera creciente hasta 30s, para no ir golpeando la API mientras dura el conflicto
            return Task.Delay(TimeSpan.FromSeconds(Math.Min(5 * _consecutiveConflicts, 30)), ct);
        }

        // Cualquier error que no sea 409 deja el contador a cero
        _consecutiveConflicts = 0;
        _firstConflictAt = null;

        _logger.LogError(ex, "Error en Telegram Polling.");
        return Task.CompletedTask;
    }

    public async Task SendNotificationAsync(JobOffer job, EvaluationResult eval, string? pdfPath, CancellationToken ct)
    {
        string locationText;
        if (job.IsRemote)
        {
            locationText = "🏠 100% Remoto";
        }
        else if (!string.IsNullOrWhiteSpace(job.City) && !string.IsNullOrWhiteSpace(job.Province) && job.City != job.Province)
        {
            locationText = $"📍 {job.City}, {job.Province}";
        }
        else if (!string.IsNullOrWhiteSpace(job.Province))
        {
            locationText = $"📍 {job.Province}";
        }
        else if (!string.IsNullOrWhiteSpace(job.City))
        {
            locationText = $"📍 {job.City}";
        }
        else
        {
            locationText = "📍 No especificada";
        }

        var strengthsText = eval.Strengths is { Count: > 0 }
            ? string.Join("\n", eval.Strengths.Select(s => $"• {Html(s)}"))
            : "• <i>No especificados</i>";

        var concernsText = eval.Concerns is { Count: > 0 }
            ? string.Join("\n", eval.Concerns.Select(c => $"• {Html(c)}"))
            : "• <i>Ninguno</i>";

        // Sin el margen el texto sale con un tab al principio
        var message =
$@"🎯 <b>NUEVA OFERTA COMPATIBLE</b> ({eval.Score}/100)

🏢 <b>Empresa:</b> {Html(job.Company)}
💼 <b>Puesto:</b> {Html(job.Title)}
📌 <b>Ubicación:</b> {Html(locationText)}

✅ <b>Puntos fuertes:</b>
{strengthsText}

⚠️ <b>A revisar:</b>
{concernsText}";

        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithUrl("🌐 Abrir oferta", job.Link)
        });

        // Texto y PDF van en try separados: si el texto falla, el documento llega igual
        try
        {
            await _botClient.SendTextMessageAsync(
                chatId: _config.TelegramChatId,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: inlineKeyboard,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enviar el mensaje de la oferta '{Title}'.", job.Title);
        }

        if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
        {
            await SendDocumentSafeAsync(_config.TelegramChatId, pdfPath, "📄 CV adaptado y listo para adjuntar.");
        }
        else
        {
            _logger.LogWarning("No hay PDF que adjuntar para '{Title}'.", job.Title);
        }
    }

    /// <summary>Manda un aviso al chat configurado (texto ya en HTML).</summary>
    public async Task SendSystemAlertAsync(string html, CancellationToken ct = default)
    {
        if (_config.TelegramChatId == 0) return;

        try
        {
            await _botClient.SendTextMessageAsync(
                chatId: _config.TelegramChatId,
                text: html,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo enviar el aviso de sistema al chat {ChatId}.", _config.TelegramChatId);
        }
    }

    private async Task SendDocumentSafeAsync(long chatId, string pdfPath, string caption)
    {
        try
        {
            await using var stream = System.IO.File.OpenRead(pdfPath);
            await _botClient.SendDocumentAsync(
                chatId: chatId,
                document: InputFile.FromStream(stream, System.IO.Path.GetFileName(pdfPath)),
                caption: caption,
                cancellationToken: CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enviar el PDF '{File}'.", System.IO.Path.GetFileName(pdfPath));
        }
    }

    /// <summary>Envía texto con escape HTML y parseo explícito (el sitio más frágil del bot).</summary>
    private async Task SendHtmlAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken ct = default)
    {
        try
        {
            await bot.SendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo enviar un mensaje de texto al chat {ChatId}.", chatId);
        }
    }

    private Task SendAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken ct) =>
        bot.SendTextMessageAsync(chatId: chatId, text: text, cancellationToken: ct);

    private static string BuildHelpText() =>
        "🤖 <b>CareerCopilot — panel de control</b>\n\n" +
        "<b>Búsqueda</b>\n" +
        "• <code>/run</code> — ciclo completo de búsqueda y análisis\n" +
        "• <code>/scan &lt;término&gt;</code> — buscar un término concreto sin añadirlo a las búsquedas\n" +
        "• <code>/test</code> — probar Gemini + Typst + Telegram con una oferta ficticia\n\n" +
        "<b>CVs</b>\n" +
        "• <code>/cv &lt;id | parte del título | empresa | url&gt;</code> — reevaluar y regenerar el CV de una oferta ya vista\n" +
        "• <code>/cv &lt;id&gt; local</code> · <code>/cvlocal &lt;id&gt;</code> — generar el CV sin Gemini, para cuando no hay cuota\n" +
        "• <code>/cvlocal &lt;título&gt; || &lt;descripción&gt;</code> — generar el CV de una oferta que pegues\n" +
        "• <code>/unmark &lt;id&gt;</code> — desmarcar una oferta para que se reevalúe en el próximo ciclo\n" +
        "• <code>/stats</code> — ofertas evaluadas, coincidencias y media de afinidad\n\n" +
        "<b>Búsquedas persistentes</b>\n" +
        "• <code>/addjob &lt;término&gt;</code> · <code>/removejob &lt;término&gt;</code> · <code>/listjobs</code>\n\n" +
        "<b>Filtros y puntuación</b>\n" +
        "• <code>/addrequired &lt;palabra&gt;</code> · <code>/removerequired &lt;palabra&gt;</code>\n" +
        "• <code>/addexcluded &lt;palabra&gt;</code> · <code>/removeexcluded &lt;palabra&gt;</code>\n" +
        "• <code>/filters</code> — ver los filtros activos\n" +
        "• <code>/threshold &lt;0-100&gt;</code> — corte de afinidad para generar CV\n\n" +
        "<b>General</b>\n" +
        "• <code>/status</code> — métricas y estado del escaneo\n" +
        "• <code>/help</code> — esta ayuda";

    /// <summary>Escape para ParseMode.Html: solo tres caracteres tienen entidad reservada.</summary>
    private static string Html(string? text) =>
        (text ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    private sealed class NullManualActions : IManualActions
    {
        public static readonly NullManualActions Instance = new();

        public bool IsBusy => false;
        public string BusyOrigin => "indisponible";
        public Task<bool> RunPipelineAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RunAdHocScanAsync(string query, CancellationToken ct) => Task.FromResult(false);
        public Task<CvRequestResult> RegenerateCvAsync(JobOffer offer, CancellationToken ct, bool localOnly = false) =>
            Task.FromResult(new CvRequestResult(false, "❌ El pipeline no está disponible."));

        public KeywordCoverage CheckKeywords(JobOffer offer) => new([], []);
    }
}