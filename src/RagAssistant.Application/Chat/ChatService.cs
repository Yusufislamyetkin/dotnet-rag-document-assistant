using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Retrieval;
using RagAssistant.Application.Usage;
using RagAssistant.Domain;

namespace RagAssistant.Application.Chat;

public sealed record ChatRequest(Guid? SessionId, string Question, IReadOnlyList<Guid>? DocumentIds);

public abstract record ChatEvent(string Type);
public sealed record ChatMetaEvent(Guid SessionId, Guid UserMessageId, string SearchQuery) : ChatEvent("meta");
public sealed record ChatTokenEvent(string Text) : ChatEvent("token");
public sealed record ChatCitationsEvent(IReadOnlyList<Citation> Citations) : ChatEvent("citations");
public sealed record ChatDoneEvent(Guid MessageId, bool AnswerFound, int InputTokens, int OutputTokens, decimal EstimatedCostUsd,
    int InvalidCitations) : ChatEvent("done");
public sealed record ChatErrorEvent(string Message) : ChatEvent("error");

public sealed class ChatValidationException(string message) : Exception(message);

public sealed class ChatService(
    IAppDbContext db,
    HybridRetriever retriever,
    IChatModel model,
    IAuxiliaryChatModel auxModel,
    IEmbeddingService embeddings,
    UsageRecorder usage,
    IOptions<ChatOptions> options,
    ILogger<ChatService> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Validates the request and creates/loads the session. Throws <see cref="ChatValidationException"/>.</summary>
    public async Task<ChatSession> PrepareAsync(Guid userId, ChatRequest request, CancellationToken ct)
    {
        var question = request.Question?.Trim() ?? "";
        if (question.Length == 0) throw new ChatValidationException("Question is required.");
        if (question.Length > options.Value.MaxQuestionChars)
            throw new ChatValidationException($"Question is longer than {options.Value.MaxQuestionChars} characters.");

        if (request.DocumentIds is { Count: > 0 } ids)
        {
            var owned = await db.Documents.CountAsync(d => d.UserId == userId && ids.Contains(d.Id), ct);
            if (owned != ids.Distinct().Count()) throw new ChatValidationException("Unknown document id in filter.");
        }

        if (request.SessionId is { } sessionId)
        {
            return await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct)
                ?? throw new ChatValidationException("Chat session not found.");
        }

        var session = new ChatSession { UserId = userId, Title = question.Length > 80 ? question[..80] + "…" : question };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(Guid userId, ChatSession session, ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("chat.answer");
        var question = request.Question.Trim();
        var o = options.Value;

        var history = await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == session.Id)
            .OrderByDescending(m => m.CreatedAt).Take(o.HistoryMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new PromptMessage(m.Role == ChatRole.User ? PromptRole.User : PromptRole.Assistant, m.Content))
            .ToListAsync(ct);

        var userMessage = new ChatMessage { SessionId = session.Id, Role = ChatRole.User, Content = question };
        db.ChatMessages.Add(userMessage);
        session.UpdatedAt = DateTimeOffset.UtcNow;

        // 1. Standalone query rewrite (only for follow-up questions).
        var searchQuery = question;
        if (history.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            var rewrite = await auxModel.CompleteAsync(PromptBuilder.BuildRewrite(question, history), 200, ct);
            await usage.RecordAsync(userId, null, UsageOperations.Rewrite, auxModel.Provider, auxModel.Model,
                rewrite.Usage.InputTokens, rewrite.Usage.OutputTokens, (int)sw.ElapsedMilliseconds, ct, save: false);
            var rewritten = rewrite.Text.Trim().Trim('"');
            if (rewritten.Length is > 0 and < 1000) searchQuery = rewritten;
        }
        userMessage.RewrittenQuery = searchQuery == question ? null : searchQuery;
        await db.SaveChangesAsync(ct);
        yield return new ChatMetaEvent(session.Id, userMessage.Id, searchQuery);

        // 2. Hybrid retrieval.
        var embedWatch = Stopwatch.StartNew();
        var retrieval = await retriever.RetrieveAsync(userId, searchQuery, request.DocumentIds, null, ct);
        await usage.RecordAsync(userId, null, UsageOperations.Embed, embeddings.Provider, embeddings.Model,
            retrieval.EmbeddingTokens, 0, (int)embedWatch.ElapsedMilliseconds, ct, save: false);

        var assistant = new ChatMessage { SessionId = session.Id, Role = ChatRole.Assistant };

        // 3. "I don't know" without spending LLM tokens when retrieval has nothing relevant.
        if (!retriever.IsGrounded(retrieval))
        {
            activity?.SetTag("chat.grounded", false);
            assistant.Content = PromptBuilder.NotFoundAnswer;
            assistant.CitationsJson = "[]";
            db.ChatMessages.Add(assistant);
            await db.SaveChangesAsync(ct);
            yield return new ChatTokenEvent(PromptBuilder.NotFoundAnswer);
            yield return new ChatCitationsEvent([]);
            yield return new ChatDoneEvent(assistant.Id, false, 0, 0, 0m, 0);
            yield break;
        }

        // 4. Grounded generation, streamed token by token.
        var grounded = PromptBuilder.BuildAnswer(question, retrieval.Chunks, history);
        var answer = new StringBuilder();
        TokenUsage? tokenUsage = null;
        var watch = Stopwatch.StartNew();
        await foreach (var update in model.StreamAsync(grounded.Prompt, o.MaxAnswerTokens, ct))
        {
            if (update.Usage is not null) tokenUsage = update.Usage;
            if (string.IsNullOrEmpty(update.Text)) continue;
            answer.Append(update.Text);
            yield return new ChatTokenEvent(update.Text);
        }

        var text = answer.ToString().Trim();
        if (text.Length == 0)
        {
            text = PromptBuilder.NotFoundAnswer;
            yield return new ChatTokenEvent(text);
        }
        var answerFound = !text.Contains(PromptBuilder.NotFoundAnswer, StringComparison.OrdinalIgnoreCase);

        // 5. Citations: parsed from the answer, validated against the chunks that were in the prompt.
        var parsed = CitationParser.Parse(text, grounded.Aliases, grounded.Chunks);
        if (parsed.Invalid.Count > 0)
            logger.LogWarning("Answer contained {Count} invalid citations: {Invalid}", parsed.Invalid.Count, parsed.Invalid);

        assistant.Content = text;
        assistant.CitationsJson = JsonSerializer.Serialize(parsed.Citations, Json);
        db.ChatMessages.Add(assistant);
        tokenUsage ??= new TokenUsage(0, 0);
        var row = await usage.RecordAsync(userId, assistant.Id, UsageOperations.Chat, model.Provider, model.Model,
            tokenUsage.InputTokens, tokenUsage.OutputTokens, (int)watch.ElapsedMilliseconds, ct, save: false);
        await db.SaveChangesAsync(ct);

        activity?.SetTag("chat.grounded", true);
        activity?.SetTag("chat.citations", parsed.Citations.Count);
        yield return new ChatCitationsEvent(parsed.Citations);
        yield return new ChatDoneEvent(assistant.Id, answerFound, tokenUsage.InputTokens, tokenUsage.OutputTokens,
            row.EstimatedCostUsd, parsed.Invalid.Count);
    }
}
