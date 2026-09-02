using System.Net.Http.Headers;

namespace StatementPortal.Infrastructure.StatementService;

/// <summary>
/// Sits in front of every request the portal sends to the Statement Service
/// (registered via AddHttpMessageHandler on that HttpClient — see
/// DependencyInjection.cs). This is what actually makes the login
/// "have to be called before single/bulk statement access": there is no
/// path from StatementServiceClient to the Statement Service that skips this
/// handler, so a valid token is guaranteed to be attached before the request
/// ever leaves the process. Neither StatementsController nor
/// StatementRequestService needs to know this login exists at all.
/// </summary>
public sealed class StatementServiceAuthHandler : DelegatingHandler
{
    private readonly IStatementServiceAuthenticator _authenticator;

    public StatementServiceAuthHandler(IStatementServiceAuthenticator authenticator) =>
        _authenticator = authenticator;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _authenticator.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
