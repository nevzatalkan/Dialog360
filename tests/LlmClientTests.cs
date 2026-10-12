using System.Net;
using System.Text.Json;
using Azure.Core;
using Diyalog360.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diyalog360.Tests;

public sealed class LlmClientTests
{
    [Fact]
    public async Task Foundry_agent_uses_responses_endpoint_and_parses_structured_output()
    {
        const string endpoint = "https://example.services.ai.azure.com/api/projects/demo/agents/d360-sql-agent/endpoint/protocols/openai/responses";
        const string answer = """{"tur":"rapor","baslik":"Toplam","sql":"SELECT 1","aciklama":"Test","varsayimlar":[],"netlestirme_sorusu":"","secenekler":[]}""";
        HttpRequestMessage? sentRequest = null;
        string? requestBody = null;
        var handler = new TestHandler(async (request, cancellationToken) =>
        {
            sentRequest = request;
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { output_text = answer })),
            };
        });
        using var http = new HttpClient(handler);
        var client = new LlmClient(
            http,
            new LlmOptions { Provider = "FoundryAgent", AgentEndpoint = endpoint },
            new TestTokenCredential(),
            NullLogger<LlmClient>.Instance);

        var result = await client.AskAsync(
            [new ChatMessage("system", "Kurallar"), new ChatMessage("user", "Raporla")],
            CancellationToken.None);

        Assert.Equal("SELECT 1", result.Answer.Sql);
        Assert.Equal(answer, result.Raw);
        Assert.Equal("Bearer", sentRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", sentRequest.Headers.Authorization.Parameter);
        Assert.Equal(new Uri(endpoint), sentRequest.RequestUri);

        using var body = JsonDocument.Parse(requestBody!);
        Assert.Equal("system", body.RootElement.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal("Kurallar", body.RootElement.GetProperty("input")[0].GetProperty("content").GetString());
        Assert.Equal("json_schema", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.True(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
    }

    sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    sealed class TestTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
