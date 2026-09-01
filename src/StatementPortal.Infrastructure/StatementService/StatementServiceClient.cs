using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using StatementPortal.Application.Statements;

namespace StatementPortal.Infrastructure.StatementService;

/// <summary>
/// The only class in the solution that talks to the existing Statement
/// Service. Every caller in the application goes through
/// IStatementServiceClient — never directly through HttpClient — so the
/// resilience policy (retry/circuit-breaker/timeout, registered once in DI via
/// AddStandardResilienceHandler) and the RequestId propagation are guaranteed
/// consistent everywhere.
/// </summary>
public sealed class StatementServiceClient : IStatementServiceClient
{
    private readonly HttpClient _httpClient;

    public StatementServiceClient(HttpClient httpClient, IOptions<StatementServiceOptions> options)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri(options.Value.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<StatementResultDto> GenerateSingleAsync(
        Guid requestId, SingleStatementRequestDto request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/statements/single")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("X-Request-Id", requestId.ToString());

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<StatementResultDto>(cancellationToken);
        return result ?? throw new InvalidOperationException("Statement Service returned an empty response.");
    }

    public async Task<BulkStatementResultDto> SubmitBulkAsync(
        Guid requestId, BulkStatementRequestDto request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/statements/bulk")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("X-Request-Id", requestId.ToString());

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<BulkStatementResultDto>(cancellationToken);
        return result ?? throw new InvalidOperationException("Statement Service returned an empty response.");
    }
}
