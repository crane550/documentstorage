using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

internal static class AssistantChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool,string> check, Func<Func<Task>,string,Task> refuses)
    {
        await GuidanceProtocolAsync(check);
        Directory.CreateDirectory(scratch); var clock = new Clock(); var library = new LibraryService(factory, new DocumentTextReader(), clock);
        var root = await library.AddRootAsync("Assistant synthetic archive", scratch, false);
        var rootFolder = (await library.OverviewAsync()).Folders.Single(x => x.RootId == root && x.ParentId == null);
        var incoming = await library.CreateFolderAsync(rootFolder.Id, "Incoming"); var archive = await library.CreateFolderAsync(rootFolder.Id, "Filed");
        await library.SetIntakeAsync(incoming, true);
        var target = new InstructionTarget("folder", incoming.ToString()); var actor = "assistant-owner";
        var connection = new AssistantConnection { OwnerId = actor, ClientId = "synthetic-client", Name = "Synthetic assistant", ScopesJson = JsonSerializer.Serialize(LibraryService.AssistantScopes), CreatedAt = clock.GetUtcNow() };
        var reader = new AssistantConnection { OwnerId = actor, ClientId = "reader-client", Name = "Read only", ScopesJson = "[\"catalog.read\"]", CreatedAt = clock.GetUtcNow() };
        await using (var db = await factory.CreateDbContextAsync()) { db.AssistantConnections.AddRange(connection, reader); await db.SaveChangesAsync(); }
        var caller = new AssistantCaller(actor, connection.Id);
        await library.SaveInstructionsAsync(incoming, "Keep original requests.", actor);
        var thread = await library.InstructionThreadAsync(target);
        var source = await library.AddInstructionMessageAsync(target, "These are synthetic statements. Use the closing date.", caller, "source-message-001");
        check(await library.AddInstructionMessageAsync(target, "These are synthetic statements. Use the closing date.", caller, "source-message-001") == source, "source-message retries preserve one original conversation message");
        await refuses(() => library.AddInstructionMessageAsync(target, "Changed input", caller, "source-message-001"), "message request keys cannot be reused for different source text");
        var context = await library.AssistantContextAsync(incoming);
        check(context.Json.Contains("Keep original requests.") && !context.Json.Contains(scratch), "assistant context contains inherited instructions without physical server paths");
        var change = new GuidanceChange(target, thread.Hash, "Identify monthly statements using the closing date.", "Condensed the source request.", [source]);
        var payload = JsonSerializer.Serialize(change);
        var proposal = await library.ProposeAssistantAsync("Guidance", payload, context.Hash, "guidance-key-001", caller);
        check((await library.InstructionThreadAsync(target)).Text == thread.Text, "assistant guidance proposal never publishes instructions directly");
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => library.ProposeAssistantAsync("Guidance", payload, context.Hash, "guidance-key-001", caller)));
        check(duplicates.All(x => x.Id == proposal.Id), "concurrent retries persist exactly one proposal for one connection and request key");
        await refuses(() => library.ProposeAssistantAsync("Guidance", JsonSerializer.Serialize(change with { Text = "Different input" }), context.Hash, "guidance-key-001", caller), "a reused request key with changed payload is rejected");
        await refuses(() => library.ProposeAssistantAsync("Guidance", payload, context.Hash, "reader-denied-key", new(actor, reader.Id)), "a read-only connection cannot propose instruction changes");
        await refuses(() => library.AssistantProposalAsync(proposal.Id, new(actor, reader.Id)), "proposal IDs do not bypass connection ownership");
        await refuses(() => library.DecideAssistantProposalAsync(proposal.Id, proposal.Revision, true, "other-owner"), "a different account cannot accept a proposal");
        await library.DecideAssistantProposalAsync(proposal.Id, proposal.Revision, true, actor);
        thread = await library.InstructionThreadAsync(target);
        check(thread.Text == change.Text && thread.History.Count == 2 && thread.History.First().SourceMessagesJson.Contains(source.ToString()), "accepted instruction versions retain immutable text and source-message links");
        await library.DecideAssistantProposalAsync(proposal.Id, proposal.Revision, true, actor);
        check((await library.InstructionThreadAsync(target)).History.Count == 2, "repeated browser acceptance does not create duplicate instruction versions");
        check((await library.ProposeAssistantAsync("Guidance", payload, context.Hash, "guidance-key-001", caller)).State == "Applied", "an identical retry returns its completed result even after guidance context changed");
        await refuses(() => library.ProposeAssistantAsync("Guidance", JsonSerializer.Serialize(change with { BaseHash = thread.Hash }), context.Hash, "stale-context-key", caller), "changed workspace context prevents a fresh proposal using an old context hash");
        context = await library.AssistantContextAsync();
        var stale = await library.ProposeAssistantAsync("Guidance", JsonSerializer.Serialize(change with { BaseHash = thread.Hash, Text = "Stale draft" }), context.Hash, "stale-conversation-key", caller);
        clock.Advance(1); await library.AddInstructionMessageAsync(target, "Correction: keep the statement's account suffix too.", caller);
        await refuses(() => library.DecideAssistantProposalAsync(stale.Id, stale.Revision, true, actor), "a newer source request blocks acceptance of an earlier conversation draft");
        await library.DecideAssistantProposalAsync(stale.Id, stale.Revision, false, actor);
        check((await library.InstructionThreadAsync(target)).Text == change.Text, "rejecting a conversation draft preserves canonical guidance");

        var settings = (await library.IdentificationOverviewAsync()).Settings;
        settings.Enabled = true; settings.Model = "synthetic-guide:latest";
        settings = await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);
        clock.Advance(1); await library.AddInstructionMessageAsync(target, "What if the period date is missing?", new(actor, null));
        var clarification = await library.DraftInstructionsAsync(target, actor, new Guide((_, _) => Task.FromResult(new GuidanceDraftResult("Which fallback date should be used?", "Never publish this text.", true))));
        check(clarification.NeedsClarification && clarification.Proposal == null && (await library.InstructionThreadAsync(target)).Text == change.Text, "a conversational clarification preserves canonical guidance and creates no publishable proposal");
        clock.Advance(1); await library.AddInstructionMessageAsync(target, "Flag missing dates for review.", new(actor, null));
        var guideCalls = 0;
        var guideClient = new Guide((request, _) => { guideCalls++; check(request.CurrentGuidance == change.Text && request.ConversationJson.Contains("Flag missing dates"), "guidance drafting receives the existing instructions and source conversation"); return Task.FromResult(new GuidanceDraftResult("A concise draft.", "Use the closing date. Flag missing dates for review.", false)); });
        var draft = await library.DraftInstructionsAsync(target, actor, guideClient);
        check(draft.Proposal?.State == "Pending" && (await library.InstructionThreadAsync(target)).Text == change.Text, "local conversation summaries remain separate proposals until accepted");
        var repeated = await library.DraftInstructionsAsync(target, actor, guideClient);
        check(repeated.Proposal?.Id == draft.Proposal!.Id && guideCalls == 1, "retrying a completed conversation request reuses its proposal without another model call");
        await library.DecideAssistantProposalAsync(draft.Proposal!.Id, draft.Proposal.Revision, true, actor);
        check((await library.InstructionThreadAsync(target)).Text.Contains("Flag missing dates"), "accepted local conversation summaries become versioned folder guidance");
        clock.Advance(1); await library.AddInstructionMessageAsync(target, "Keep this concise.", new(actor, null));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<GuidanceDraftResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drafting = library.DraftInstructionsAsync(target, actor, new Guide((_, _) => { started.SetResult(); return completion.Task; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await refuses(() => library.DraftInstructionsAsync(target, actor, guideClient), "guidance inference is bounded to one active draft without blocking library writes");
        await library.SaveInstructionsAsync(incoming, "Changed while the model reads.", actor);
        completion.SetResult(new("Old draft.", "Stale guidance.", false));
        await refuses(async () => await drafting, "editing instructions during inference prevents a stale conversation summary from being proposed");
        settings.Enabled = false; await library.SaveOllamaSettingsAsync(settings, settings.Revision, actor);

        var input = new RuleInput(null, 0, "Synthetic assistant filing", "assistant_statement", archive, new(), "{document_date:yyyy-MM-dd} - {institution}.{extension}", ["document_date", "institution"], false);
        context = await library.AssistantContextAsync();
        int rulesBefore, versionsBefore;
        await using (var db = await factory.CreateDbContextAsync()) { rulesBefore = await db.FilingRules.CountAsync(); versionsBefore = await db.RuleRevisions.CountAsync(); }
        var ruleProposal = await library.ProposeAssistantAsync("Rule", JsonSerializer.Serialize(input), context.Hash, "new-rule-key-001", caller);
        await using (var db = await factory.CreateDbContextAsync()) check(await db.FilingRules.CountAsync() == rulesBefore && await db.RuleRevisions.CountAsync() == versionsBefore, "rule-proposal validation rolls back rule and version writes without altering the catalog");
        await refuses(() => library.ProposeAssistantAsync("Rule", JsonSerializer.Serialize(input with { Enabled = true }), context.Hash, "invalid-enabled-rule", caller), "external assistants cannot propose already-enabled rules");
        await refuses(() => library.ProposeAssistantAsync("Rule", JsonSerializer.Serialize(input with { FilenameTemplate = "../escape.txt" }), context.Hash, "invalid-rule-path", caller), "proposed rules use the real engine's path and template validation");
        await library.DecideAssistantProposalAsync(ruleProposal.Id, ruleProposal.Revision, true, actor);
        var rule = (await library.RulesAsync()).Single(x => x.Name == input.Name);
        check(!rule.Enabled && (await library.RuleHistoryAsync(rule.Id)).Count == 1, "accepting a rule proposal atomically saves a paused versioned rule");
        await library.SaveRuleAsync(input with { Id = rule.Id, ExpectedRevision = rule.Revision, Enabled = true }, actor);
        const string original = "Synthetic assistant statement. Institution Elm. Date 2026-10-31.";
        await library.UploadAsync(incoming, "scan.txt", new MemoryStream(Encoding.UTF8.GetBytes(original)));
        clock.Advance(6); await library.ScanAsync();
        var document = (await library.BrowseAsync(incoming)).Items.Single(x => x.Name == "scan.txt");
        var facts = new Dictionary<string,string> { ["institution"] = "Elm", ["document_date"] = "2026-10-31" };
        context = await library.AssistantContextAsync();
        await refuses(() => library.ProposeAssistantAsync("Filing", JsonSerializer.Serialize(new FilingRequest(document.Id)), context.Hash, "unreviewed-filing-key", caller), "an assistant cannot request executable filing for unreviewed facts");
        var review = await library.SaveReviewAsync(document.Id, 0, "assistant_statement", facts, true, actor);
        var filing = await library.ProposeAssistantAsync("Filing", JsonSerializer.Serialize(new FilingRequest(document.Id)), context.Hash, "filing-key-001", caller);
        await refuses(() => library.ExecuteAssistantFilingAsync(filing.Id, caller), "MCP filing cannot self-approve a pending request");
        check(File.Exists(Path.Combine(scratch,"Incoming/scan.txt")), "requesting and inspecting filing approval leaves the original in place");
        await library.DecideAssistantProposalAsync(filing.Id, filing.Revision, true, actor);
        var operation = await library.ExecuteAssistantFilingAsync(filing.Id, caller);
        check(await File.ReadAllTextAsync(Path.Combine(scratch,"Filed/2026-10-31 - Elm.txt")) == original && (await library.AssistantProposalAsync(filing.Id, caller)).State == "Executed", "approved MCP filing uses the journal and commits its result with the catalog move");
        clock.Advance(1000);
        check(await library.ExecuteAssistantFilingAsync(filing.Id, caller) == operation, "completed filing retries return the same operation even after approval expiry");
        var audit = (await library.AssistantChangesAsync(actor)).Audit;
        check(audit.Any(x => x.Action == "filing.execute" && x.ProposalId == filing.Id), "assistant execution has a durable audit entry linked to the approved proposal");

        // Simulate the process stopping after the approved bytes moved, before the catalog commit.
        await library.UploadAsync(incoming, "recover.txt", new MemoryStream(Encoding.UTF8.GetBytes(original))); clock.Advance(6); await library.ScanAsync();
        var recoverDocument = (await library.BrowseAsync(incoming)).Items.Single(x => x.Name == "recover.txt");
        await library.SaveReviewAsync(recoverDocument.Id, 0, "assistant_statement", new() { ["institution"] = "Birch", ["document_date"] = "2026-10-31" }, true, actor);
        context = await library.AssistantContextAsync();
        var recoverApproval = await library.ProposeAssistantAsync("Filing", JsonSerializer.Serialize(new FilingRequest(recoverDocument.Id)), context.Hash, "recover-filing-001", caller);
        await library.DecideAssistantProposalAsync(recoverApproval.Id, recoverApproval.Revision, true, actor);
        async Task<FileOperation> Prepare(AssistantProposal approval, CatalogEntry entry)
        {
            await using var db = await factory.CreateDbContextAsync();
            var preview = await db.FilingPreviews.SingleAsync(x => x.Id == approval.FilingPreviewId);
            var op = new FileOperation { Kind = "File", RootId = root, EntryId = entry.Id, FromPath = entry.RelativePath, ToPath = preview.DestinationPath, TargetParentId = archive, FilingPreviewId = preview.Id };
            preview.OperationId = op.Id; db.Operations.Add(op); await db.SaveChangesAsync();
            File.Move(StoragePaths.Resolve(scratch, op.FromPath), StoragePaths.Resolve(scratch, op.ToPath)); return op;
        }
        var interrupted = await Prepare(recoverApproval, recoverDocument); await library.ScanAsync();
        check((await library.AssistantProposalAsync(recoverApproval.Id, caller)).State == "Executed" && await library.ExecuteAssistantFilingAsync(recoverApproval.Id, caller) == interrupted.Id,
            "restart recovery completes an interrupted MCP filing and retains its idempotent operation result");
        check(await File.ReadAllTextAsync(StoragePaths.Resolve(scratch, interrupted.ToPath)) == original, "MCP filing recovery verifies and preserves the approved bytes");

        await library.UploadAsync(incoming, "second.txt", new MemoryStream(Encoding.UTF8.GetBytes(original))); clock.Advance(6); await library.ScanAsync();
        document = (await library.BrowseAsync(incoming)).Items.Single(x => x.Name == "second.txt");
        await library.SaveReviewAsync(document.Id, 0, "assistant_statement", new() { ["institution"] = "Oak", ["document_date"] = "2026-10-31" }, true, actor);
        context = await library.AssistantContextAsync();
        filing = await library.ProposeAssistantAsync("Filing", JsonSerializer.Serialize(new FilingRequest(document.Id)), context.Hash, "filing-revoked-001", caller);
        await library.DecideAssistantProposalAsync(filing.Id, filing.Revision, true, actor);
        await library.UploadAsync(incoming, "bad-recovery.txt", new MemoryStream(Encoding.UTF8.GetBytes(original))); clock.Advance(6); await library.ScanAsync();
        var badDocument = (await library.BrowseAsync(incoming)).Items.Single(x => x.Name == "bad-recovery.txt");
        await library.SaveReviewAsync(badDocument.Id, 0, "assistant_statement", new() { ["institution"] = "Spruce", ["document_date"] = "2026-10-31" }, true, actor);
        var badApproval = await library.ProposeAssistantAsync("Filing", JsonSerializer.Serialize(new FilingRequest(badDocument.Id)), (await library.AssistantContextAsync()).Hash, "bad-recovery-001", caller);
        await library.DecideAssistantProposalAsync(badApproval.Id, badApproval.Revision, true, actor);
        var badOperation = await Prepare(badApproval, badDocument);
        var badPath = StoragePaths.Resolve(scratch, badOperation.ToPath); var modified = File.GetLastWriteTimeUtc(badPath);
        await File.WriteAllTextAsync(badPath, new string('X', Encoding.UTF8.GetByteCount(original))); File.SetLastWriteTimeUtc(badPath, modified);
        await library.ScanAsync();
        await using (var db = await factory.CreateDbContextAsync()) check((await db.Operations.SingleAsync(x=>x.Id==badOperation.Id)).State=="Needs review" && (await db.AssistantProposals.SingleAsync(x=>x.Id==badApproval.Id)).State=="Approved",
            "a recovery target with different bytes never completes MCP approval, even when size and timestamp match");
        await library.RevokeAssistantConnectionAsync(connection.Id, actor);
        await refuses(() => library.ExecuteAssistantFilingAsync(filing.Id, caller), "revoking a connection blocks already-approved execution immediately");
        check(File.Exists(Path.Combine(scratch,"Incoming/second.txt")), "revoked approval preserves document bytes and source location");
        await library.SetIntakeAsync(incoming, false);
    }
    private static async Task GuidanceProtocolAsync(Action<bool,string> check)
    {
        var content = JsonSerializer.Serialize(new { reply = "A draft.", guidance = "Keep the closing date.", needs_clarification = false });
        var chatCount = 0; var upstream = false;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/show") return Response(upstream ? "{\"remote_host\":\"https://remote.example\"}" : "{\"capabilities\":[\"completion\"]}");
            chatCount++;
            var body = await request.Content!.ReadAsStringAsync(token);
            check(body.Contains("source_conversation") && body.Contains("current_guidance"), "guidance uses the structured local chat protocol with explicit source and canonical context");
            return Response(JsonSerializer.Serialize(new { done = true, message = new { content } }));
        }));
        var client = new OllamaClient(http);
        var input = new GuidanceDraftRequest("http://127.0.0.1:11434/", "synthetic-guide", 30, "folder", "Existing guide.", "[]");
        check((await client.DraftGuidanceAsync(input)).Guidance == "Keep the closing date.", "the Ollama adapter parses only the structured guidance result");
        content = JsonSerializer.Serialize(new { reply = "Which fallback?", guidance = "Wrong replacement.", needs_clarification = true });
        check((await client.DraftGuidanceAsync(input)).Guidance == input.CurrentGuidance, "clarification responses cannot smuggle a replacement instruction string");
        content = "{\"reply\":\"x\",\"guidance\":\"x\",\"needs_clarification\":false,\"execute\":\"move\"}";
        var refused = false; try { await client.DraftGuidanceAsync(input); } catch (OllamaException) { refused = true; }
        check(refused, "the conversation schema rejects model operation fields");
        upstream = true; var before = chatCount; refused = false;
        try { await client.DraftGuidanceAsync(input); } catch (OllamaException) { refused = true; }
        check(refused && chatCount == before, "cloud-backed guidance models are rejected before the source conversation is sent");
    }
    private static HttpResponseMessage Response(string json) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request, token); }
    private sealed class Guide(Func<GuidanceDraftRequest,CancellationToken,Task<GuidanceDraftResult>> draft) : IOllamaClient
    {
        public Task<List<LocalModel>> ModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(new List<LocalModel>());
        public Task<IdentificationResult> IdentifyAsync(IdentificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GuidanceDraftResult> DraftGuidanceAsync(GuidanceDraftRequest request, CancellationToken cancellationToken = default) => draft(request, cancellationToken);
    }
    private sealed class Clock : TimeProvider
    { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
}
