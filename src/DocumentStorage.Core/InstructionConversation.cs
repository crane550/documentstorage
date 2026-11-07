using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed record ConversationDraft(string Reply, bool NeedsClarification, AssistantProposal? Proposal);
public sealed partial class LibraryService
{
    private readonly SemaphoreSlim guidanceDrafts = new(1, 1);
    public async Task<ConversationDraft> DraftInstructionsAsync(InstructionTarget target, string actor, IOllamaClient client, CancellationToken cancellationToken = default)
    {
        Actor(actor);
        var thread = await InstructionThreadAsync(target);
        var messages = thread.Messages.TakeLast(6).ToArray();
        var lastRequest = messages.LastOrDefault(x => x.Role == "user") ?? throw new StorageException("Write a source request first.");
        var context = await AssistantContextAsync();
        var key = "conversation_" + lastRequest.Id.ToString("N") + "_" + thread.Hash[..12];
        OllamaSettings settings;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var prior = await db.AssistantProposals.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == actor && x.ConnectionKey == "workspace" && x.IdempotencyKey == key);
            if (prior != null) return new("This request already has a saved proposal.", false, prior);
            settings = await db.OllamaConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1);
        }
        if (!settings.Enabled) throw new StorageException("Your message is saved. Enable a local model in Local AI to draft updated guidance, or use an external MCP assistant.");
        if (!await guidanceDrafts.WaitAsync(TimeSpan.Zero, cancellationToken)) throw new StorageException("Another guidance draft is running. Your source request is saved; retry shortly.");
        GuidanceDraftResult result;
        try
        {
            await modelWork.WaitAsync(cancellationToken);
            try
            {
            result = await WithReaderAsync(settings, client, (reader, seconds, token) => client.DraftGuidanceAsync(new(reader.Endpoint, reader.Model, seconds, target.Kind, thread.Text,
            JsonSerializer.Serialize(messages.Select(x => new { x.Role, x.Text })), settings.KeepAliveSeconds), token), cancellationToken);
            }
            finally { modelWork.Release(); }
        }
        finally { guidanceDrafts.Release(); }
        cancellationToken.ThrowIfCancellationRequested();
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            var current = await db.OllamaConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
            if (!current.Enabled || current.Revision != settings.Revision) throw new StorageException("Local model settings changed during drafting. Your source message is saved; draft again.");
        }
        AssistantProposal? proposal = null;
        if (!result.NeedsClarification)
            proposal = await ProposeAssistantAsync("Guidance", JsonSerializer.Serialize(new GuidanceChange(target, thread.Hash, result.Guidance, result.Reply, messages.Select(x => x.Id).ToArray())), context.Hash, key, new(actor, null));
        await writer.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            db.InstructionMessages.Add(new() { TargetKind = thread.Target.Kind, TargetKey = thread.Target.Key, Role = "assistant", Text = result.Reply, Actor = actor, CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { writer.Release(); }
        return new(result.Reply, result.NeedsClarification, proposal);
    }
}
