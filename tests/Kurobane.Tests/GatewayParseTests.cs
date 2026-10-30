using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Kurobane.Core;
using Xunit;

namespace Kurobane.Tests;

public class GatewayWritesTests
{
    [Fact]
    public void Cron_add_uses_a_string_schedule_and_one_job_kind()
    {
        var agent = GatewayWrites.CronAdd("kogarashi", "0 9 * * *", "Morning", "Check the garden", null);
        Assert.Equal("kogarashi", agent["agent"]!.GetValue<string>());
        Assert.Equal("0 9 * * *", agent["schedule"]!.GetValue<string>());
        Assert.Equal("agent", agent["job_type"]!.GetValue<string>());
        Assert.Equal("Check the garden", agent["prompt"]!.GetValue<string>());
        Assert.Null(agent["command"]);

        var shell = GatewayWrites.CronAdd("kogarashi", "*/15 * * * *", null, null, "echo ok");
        Assert.Equal("shell", shell["job_type"]!.GetValue<string>());
        Assert.Equal("echo ok", shell["command"]!.GetValue<string>());
        Assert.False(shell.ContainsKey("name"));
    }

    [Fact]
    public void Personality_names_stay_on_the_gateway_allowlist()
    {
        Assert.True(GatewayWrites.PersonalityFilenameAllowed("SOUL.md"));
        Assert.False(GatewayWrites.PersonalityFilenameAllowed("../SOUL.md"));
        Assert.False(GatewayWrites.PersonalityFilenameAllowed("soul.md"));
        var body = GatewayWrites.PersonalityPut("hello", 12);
        Assert.Equal("hello", body["content"]!.GetValue<string>());
        Assert.Equal(12, body["expected_mtime_ms"]!.GetValue<long>());
    }
}

public class GatewayParseTests
{
    private static JsonNode J(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Agents_parse_enabled_and_ignore_unknown_fields()
    {
        var agents = AgentsApi.Parse(J("""
            {"agents":{
              "kogarashi":{"enabled":true,"model_provider":"mistral.kogarashi","risk_profile":"kogarashi","extra":1},
              "other":{"enabled":false,"model_provider":"openai.codex","channels":["cli"]}
            }}
            """));
        Assert.Equal(2, agents.Count);
        Assert.Equal("kogarashi", agents[0].Alias);
        Assert.True(agents[0].Enabled);
        Assert.Equal("mistral.kogarashi", agents[0].ModelProvider);
        Assert.False(agents[1].Enabled);
        Assert.Equal("cli", agents[1].Channels[0]);
    }

    [Fact]
    public void Invalid_saved_alias_switches_to_kogarashi_not_the_first_agent()
    {
        var agents = new[]
        {
            new AgentInfo("alpha", true, "p", Array.Empty<string>()),
            new AgentInfo("kogarashi", true, "mistral.kogarashi", Array.Empty<string>()),
            new AgentInfo("zeta", false, "p", Array.Empty<string>())
        };
        var decided = AgentAlias.Decide("default", agents);
        Assert.Equal("kogarashi", decided.Alias);
        Assert.True(decided.SwitchLocalToKogarashi);
        Assert.False(decided.NeedsUserChoice);
        Assert.Contains("kogarashi", decided.StatusLine);
        Assert.Contains("not changed", decided.StatusLine);

        var kept = AgentAlias.Decide("alpha", agents);
        Assert.Equal("alpha", kept.Alias);
        Assert.False(kept.SwitchLocalToKogarashi);
    }

    [Fact]
    public void Without_kogarashi_an_invalid_alias_is_not_replaced_by_the_first_agent()
    {
        var agents = new[]
        {
            new AgentInfo("alpha", true, "p", Array.Empty<string>()),
            new AgentInfo("beta", true, "p", Array.Empty<string>())
        };
        var decided = AgentAlias.Decide("missing", agents);
        Assert.Equal("missing", decided.Alias);
        Assert.False(decided.SwitchLocalToKogarashi);
        Assert.True(decided.NeedsUserChoice);
        Assert.Contains("will not pick", decided.StatusLine);
    }

    [Fact]
    public void Provider_model_and_fallback_from_snapshot_and_prop()
    {
        var snapshot = J("""
            {"agents":{"kogarashi":{"model_provider":"mistral.kogarashi"}},
             "providers":{"models":{"mistral":{"kogarashi":{
               "model":"zai-glm-5-3-flash","fallback_models":["mistral-large-latest"],"api_key":"ignored"}}}}}
            """);
        var parsed = GatewayParse.ParseProviderForAgent(snapshot, "kogarashi");
        Assert.Equal("mistral.kogarashi", parsed.ProviderId);
        Assert.Equal("zai-glm-5-3-flash", parsed.Model);
        Assert.Equal("mistral-large-latest", Assert.Single(parsed.FallbackModels));

        var prop = GatewayParse.ParseProvider(J("""
            {"path":"providers.models.mistral.kogarashi","value":{"model":"zai-glm-5-3-flash","fallback_models":["mistral-large-latest"]}}
            """), "mistral.kogarashi");
        Assert.Equal("zai-glm-5-3-flash", prop.Model);
        Assert.Equal("mistral-large-latest", prop.FallbackModels[0]);
    }

    [Fact]
    public void Process_model_stays_separate_from_the_agent_model()
    {
        var status = GatewayParse.ParseStatus(J("""
            {"version":"0.8.7","model_provider":"openai.codex","model":"gpt-6.1-sol","uptime_secs":3}
            """));
        var line = GatewayText.FormatModels("mistral.kogarashi", "zai-glm-5-3-flash",
            new[] { "mistral-large-latest" }, status.Provider, status.Model);
        Assert.Contains("This agent: mistral.kogarashi / zai-glm-5-3-flash", line);
        Assert.Contains("Gateway process model: openai.codex / gpt-6.1-sol", line);
        Assert.DoesNotContain("This agent: openai.codex", line);
    }

    [Fact]
    public void Risk_profile_flat_and_nested()
    {
        var flat = GatewayParse.ParseRisk(J("""
            {"agents":{"kogarashi":{"risk_profile":"kogarashi"}},
             "risk_profiles":{"kogarashi":{"level":"supervised","workspace_only":true,
               "allowed_commands":["*"],"always_ask":["shell","file_write"],"auto_approve":["file_read"]}}}
            """), "kogarashi");
        Assert.Equal("supervised", flat.Level);
        Assert.True(flat.WorkspaceOnly);
        Assert.Contains("shell", flat.AlwaysAsk);
        Assert.Contains("file_read", flat.AutoApprove);

        var nested = GatewayParse.ParseRisk(J("""
            {"agents":{"kogarashi":{"risk_profile":"supervised.kogarashi"}},
             "risk_profiles":{"supervised":{"kogarashi":{"level":"supervised","always_ask":["shell"],"auto_approve":["calculator"]}}}}
            """), "kogarashi");
        Assert.Equal("supervised", nested.Level);
        Assert.Equal("shell", Assert.Single(nested.AlwaysAsk));
        Assert.Contains("always_ask", GatewayText.AlwaysButtonCaption(nested.AlwaysAsk));
        Assert.Contains("shell", GatewayText.AlwaysButtonCaption(nested.AlwaysAsk));
        Assert.Contains("does not disable supervision", GatewayText.AlwaysButtonCaption(null));
    }

    [Fact]
    public void Cost_shows_tokens_and_does_not_invent_dollars()
    {
        var unpriced = GatewayParse.ParseCost(J("""
            {"cost_usd":0,"unpriced_tokens":42,"total_tokens":42,"input_tokens":40,"output_tokens":2}
            """));
        Assert.Equal(0, unpriced.CostUsd);
        Assert.Equal(42, unpriced.UnpricedTokens);
        Assert.Contains("42 tokens", unpriced.UsageLine);
        Assert.Contains("did not price", unpriced.UsageLine);
        Assert.DoesNotContain("$", unpriced.UsageLine);

        var nested = GatewayParse.ParseCost(J("""
            {"cost":{"session_cost_usd":1.5,"total_tokens":10,"request_count":2,"by_model":{}}}
            """));
        Assert.Equal(1.5, nested.CostUsd);
        Assert.Contains("$1.5", nested.UsageLine);

        var byModel = GatewayParse.ParseCost(J("""
            {"cost":{"daily_cost_usd":0,"total_tokens":30,"request_count":2,"by_model":{
              "one":{"cost_usd":0,"unpriced_tokens":12,"input_tokens":10,"output_tokens":2},
              "two":{"cost_usd":0,"unpriced_tokens":18,"input_tokens":15,"output_tokens":3}
            }}}
            """));
        Assert.Equal(0, byModel.CostUsd);
        Assert.Equal(30, byModel.UnpricedTokens);
        Assert.Equal(25, byModel.InputTokens);
        Assert.Equal(5, byModel.OutputTokens);
        Assert.Contains("did not price", byModel.UsageLine);
        Assert.DoesNotContain("$", byModel.UsageLine);

        var tokensOnly = GatewayParse.ParseCost(J("{\"total_tokens\":1000}"));
        Assert.False(tokensOnly.HasCostUsd);
        Assert.DoesNotContain("$", tokensOnly.UsageLine);
        Assert.Contains("1000 tokens", tokensOnly.UsageLine);
    }

    [Fact]
    public void Cron_tolerates_empty_and_two_shapes()
    {
        Assert.Empty(GatewayParse.ParseCronJobs(J("{\"jobs\":[]}")));
        Assert.Empty(GatewayParse.ParseCronJobs(J("{}")));

        var shaped = GatewayParse.ParseCronJobs(J("""
            {"jobs":[{"id":"morning","name":"Morning","schedule":{"kind":"cron","expr":"0 9 * * *"},
              "next_run":"2026-10-31T09:00:00Z","enabled":true,"prompt":"hi","extra":false}]}
            """));
        Assert.Equal("morning", shaped[0].Id);
        Assert.Equal("0 9 * * *", shaped[0].Schedule);
        Assert.True(shaped[0].Enabled);

        var other = GatewayParse.ParseCronJobs(J("""
            [{"id":"echo","command":"echo hi","schedule":"every 1h","last_status":"ok"}]
            """));
        Assert.Equal("echo", other[0].Id);
        Assert.Equal("every 1h", other[0].Schedule);
        Assert.Equal("ok", other[0].LastStatus);
        Assert.Equal("echo hi", other[0].Command);
    }

    [Fact]
    public void Memory_entries_keep_id_category_timestamp_content()
    {
        var rows = GatewayParse.ParseMemory(J("""
            {"entries":[
              {"id":"e1","key":"k","category":"core","timestamp":"2026-10-01T00:00:00Z","content":"hello","score":0.2},
              {"key":"e2","category":{"custom":"health"},"created_at":"t","content":"note"}
            ]}
            """));
        Assert.Equal("e1", rows[0].Id);
        Assert.Equal("core", rows[0].Category);
        Assert.Equal("hello", rows[0].Content);
        Assert.Equal("e2", rows[1].Id);
        Assert.Equal("health", rows[1].Category);
        Assert.Equal("t", rows[1].Timestamp);
        var longText = new string('x', 500);
        Assert.EndsWith("…", GatewayParse.TruncateContent(longText));
        Assert.Equal(401, GatewayParse.TruncateContent(longText).Length);
    }

    [Fact]
    public void Skill_names_from_agent_list_and_bundles()
    {
        var names = GatewayParse.ParseSkillNames(J("""
            {"skills":["release-check",{"name":"review","description":"x"}],
             "bundles":[{"alias":"ops","skills":["ship"]}]}
            """));
        Assert.Contains("release-check", names);
        Assert.Contains("review", names);
        Assert.Contains("ship", names);

        var map = GatewayParse.ParseSkillNames(J("""{"bundles":{"ops":["alpha"]}}"""));
        Assert.Contains("alpha", map);
    }

    [Fact]
    public void Upload_response_personality_names_and_devices_drop_secrets()
    {
        var upload = GatewayParse.ParseUpload(J("""{"path":"webui_uploads/a.png","marker":"[IMAGE:webui_uploads/a.png]","bytes":12}"""));
        Assert.Equal("[IMAGE:webui_uploads/a.png]", upload!.Marker);
        Assert.Equal("[IMAGE:webui_uploads/b.png]", GatewayParse.ParseUpload(J("{\"path\":\"webui_uploads/b.png\"}"))!.Marker);

        var names = GatewayParse.ParsePersonalityNames(J("""
            {"files":[{"name":"SOUL.md","body":"SECRET BODY TEXT"},{"filename":"USER.md"}]}
            """));
        Assert.Equal(new[] { "SOUL.md", "USER.md" }, names);
        Assert.DoesNotContain("SECRET BODY TEXT", string.Join(" ", names));

        var fromKeys = GatewayParse.ParsePersonalityNames(J("""{"SOUL.md":"# do not keep this body","agent":"kogarashi"}"""));
        Assert.Equal("SOUL.md", Assert.Single(fromKeys));

        var devices = GatewayParse.ParseDevices(J("""
            {"devices":[{"name":"Kurobane on Windows","device_type":"windows-desktop","token":"should-not-appear"}]}
            """));
        Assert.Equal("Kurobane on Windows", devices[0].Name);
        Assert.Equal("windows-desktop", devices[0].Type);
    }

    [Fact]
    public void Plugins_and_tools_parse()
    {
        var plugins = GatewayParse.ParsePlugins(J("""{"plugins_enabled":false,"plugins":[]}"""));
        Assert.False(plugins.PluginsEnabled);
        Assert.Empty(plugins.Names);
        var tools = GatewayParse.ParseTools(J("""{"tools":[{"name":"shell","description":"run"},"memory_recall"]}"""));
        Assert.Equal("shell", tools[0].Name);
        Assert.Equal("run", tools[0].Description);
        Assert.Equal("memory_recall", tools[1].Name);
    }

    [Fact]
    public void Image_sniff_rejects_non_images()
    {
        Assert.True(GatewayParse.TryPrepareImage(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }, out var png, out _));
        Assert.Equal("image/png", png);
        Assert.False(GatewayParse.TryPrepareImage("hello"u8.ToArray(), out _, out var why));
        Assert.Contains("not uploaded", why);
        Assert.Equal("image/webp", GatewayParse.Mime("RIFF0000WEBP"u8));
    }

    [Fact]
    public async Task Upload_posts_raw_bytes_not_json()
    {
        byte[]? seen = null;
        string? type = null;
        var handler = new Stub(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"path\":\"p.png\",\"marker\":\"[IMAGE:p.png]\"}", Encoding.UTF8, "application/json")
            };
        }, req =>
        {
            type = req.Content!.Headers.ContentType!.MediaType;
            seen = req.Content.ReadAsByteArrayAsync().Result;
            Assert.Equal("/api/upload", req.RequestUri!.AbsolutePath);
            Assert.Equal("kogarashi", req.RequestUri.Query.Contains("agent=kogarashi") ? "kogarashi" : req.RequestUri.Query);
        });
        using var client = new ZeroClawClient(new Uri("http://gw.local/"), handler);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var result = await GatewayParse.UploadImageAsync(client, "kogarashi", png, default);
        Assert.Equal("[IMAGE:p.png]", result.Marker);
        Assert.Equal(png, seen);
        Assert.Equal("image/png", type);
    }

    [Fact]
    public void Errors_are_plain_and_a_dropped_turn_is_not_retried()
    {
        var denied = new ZeroClawApiException(HttpStatusCode.Unauthorized, "{\"error\":\"no\"}");
        Assert.Contains("Pairing expired", GatewayText.Explain(denied));
        Assert.Contains("Gateway unreachable", GatewayText.Explain(new HttpRequestException("no route")));
        Assert.Contains("Provider failed", GatewayText.ExplainFrame("provider_error", "model overloaded"));
        Assert.Contains("Pairing expired", GatewayText.ExplainFrame("401", "nope"));
        Assert.Contains("did not send the message again", GatewayText.UnsettledTurn(false));
        Assert.Null(GatewayText.UnsettledTurn(true));
        Assert.Equal("Cancelled", GatewayText.Explain(new OperationCanceledException()));
    }

    [Fact]
    public void Drafts_and_labels_roundtrip_per_server_and_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kurobane-tests-" + Guid.NewGuid().ToString("N"));
        var store = new DeviceLocalStore(dir);
        store.SetDraft("server:1", DeviceLocalStore.NewChatSessionKey, "draft");
        store.SetDraft("server:1", "sess", "keep");
        store.SetLabel("server:1", "sess", "Book");
        var again = new DeviceLocalStore(dir);
        Assert.Equal("draft", again.GetDraft("server:1", DeviceLocalStore.NewChatSessionKey));
        Assert.Equal("keep", again.GetDraft("server:1", "sess"));
        Assert.Equal("Book", again.GetLabel("server:1", "sess"));
        Assert.Null(again.GetLabel("server:1", "other"));
        again.SetDraft("server:1", "sess", "");
        Assert.Equal("", new DeviceLocalStore(dir).GetDraft("server:1", "sess"));
        Assert.True(File.Exists(Path.Combine(dir, "drafts.json")));
        Assert.True(File.Exists(Path.Combine(dir, "project-labels.json")));
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect?.Invoke(request);
            return Task.FromResult(respond(request));
        }
    }
}
