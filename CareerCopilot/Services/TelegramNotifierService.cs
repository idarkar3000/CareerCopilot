using Telegram.Bot;
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

    public TelegramNotifierService(
        BotConfig config,
        JobDatabase db,
        ILogger<TelegramNotifierService> logger,
        GeminiScorerService scorer,
        CvCompilerService cvCompiler)
    {
        _config = config;
        _db = db;
        _logger = logger;
        _scorer = scorer;
        _cvCompiler = cvCompiler;
        _botClient = new TelegramBotClient(_config.TelegramBotToken);
    }

    public void StartReceiving(CancellationToken ct)
    {
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
                    await bot.SendTextMessageAsync(message.Chat.Id, "❌ Error: Gemini devolvió nulo. Revisa la API Key.", cancellationToken: ct);
                    return;
                }

                var pdf = await _cvCompiler.GeneratePdfAsync(mockJob, eval, ct);
                await SendNotificationAsync(mockJob, eval, pdf, ct);
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

            case "/help":
            case "/start":
                var helpText = @"🤖 *Comandos disponibles:*
• `/test` — Prueba inmediata (evalúa una oferta ficticia con Gemini y te manda el PDF).
• `/addjob <texto>` — Añadir un nuevo título/búsqueda.
• `/removejob <texto>` — Eliminar una búsqueda.
• `/listjobs` — Ver todas las búsquedas activas.
• `/help` — Mostrar este mensaje.";
                await bot.SendTextMessageAsync(message.Chat.Id, helpText, parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
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