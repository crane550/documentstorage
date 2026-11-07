using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentStorage.Core;
using DocumentStorage.Data;
using DocumentStorage.Web.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DocumentStorage.McpChecks;
internal static class Runner
{
public static async Task Main()
{
const string connection = "Host=127.0.0.1;Port=55432;Database=documentstorage_mcp_checks;Username=postgres";
const string origin = "https://documents.example.test";
const string resource = origin + "/mcp";
const string redirect = "https://client.example.test/callback";
var password = "Synthetic-only-" + Guid.NewGuid();
var scratch = Path.Combine(Path.GetTempPath(), "documentstorage-mcp-" + Guid.NewGuid()); Directory.CreateDirectory(scratch);
var values = new Dictionary<string,string?> { ["Hosting__PublicOrigin"] = origin, ["Hosting__DataProtectionDirectory"] = Path.Combine(scratch,"keys"), ["BootstrapAdmin__Username"] = "mcp-fixture-admin", ["BootstrapAdmin__Password"] = password, ["ConnectionStrings__Catalog"] = connection, ["DOCUMENTSTORAGE_INSTANCE"] = null, ["DOCUMENTSTORAGE_DB_PASSWORD"] = null };
var saved = values.Keys.ToDictionary(x => x, Environment.GetEnvironmentVariable);
foreach (var item in values) Environment.SetEnvironmentVariable(item.Key, item.Value);
var dbOptions = new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(connection).Options;
int passed = 0;
void Check(bool valid, string label) { if (!valid) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
static string Field(string html)
{
    var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
    if (!match.Success) throw new Exception("Antiforgery field missing."); return WebUtility.HtmlDecode(match.Groups[1].Value);
}
static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
try
{
    await using (var db = new CatalogDb(dbOptions)) await db.Database.EnsureDeletedAsync();
    WebApplicationFactory<global::Program> Factory() => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName,"DocumentStorage.sln"))) root = root.Parent;
        builder.UseContentRoot(Path.Combine(root!.FullName,"src","DocumentStorage.Web")); builder.UseEnvironment("Production");
    });
    await using var app = Factory();
    HttpClient Http(bool cookies = false) => app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(origin), AllowAutoRedirect = false, HandleCookies = cookies });
    using var browser = Http(true); using var anonymous = Http();
    var metadata = await anonymous.GetAsync("/.well-known/oauth-protected-resource/mcp");
    var metadataJson = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
    Check(metadata.IsSuccessStatusCode && metadataJson.RootElement.GetProperty("resource").GetString() == resource, "protected-resource discovery advertises the exact HTTPS MCP audience");
    var discovery = await anonymous.GetAsync("/.well-known/oauth-authorization-server");
    var discoveryJson = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
    Check(discovery.IsSuccessStatusCode && discoveryJson.RootElement.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"S256"}), "OAuth discovery advertises authorization code with S256 PKCE only");
    var missing = await anonymous.PostAsJsonAsync("/mcp", new { jsonrpc="2.0", id=1, method="tools/list", @params=new{} });
    Check(missing.StatusCode == HttpStatusCode.Unauthorized && missing.Headers.WwwAuthenticate.ToString().Contains("resource_metadata="), "anonymous MCP is challenged with discoverable OAuth metadata, never a login-page redirect");
    var loginPage = await browser.GetStringAsync("/login");
    var login = await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string> { ["username"]="mcp-fixture-admin", ["password"]=password, ["returnUrl"]="/", ["__RequestVerificationToken"]=Field(loginPage) }));
    Check(login.StatusCode == HttpStatusCode.Redirect, "the existing application login is used for OAuth consent");
    Check((await browser.PostAsJsonAsync("/mcp", new {jsonrpc="2.0",id=1,method="tools/list"})).StatusCode == HttpStatusCode.Unauthorized, "application cookies alone cannot authenticate an MCP request");
    string owner;
    await using (var scope = app.Services.CreateAsyncScope()) owner = (await scope.ServiceProvider.GetRequiredService<UserManager<LibraryUser>>().FindByNameAsync("mcp-fixture-admin"))!.Id;
    async Task<RegisteredAssistant> Register(string name, string[] permissions, bool publicClient = true)
    { await using var scope = app.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<AssistantConnections>().CreateAsync(name, [redirect], permissions, publicClient, owner); }
    var full = await Register("Full synthetic assistant", LibraryService.AssistantScopes);
    var reader = await Register("Read-only synthetic assistant", ["catalog.read"]);
    var confidential = await Register("Confidential assistant", ["catalog.read"], false);
    Check(confidential.Secret?.Length > 40, "confidential clients receive a random one-time secret");
    await using (var scope=app.Services.CreateAsyncScope())
    {
        var storedSecret = await scope.ServiceProvider.GetRequiredService<CatalogDb>().Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication>().Where(x=>x.ClientId==confidential.Connection.ClientId).Select(x=>x.ClientSecret).SingleAsync();
        Check(!string.IsNullOrEmpty(storedSecret) && storedSecret!=confidential.Secret && !storedSecret.Contains(confidential.Secret ?? ""),"OAuth client secrets are stored as hashes rather than recoverable plaintext");
        var native=await scope.ServiceProvider.GetRequiredService<AssistantConnections>().CreateAsync("Native desktop fixture",["http://127.0.0.1:8912/callback"],["catalog.read"],true,owner);
        Check(native.Secret==null,"public desktop clients support loopback HTTP callbacks with PKCE in production");
    }

    string Authorization(RegisteredAssistant client, string verifier, string? callback = null, string? challenge = null, string? target = null) => QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string,string?>
    { ["client_id"]=client.Connection.ClientId, ["redirect_uri"]=callback ?? redirect, ["response_type"]="code", ["scope"]=string.Join(' ',JsonSerializer.Deserialize<string[]>(client.Connection.ScopesJson)!) + " offline_access", ["resource"]=target ?? resource, ["state"]="synthetic-state", ["code_challenge"]=challenge ?? WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"]="S256" });
    Dictionary<string,string> Consent(string path, string token, string decision)
    {
        var fields = QueryHelpers.ParseQuery(new Uri(new Uri(origin),path).Query).ToDictionary(x => x.Key,x => x.Value.ToString());
        fields["decision"] = decision; fields["__RequestVerificationToken"] = token; return fields;
    }
    async Task<(string Code,string Verifier)> Code(RegisteredAssistant client)
    {
        var verifier = Random(); var path = Authorization(client,verifier);
        var page = await browser.GetAsync(path); if (page.StatusCode != HttpStatusCode.OK) throw new Exception("OAuth consent did not open: " + page.StatusCode + " " + await page.Content.ReadAsStringAsync());
        var response = await browser.PostAsync(path,new FormUrlEncodedContent(Consent(path, Field(await page.Content.ReadAsStringAsync()), "allow")));
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location == null) throw new Exception("OAuth consent did not return a code: " + response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        if (!query.ContainsKey("code")) throw new Exception("OAuth consent response omitted its code.");
        Check(query["state"] == "synthetic-state", "OAuth callback preserves client state"); return (query["code"].ToString(),verifier);
    }
    async Task<HttpResponseMessage> Exchange(RegisteredAssistant client, string code, string verifier, string target = resource)
    {
        var form = new Dictionary<string,string> { ["grant_type"]="authorization_code",["client_id"]=client.Connection.ClientId,["redirect_uri"]=redirect,["code"]=code,["code_verifier"]=verifier,["resource"]=target };
        if (client.Secret != null) form["client_secret"] = client.Secret;
        return await anonymous.PostAsync("/connect/token",new FormUrlEncodedContent(form));
    }
    async Task<JsonElement> Tokens(RegisteredAssistant client)
    {
        var code = await Code(client); var response = await Exchange(client,code.Code,code.Verifier);
        if (!response.IsSuccessStatusCode) throw new Exception("Token exchange failed: " + response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
    }
    using (var cleanBrowser = Http(true))
    {
        var authorizationPath = Authorization(full,Random());
        var loginChallenge = await cleanBrowser.GetAsync(authorizationPath);
        Check(loginChallenge.StatusCode==HttpStatusCode.Redirect && loginChallenge.Headers.Location?.AbsolutePath=="/login", "an unsigned-in OAuth browser is routed to the existing application login");
        var loginHtml = await cleanBrowser.GetStringAsync(loginChallenge.Headers.Location!);
        var returnPath = QueryHelpers.ParseQuery(loginChallenge.Headers.Location!.Query)["returnUrl"].ToString();
        var signedIn = await cleanBrowser.PostAsync("/account/login",new FormUrlEncodedContent(new Dictionary<string,string> { ["username"]="mcp-fixture-admin",["password"]=password,["returnUrl"]=returnPath,["__RequestVerificationToken"]=Field(loginHtml) }));
        Check(signedIn.StatusCode==HttpStatusCode.Redirect && signedIn.Headers.Location!.ToString().StartsWith("/connect/authorize?") && (await cleanBrowser.GetAsync(signedIn.Headers.Location)).StatusCode==HttpStatusCode.OK,
            "application login preserves the encoded OAuth callback, scopes, resource and PKCE request before consent");
    }
    var deniedRedirect = await browser.GetAsync(Authorization(full,Random(),"https://attacker.example.test/callback"));
    Check(deniedRedirect.StatusCode == HttpStatusCode.BadRequest && deniedRedirect.Headers.Location == null, "unregistered callback URLs cannot receive OAuth errors or authorization codes");
    var badResource = await browser.GetAsync(Authorization(full,Random(),target:"https://another-resource.example.test/mcp"));
    Check(badResource.StatusCode == HttpStatusCode.BadRequest || (badResource.StatusCode == HttpStatusCode.Redirect && QueryHelpers.ParseQuery(badResource.Headers.Location!.Query).ContainsKey("error")), "wrong-resource authorization is rejected without an authorization code");
    var consentPath = Authorization(full,Random()); var consentPage = await browser.GetAsync(consentPath);
    if (consentPage.StatusCode != HttpStatusCode.OK) throw new Exception("Consent failure: " + await consentPage.Content.ReadAsStringAsync());
    Check(consentPage.StatusCode == HttpStatusCode.OK && !(await consentPage.Content.ReadAsStringAsync()).Contains("client_secret"), "OAuth shows explicit consent without exposing a client secret");
    Check((await browser.PostAsync(consentPath,new FormUrlEncodedContent(QueryHelpers.ParseQuery(new Uri(new Uri(origin),consentPath).Query).ToDictionary(x => x.Key,x => x.Value.ToString())))).StatusCode == HttpStatusCode.BadRequest, "OAuth consent POST requires antiforgery validation");
    var deny = await browser.PostAsync(consentPath,new FormUrlEncodedContent(Consent(consentPath,Field(await consentPage.Content.ReadAsStringAsync()),"deny")));
    Check(deny.StatusCode == HttpStatusCode.Redirect && QueryHelpers.ParseQuery(deny.Headers.Location!.Query).ContainsKey("error"), "denying consent issues no authorization code");
    var pkce = await Code(full);
    Check(!(await Exchange(full,pkce.Code,Random())).IsSuccessStatusCode, "a stolen authorization code cannot be exchanged with the wrong PKCE verifier");
    var replay = await Code(full); var replayTokens = await Exchange(full,replay.Code,replay.Verifier);
    Check(replayTokens.IsSuccessStatusCode && !(await Exchange(full,replay.Code,replay.Verifier)).IsSuccessStatusCode, "authorization codes are redeemed once and reject replay");
    var wrongTarget = await Code(full);
    Check(!(await Exchange(full,wrongTarget.Code,wrongTarget.Verifier,"https://another-resource.example.test/mcp")).IsSuccessStatusCode, "token exchange cannot redirect the authorization to another resource");
    var confidentialTokens = await Tokens(confidential);
    Check(confidentialTokens.TryGetProperty("access_token",out _), "confidential OAuth clients complete the code flow using their secret");
    var tokens = await Tokens(full); var readerTokens = await Tokens(reader);
    using var authorized = Http(); authorized.DefaultRequestHeaders.Authorization = new("Bearer",tokens.GetProperty("access_token").GetString());
    Check((await authorized.GetAsync("/updates/status")).StatusCode == HttpStatusCode.Redirect, "MCP bearer credentials cannot read browser updater status");
    Check((await authorized.PostAsync("/updates/update",new FormUrlEncodedContent([]))).StatusCode == HttpStatusCode.Redirect, "MCP bearer credentials cannot start deployments");
    using var readHttp = Http(); readHttp.DefaultRequestHeaders.Authorization = new("Bearer",readerTokens.GetProperty("access_token").GetString());
    Check((await authorized.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "MCP bearer credentials cannot authenticate the browser workspace or approval controls");
    Check((await authorized.GetAsync("/documents/00000000-0000-0000-0000-000000000001/preview?v=fixture")).StatusCode == HttpStatusCode.Unauthorized, "MCP bearer credentials cannot authenticate binary browser previews");
    Check((await authorized.GetAsync("/documents/00000000-0000-0000-0000-000000000001/preview-text?v=fixture")).StatusCode == HttpStatusCode.Unauthorized, "MCP bearer credentials cannot authenticate text browser previews");
    await using var transport = new HttpClientTransport(new() { Endpoint=new Uri(resource),TransportMode=HttpTransportMode.StreamableHttp },authorized);
    await using var client = await McpClient.CreateAsync(transport);
    var tools = await client.ListToolsAsync();
    Check(tools.Count >= 15 && tools.Any(x=>x.Name=="execute_filing"), "official MCP client discovers the full scoped tool surface through Streamable HTTP");
    await using var readTransport = new HttpClientTransport(new() { Endpoint=new Uri(resource),TransportMode=HttpTransportMode.StreamableHttp },readHttp);
    await using var readClient = await McpClient.CreateAsync(readTransport);
    var readTools = await readClient.ListToolsAsync();
    Check(!readTools.Any(x=>x.Name is "execute_filing" or "propose_rule" or "propose_instructions" or "read_document_text"), "tool discovery filters operations and OCR according to granted scopes");
    var forbidden = false;
    try { await readClient.CallToolAsync("propose_instructions",new Dictionary<string,object?>()); } catch (McpProtocolException ex) { forbidden = ex.ErrorCode == McpErrorCode.InvalidRequest && ex.Message.Contains("authorization"); }
    Check(forbidden, "calling a hidden write tool by name cannot bypass permissions");
    var types = await client.CallToolAsync("list_document_types", new Dictionary<string,object?> { ["limit"]=2 });
    Check(types.StructuredContent!.Value.GetProperty("data").GetProperty("items").GetArrayLength()==2 && types.StructuredContent!.Value.GetProperty("data").GetProperty("next_offset").GetInt32()==2,"MCP can discover document types omitted from compact context through bounded pagination");
    var contextResult = await client.CallToolAsync("get_context");
    var context = contextResult.StructuredContent!.Value.GetProperty("data"); var hash = context.GetProperty("context_hash").GetString()!;
    Check(contextResult.IsError != true && context.GetProperty("policy").GetString()!.Contains("untrusted"), "MCP context primes assistants with instruction precedence and untrusted-document boundaries");
    var resources = await client.ListResourcesAsync();
    Check(resources.Any(x=>x.Uri=="documentstorage://context"), "canonical workspace context is also available as an MCP resource");
    var prompts = await client.ListPromptsAsync();
    Check(prompts.Any(x => x.Name == "organize_documents"), "MCP advertises a reusable organizing prompt with current workspace context");
    var guidance = await client.CallToolAsync("get_instructions",new Dictionary<string,object?> { ["kind"]="global",["key"]="global" });
    var baseHash = guidance.StructuredContent!.Value.GetProperty("data").GetProperty("hash").GetString();
    var sourceMessage = await client.CallToolAsync("add_instruction_message",new Dictionary<string,object?> { ["kind"]="global",["key"]="global",["text"]="Use only supported document facts.",["idempotency_key"]="official-source-key-001" });
    var sourceId = sourceMessage.StructuredContent!.Value.GetProperty("data").GetProperty("message_id").GetGuid();
    var args = new Dictionary<string,object?> { ["kind"]="global",["key"]="global",["base_hash"]=baseHash,["text"]="Use only supported document facts.",["explanation"]="Synthetic MCP proposal.",["source_messages"]=new[]{sourceId},["context_hash"]=hash,["idempotency_key"]="official-client-key-001" };
    var proposed = await client.CallToolAsync("propose_instructions",args);
    Check(proposed.IsError != true && proposed.StructuredContent!.Value.GetProperty("data").GetProperty("state").GetString()=="Pending", "official MCP tool calls persist pending guidance proposals without publishing");
    var proposalId = proposed.StructuredContent!.Value.GetProperty("data").GetProperty("id").GetGuid();
    var again = await client.CallToolAsync("propose_instructions",args);
    Check(again.StructuredContent!.Value.GetProperty("data").GetProperty("id").GetGuid()==proposalId, "network retries return the same durable proposal through the public MCP transport");
    var cross = await readClient.CallToolAsync("get_proposal",new Dictionary<string,object?> { ["proposal_id"]=proposalId });
    Check(cross.IsError==true, "cross-connection proposal access is rejected through MCP");
    var staleArgs = new Dictionary<string,object?>(args) { ["context_hash"]="stale",["idempotency_key"]="stale-official-key" };
    var staleResult = await client.CallToolAsync("propose_instructions",staleArgs);
    Check(staleResult.IsError==true && staleResult.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString()=="stale_context", "stale context is reported as a stable structured tool error");
    await app.Services.GetRequiredService<LibraryService>().DecideAssistantProposalAsync(proposalId, proposed.StructuredContent!.Value.GetProperty("data").GetProperty("revision").GetInt32(), true, owner);
    var history = await client.CallToolAsync("list_instruction_history",new Dictionary<string,object?> { ["kind"]="global",["key"]="global" });
    var versionId = history.StructuredContent!.Value.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetGuid();
    var version = await client.CallToolAsync("read_instruction_revision",new Dictionary<string,object?> { ["revision_id"]=versionId });
    Check(version.StructuredContent!.Value.GetProperty("data").GetProperty("source_messages").EnumerateArray().Any(x=>x.GetGuid()==sourceId),"paginated MCP instruction history preserves links back to the accepted source request");
    var originalMessage = await client.CallToolAsync("get_instruction_message",new Dictionary<string,object?> { ["message_id"]=sourceId });
    Check(originalMessage.StructuredContent!.Value.GetProperty("data").GetProperty("text").GetString()=="Use only supported document facts.","a historical source message can be retrieved completely through MCP");
    // Sorting: kinds, details and tidy-up are readable with catalog access; proposals need the sorting permission.
    var kinds = await client.CallToolAsync("list_kinds");
    Check(kinds.IsError != true && kinds.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "Bank statement" && x.GetProperty("folder_pattern").GetString()!.Contains("{from}")),
        "MCP lists the kinds Stash files by, with their folder and name patterns");
    Check(readTools.Any(x => x.Name == "list_kinds") && !readTools.Any(x => x.Name is "propose_document_details" or "propose_kind" or "propose_refile"), "sorting proposals are hidden without the sorting permission; reading kinds is not");
    var found = await client.CallToolAsync("find_documents", new Dictionary<string,object?> { ["kind"]="Invoice" });
    Check(found.IsError != true && found.StructuredContent!.Value.GetProperty("data").TryGetProperty("total", out _), "documents can be found by their details through MCP");
    var noFilter = await readClient.CallToolAsync("find_documents", new Dictionary<string,object?> { ["words"]="statement" });
    Check(noFilter.IsError == true && noFilter.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "documents_read_required", "searching document words needs the document-read permission");
    var sortingHash = (await client.CallToolAsync("get_context")).StructuredContent!.Value.GetProperty("data").GetProperty("context_hash").GetString();
    var badKind = await client.CallToolAsync("propose_kind", new Dictionary<string,object?> { ["kind_id"]=null, ["name"]="Escape", ["description"]="", ["folder_pattern"]="../{from}", ["name_pattern"]="{date}", ["fields"]=Array.Empty<string>(), ["source_request"]="synthetic", ["context_hash"]=sortingHash, ["idempotency_key"]="sorting-kind-escape-01" });
    Check(badKind.IsError == true, "a proposed pattern that leaves storage is refused through MCP");
    var newKind = await client.CallToolAsync("propose_kind", new Dictionary<string,object?> { ["kind_id"]=null, ["name"]="Recipe", ["description"]="Cooking recipes.", ["folder_pattern"]="Recipes", ["name_pattern"]="{title}", ["fields"]=Array.Empty<string>(), ["source_request"]="synthetic", ["context_hash"]=sortingHash, ["idempotency_key"]="sorting-kind-recipe-01" });
    Check(newKind.IsError != true && newKind.StructuredContent!.Value.GetProperty("data").GetProperty("state").GetString() == "Pending"
        && !(await app.Services.GetRequiredService<LibraryService>().SortVocabularyAsync()).Kinds.Any(x => x.Name == "Recipe"), "a kind proposal waits for the person and changes nothing yet");
    Check(kinds.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Bank statement")
        .GetProperty("field_guides").GetProperty("account_ending").GetProperty("type").GetString() == "id", "MCP lists how each of a kind's fields is found");
    var strayGuide = await client.CallToolAsync("propose_kind", new Dictionary<string,object?> { ["kind_id"]=null, ["name"]="Synthetic lease", ["description"]="Leases.", ["folder_pattern"]="Leases", ["name_pattern"]="{title}", ["fields"]=new[] { "rent" },
        ["field_guides"]=new Dictionary<string,object> { ["deposit"]=new { type="money", look_for="The deposit.", labels=new[] { "Deposit" } } }, ["source_request"]="synthetic", ["context_hash"]=sortingHash, ["idempotency_key"]="sorting-kind-lease-stray-01" });
    Check(strayGuide.IsError == true, "a guide for a field the kind doesn't carry is refused");
    var guided = await client.CallToolAsync("propose_kind", new Dictionary<string,object?> { ["kind_id"]=null, ["name"]="Synthetic lease", ["description"]="Leases.", ["folder_pattern"]="Leases", ["name_pattern"]="{title}", ["fields"]=new[] { "rent" },
        ["field_guides"]=new Dictionary<string,object> { ["rent"]=new { type="money", look_for="The monthly rent.", labels=new[] { "Monthly rent", "Rent" } } }, ["source_request"]="synthetic", ["context_hash"]=sortingHash, ["idempotency_key"]="sorting-kind-lease-01" });
    var guidedData = guided.StructuredContent!.Value.GetProperty("data");
    await app.Services.GetRequiredService<LibraryService>().DecideAssistantProposalAsync(guidedData.GetProperty("id").GetGuid(), guidedData.GetProperty("revision").GetInt32(), true, owner);
    var lease = (await app.Services.GetRequiredService<LibraryService>().SortVocabularyAsync()).Kinds.Single(x => x.Name == "Synthetic lease");
    Check(guided.IsError != true && LibraryService.FieldGuides(lease)["rent"] is { Type: "money", Look: "The monthly rent." } rent && rent.Labels.SequenceEqual(["Monthly rent", "Rent"]),
        "an accepted kind proposal saves how each field is found");
    using (var request = new HttpRequestMessage(HttpMethod.Post,"/mcp"))
    {
        request.Headers.Add("Origin","https://attacker.example.test"); request.Content=JsonContent.Create(new {jsonrpc="2.0",id=1,method="tools/list"});
        Check((await authorized.SendAsync(request)).StatusCode==HttpStatusCode.Forbidden,"untrusted browser origins are rejected before MCP processing");
    }
    Check((await authorized.PostAsync("/mcp?access_token=not-accepted",new StringContent("{}"))).StatusCode==HttpStatusCode.BadRequest,"bearer credentials in URL query strings are refused");
    using (var large = new StringContent(new string('x',262145))) Check((await authorized.PostAsync("/mcp",large)).StatusCode==HttpStatusCode.RequestEntityTooLarge,"oversized MCP requests are bounded before parsing");
    // Verify the wire route for older initialize-based clients as well as the current official client.
    using (var legacy = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
    {
        legacy.Headers.Add("Accept", "application/json, text/event-stream"); legacy.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        legacy.Content = JsonContent.Create(new { jsonrpc = "2.0", id = 10, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "Synthetic legacy client", version = "1" } } });
        var legacyResponse = await authorized.SendAsync(legacy);
        Check(legacyResponse.IsSuccessStatusCode && (await legacyResponse.Content.ReadAsStringAsync()).Contains("2025-11-25") && !legacyResponse.Headers.Contains("Mcp-Session-Id"), "initialize-based 2025 clients are supported without creating an unauthenticated transport session");
    }
    var library = app.Services.GetRequiredService<LibraryService>();
    var storage = Path.Combine(scratch,"storage"); Directory.CreateDirectory(storage);
    var storageId = await library.AddRootAsync("HTTP synthetic archive",storage,false);
    var rootFolder = (await library.OverviewAsync()).Folders.Single(x=>x.RootId==storageId && x.ParentId==null);
    var intakeId = await library.CreateFolderAsync(rootFolder.Id,"Incoming"); var filedId = await library.CreateFolderAsync(rootFolder.Id,"Filed");
    await library.SetIntakeAsync(intakeId,true);
    const string synthetic = "Synthetic HTTP fixture. Date 2026-10-31. Elm statement. Reference P-27.";
    await library.UploadAsync(intakeId,"scan.txt",new MemoryStream(Encoding.UTF8.GetBytes(synthetic)));
    await Task.Delay(5500); await library.ScanAsync();
    var document = (await library.BrowseAsync(intakeId)).Items.Single();
    Check(tools.Any(x=>x.Name=="list_inbox")&&tools.Any(x=>x.Name=="update_kind_training")&&!readTools.Any(x=>x.Name is "list_inbox" or "inspect_document" or "update_document_details" or "teach_reader_example"),"Inbox diagnostics and coaching writes are scoped separately from catalog metadata");
    var inboxPage=await client.CallToolAsync("list_inbox",new Dictionary<string,object?>{["limit"]=5});
    Check(inboxPage.IsError!=true&&inboxPage.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray().Any(x=>x.GetProperty("document_id").GetGuid()==document.Id)&&!inboxPage.StructuredContent.Value.GetRawText().Contains(storage),"MCP exposes pending Inbox decisions without physical storage paths");
    var releaseContext=await client.CallToolAsync("get_context");
    Check(releaseContext.StructuredContent!.Value.GetProperty("data").GetProperty("server_release").GetProperty("version").GetString()==DocumentStorage.Web.AppVersion.Display,"MCP reports the actual running version and commit for development diagnostics");
    var inspected=await client.CallToolAsync("inspect_document",new Dictionary<string,object?>{["document_id"]=document.Id});
    Check(inspected.IsError!=true&&inspected.StructuredContent!.Value.GetProperty("data").GetProperty("details_revision").GetInt32()==0,"inspection exposes the exact details revision needed for a correction");
    var attempt=new IdentificationJob{EntryId=document.Id,SourcePath=document.RelativePath,ContentHash=document.ContentHash,Model="synthetic-reader",Sorting=true,State="Succeeded",InputText=synthetic,ContextJson="{\"known_kinds\":[\"Invoice\"],\"text\":\"Synthetic evidence\"}",OrganizationJson="{\"kind\":\"Invoice\"}",CreatedAt=DateTimeOffset.UtcNow};
    await using(var db=await app.Services.GetRequiredService<IDbContextFactory<CatalogDb>>().CreateDbContextAsync()){db.IdentificationJobs.Add(attempt);await db.SaveChangesAsync();}
    var attempts=await client.CallToolAsync("list_reader_attempts",new Dictionary<string,object?>{["document_id"]=document.Id});
    Check(attempts.IsError!=true&&attempts.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray().Any(x=>x.GetProperty("id").GetGuid()==attempt.Id&&x.GetProperty("current_source").GetBoolean()),"MCP lists saved model attempts without rerunning them");
    var inputSlice=await client.CallToolAsync("read_reader_attempt",new Dictionary<string,object?>{["attempt_id"]=attempt.Id,["section"]="input",["length"]=5});
    Check(inputSlice.IsError!=true&&inputSlice.StructuredContent!.Value.GetProperty("data").GetProperty("text").GetString()==synthetic[..5]&&inputSlice.StructuredContent.Value.GetProperty("data").GetProperty("untrusted").GetBoolean(),"exact model input is paginated and marked untrusted");
    var badSlice=await client.CallToolAsync("read_reader_attempt",new Dictionary<string,object?>{["attempt_id"]=attempt.Id,["offset"]=-1});Check(badSlice.IsError==true,"invalid diagnostic ranges return structured errors");
    async Task<string> FreshHash()=> (await client.CallToolAsync("get_context")).StructuredContent!.Value.GetProperty("data").GetProperty("context_hash").GetString()!;
    var directKind=await client.CallToolAsync("update_kind_training",new Dictionary<string,object?>{["kind_id"]=null,["expected_revision"]=0,["name"]="Synthetic safety policy",["description"]="Ministry training and policy.",["folder_pattern"]="Church/Safety Ministry",["name_pattern"]="{title}",["fields"]=new[]{"reference"},["field_guides"]=new Dictionary<string,object>{["reference"]=new{type="id",look_for="The printed reference.",labels=new[]{"Reference"}}},["context_hash"]=await FreshHash(),["idempotency_key"]="http-training-kind-01",["source_request"]="Set up this synthetic policy kind.",["apply_now"]=true});
    Check(directKind.IsError!=true&&directKind.StructuredContent!.Value.GetProperty("data").GetProperty("applied").GetBoolean(),"explicit go-ahead applies kind training without a browser approval");
    var fieldTest=await client.CallToolAsync("test_kind_fields",new Dictionary<string,object?>{["document_id"]=document.Id,["kind_id"]=directKind.StructuredContent!.Value.GetProperty("data").GetProperty("target_id").GetGuid()});
    Check(fieldTest.IsError!=true&&!fieldTest.StructuredContent!.Value.GetProperty("data").GetProperty("asked_reader").GetBoolean()&&fieldTest.StructuredContent.Value.GetProperty("data").GetProperty("fields").GetProperty("reference").GetString()!.StartsWith("P-27"),"field teaching can be tested deterministically without spending a model call");
    var detailArgs=new Dictionary<string,object?>{["document_id"]=document.Id,["expected_revision"]=0,["content_hash"]=document.ContentHash,["kind"]="Synthetic safety policy",["from"]="Elm Ministry",["date"]="2026-10-31",["title"]="Safety procedures",["tags"]=new[]{"training","policy"},["fields"]=new Dictionary<string,string>{["reference"]="P-27"},["context_hash"]=await FreshHash(),["idempotency_key"]="http-details-edit-01",["source_request"]="Correct this synthetic document.",["apply_now"]=true};
    var corrected=await client.CallToolAsync("update_document_details",detailArgs);
    Check(corrected.IsError!=true&&corrected.StructuredContent!.Value.GetProperty("data").GetProperty("applied").GetBoolean()&&File.Exists(Path.Combine(storage,"Incoming/scan.txt")),"strong assistant corrections save metadata without moving files");
    var correctedAgain=await client.CallToolAsync("update_document_details",detailArgs);
    var editId=corrected.StructuredContent!.Value.GetProperty("data").GetProperty("receipt_id").GetGuid();
    Check(correctedAgain.IsError!=true&&correctedAgain.StructuredContent!.Value.GetProperty("data").GetProperty("receipt_id").GetGuid()==editId,"edit retries over the official protocol reuse the same receipt");
    var receipt=await client.CallToolAsync("get_assistant_edit",new Dictionary<string,object?>{["receipt_id"]=editId});
    Check(receipt.IsError!=true&&receipt.StructuredContent!.Value.GetProperty("data").GetProperty("source_request").GetString()=="Correct this synthetic document.","the stronger AI can inspect the applied decision and its original request");
    detailArgs["context_hash"]=await FreshHash();detailArgs["idempotency_key"]="http-details-edit-stale";
    var staleCorrection=await client.CallToolAsync("update_document_details",detailArgs);Check(staleCorrection.IsError==true,"stale detail revisions are rejected through MCP");
    var teaching=await client.CallToolAsync("teach_reader_example",new Dictionary<string,object?>{["document_id"]=document.Id,["expected_revision"]=1,["content_hash"]=document.ContentHash,["lesson"]="Training and policy may both apply.",["context_hash"]=await FreshHash(),["idempotency_key"]="http-teaching-example-01",["source_request"]="Keep this synthetic correction as an example.",["apply_now"]=true});
    Check(teaching.IsError!=true&&teaching.StructuredContent!.Value.GetProperty("data").GetProperty("applied").GetBoolean(),"a corrected document can teach future local-reader requests over MCP");
    var examples=await client.CallToolAsync("list_reader_examples");
    Check(examples.IsError!=true&&examples.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray().Any(x=>x.GetProperty("active").GetBoolean()),"teaching examples expose whether their source and revision remain current");
    var forgotten=await client.CallToolAsync("forget_reader_example",new Dictionary<string,object?>{["example_id"]=teaching.StructuredContent!.Value.GetProperty("data").GetProperty("target_id").GetGuid(),["expected_revision"]=1,["context_hash"]=await FreshHash(),["idempotency_key"]="http-forget-example-01",["source_request"]="Stop using this synthetic example.",["apply_now"]=true});Check(forgotten.IsError!=true,"teaching influence can be removed while its history is retained");
    var pendingCorrection=await client.CallToolAsync("update_tag",new Dictionary<string,object?>{["tag_id"]=null,["expected_revision"]=0,["name"]="ministry-notes",["aliases"]=Array.Empty<string>(),["context_hash"]=await FreshHash(),["idempotency_key"]="http-pending-tag-01",["source_request"]="Review this synthetic tag first."});
    Check(pendingCorrection.IsError!=true&&pendingCorrection.StructuredContent!.Value.GetProperty("data").GetProperty("state").GetString()=="Pending","the connection's review-first default queues rather than applying a change");
    await library.DecideAssistantEditAsync(pendingCorrection.StructuredContent!.Value.GetProperty("data").GetProperty("receipt_id").GetGuid(),owner,true);
    await library.DecideAssistantEditAsync(editId,owner,true,true);
    var verifiedReceipt=await client.CallToolAsync("get_assistant_edit",new Dictionary<string,object?>{["receipt_id"]=editId});
    Check(verifiedReceipt.StructuredContent!.Value.GetProperty("data").GetProperty("state").GetString()=="Verified","human verification is visible to the assistant after the edit is already applied");
    await library.SaveReviewAsync(document.Id,0,"mcp_http_statement",new(){["date"]="2026-10-31"},true,owner);
    await library.SaveRuleAsync(new(null,0,"HTTP synthetic filing","mcp_http_statement",filedId,new(),"{date:yyyy-MM-dd}.{extension}",["date"],true),owner);
    var filingContext = await client.CallToolAsync("get_context");
    var filingRequest = await client.CallToolAsync("request_filing",new Dictionary<string,object?> { ["document_id"]=document.Id,["context_hash"]=filingContext.StructuredContent!.Value.GetProperty("data").GetProperty("context_hash").GetString(),["idempotency_key"]="http-filing-001" });
    Check(filingRequest.IsError!=true,"MCP can request an exact preview for a human-reviewed document");
    var filingId = filingRequest.StructuredContent!.Value.GetProperty("data").GetProperty("id").GetGuid();
    var premature = await client.CallToolAsync("execute_filing",new Dictionary<string,object?> { ["proposal_id"]=filingId });
    Check(premature.IsError==true && File.Exists(Path.Combine(storage,"Incoming/scan.txt")),"an MCP client cannot execute its own unapproved filing request");
    await library.DecideAssistantProposalAsync(filingId,filingRequest.StructuredContent!.Value.GetProperty("data").GetProperty("revision").GetInt32(),true,owner);
    var executed = await client.CallToolAsync("execute_filing",new Dictionary<string,object?> { ["proposal_id"]=filingId });
    Check(executed.IsError!=true && await File.ReadAllTextAsync(Path.Combine(storage,"Filed/2026-10-31.txt"))==synthetic,"approved execution through the real MCP transport preserves original bytes and files through the shared journal");
    var executedAgain = await client.CallToolAsync("execute_filing",new Dictionary<string,object?> { ["proposal_id"]=filingId });
    Check(executedAgain.StructuredContent!.Value.GetProperty("data").GetProperty("operation_id").GetGuid()==executed.StructuredContent!.Value.GetProperty("data").GetProperty("operation_id").GetGuid(),"MCP execution retries return the same completed operation");
    var textPage = await client.CallToolAsync("read_document_text",new Dictionary<string,object?> { ["document_id"]=document.Id,["length"]=10 });
    Check(textPage.StructuredContent!.Value.GetProperty("data").GetProperty("untrusted").GetBoolean() && textPage.StructuredContent!.Value.GetProperty("data").GetProperty("next_offset").GetInt32()==10,"document text reads are paginated and explicitly marked as untrusted");
    var reviewed = await client.CallToolAsync("get_document_review",new Dictionary<string,object?> { ["document_id"]=document.Id });
    Check(reviewed.StructuredContent!.Value.GetProperty("data").GetProperty("review").GetProperty("reviewed_by_human").GetBoolean(),"MCP exposes structured reviewed facts while keeping approval controls outside the tool surface");
    async Task<HttpResponseMessage> Refresh(RegisteredAssistant registered, string refresh) => await anonymous.PostAsync("/connect/token",new FormUrlEncodedContent(new Dictionary<string,string> { ["client_id"]=registered.Connection.ClientId,["grant_type"]="refresh_token",["refresh_token"]=refresh,["resource"]=resource }));
    var refreshResponse = await Refresh(full,tokens.GetProperty("refresh_token").GetString()!);
    Check(refreshResponse.IsSuccessStatusCode,"OAuth issues a usable rotating refresh token for offline access");
    Check(!(await Refresh(full,tokens.GetProperty("refresh_token").GetString()!)).IsSuccessStatusCode,"replayed refresh tokens are rejected with no reuse grace window");
    await app.Services.GetRequiredService<LibraryService>().RevokeAssistantConnectionAsync(reader.Connection.Id,owner);
    var revoked = await readHttp.PostAsJsonAsync("/mcp",new {jsonrpc="2.0",id=1,method="tools/list"});
    Check(revoked.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,"revoking a connection blocks an existing access token on the next HTTP request");
    var fresh = await Tokens(full);
    using var freshHttp = Http(); freshHttp.DefaultRequestHeaders.Authorization=new("Bearer",fresh.GetProperty("access_token").GetString());
    await using (var scope=app.Services.CreateAsyncScope()) {var users=scope.ServiceProvider.GetRequiredService<UserManager<LibraryUser>>();await users.UpdateSecurityStampAsync((await users.FindByIdAsync(owner))!);}
    Check((await freshHttp.PostAsJsonAsync("/mcp",new {jsonrpc="2.0",id=1,method="tools/list"})).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,"account security-stamp changes invalidate existing MCP access tokens");
    Check(!(await Refresh(full,fresh.GetProperty("refresh_token").GetString()!)).IsSuccessStatusCode,"account security-stamp changes also prevent token refresh");
    Check(Directory.GetFiles(Path.Combine(scratch,"keys","oauth"),"*.key").Length==2,"separate signing and encryption keys persist under the protected instance key directory");
    HttpResponseMessage? limited = null;
    for (var i=0;i<95;i++)
    {
        var response = await anonymous.PostAsJsonAsync("/mcp",new {jsonrpc="2.0",id=i,method="tools/list"});
        if (response.StatusCode==HttpStatusCode.TooManyRequests) { limited=response;break; }
        response.Dispose();
    }
    Check(limited?.Headers.RetryAfter?.Delta==TimeSpan.FromSeconds(60),"MCP request rate limiting returns 429 with a retry delay"); limited?.Dispose();
    // Reauthenticate after the stamp change, then prove a new host can use the persisted grant and keys.
    var reloginPage = await browser.GetStringAsync("/login");
    await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string,string> { ["username"]="mcp-fixture-admin", ["password"]=password, ["returnUrl"]="/", ["__RequestVerificationToken"]=Field(reloginPage) }));
    var restartTokens = await Tokens(full);
    var keyTimes = Directory.GetFiles(Path.Combine(scratch,"keys","oauth")).ToDictionary(x=>x,File.GetLastWriteTimeUtc);
    await app.DisposeAsync();
    await using var restarted = Factory();
    using var restartedHttp = restarted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new Uri(origin),AllowAutoRedirect=false,HandleCookies=false });
    restartedHttp.DefaultRequestHeaders.Authorization=new("Bearer",restartTokens.GetProperty("access_token").GetString());
    restartedHttp.DefaultRequestHeaders.Add("Accept","application/json, text/event-stream"); restartedHttp.DefaultRequestHeaders.Add("MCP-Protocol-Version","2025-11-25");
    var afterRestart = await restartedHttp.PostAsJsonAsync("/mcp",new {jsonrpc="2.0",id=1,method="tools/list"});
    Check(afterRestart.IsSuccessStatusCode && keyTimes.All(x=>File.GetLastWriteTimeUtc(x.Key)==x.Value),"a fresh application host accepts an existing access token using the same persisted signing/encryption keys");
    var refreshedAfterRestart = await restartedHttp.PostAsync("/connect/token",new FormUrlEncodedContent(new Dictionary<string,string> { ["client_id"]=full.Connection.ClientId,["grant_type"]="refresh_token",["refresh_token"]=restartTokens.GetProperty("refresh_token").GetString()!,["resource"]=resource }));
    Check(refreshedAfterRestart.IsSuccessStatusCode,"refresh grants survive a real application host restart");
    Console.WriteLine($"All {passed} OAuth and official-SDK MCP checks passed.");
}
finally
{
    await using var db = new CatalogDb(dbOptions); await db.Database.EnsureDeletedAsync();
    foreach(var item in saved) Environment.SetEnvironmentVariable(item.Key,item.Value);
    Directory.Delete(scratch,true);
}

}
}
