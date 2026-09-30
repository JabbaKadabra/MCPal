using System.Net;
using MCPal.Bridge.Local;

namespace MCPal.Bridge.Tests.Local;

[TestFixture]
internal sealed class UserTokenHeaderHandlerTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpClient ClientFor(UserTokenScope scope, string header, Recorder recorder) =>
        new(new UserTokenHeaderHandler(scope, header) { InnerHandler = recorder });

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Send_InsideScopeWithAuthorizationHeader_SendsBearerToken()
    {
        var scope = new UserTokenScope();
        var recorder = new Recorder();
        using var client = ClientFor(scope, "Authorization", recorder);

        using (scope.Enter("jwt.value"))
        {
            (await client.GetAsync(new Uri("http://local/mcp"), Ct)).Dispose();
        }

        recorder.Requests.Single().Headers.Authorization?.ToString().Should().Be("Bearer jwt.value");
    }

    [Test]
    public async Task Send_InsideScopeWithCustomHeader_SendsRawToken()
    {
        var scope = new UserTokenScope();
        var recorder = new Recorder();
        using var client = ClientFor(scope, "X-MCPal-User", recorder);

        using (scope.Enter("jwt.value"))
        {
            (await client.GetAsync(new Uri("http://local/mcp"), Ct)).Dispose();
        }

        recorder.Requests.Single().Headers.GetValues("X-MCPal-User").Should().Equal("jwt.value");
    }

    [Test]
    public async Task Send_OutsideScope_SendsNoHeader()
    {
        var scope = new UserTokenScope();
        var recorder = new Recorder();
        using var client = ClientFor(scope, "Authorization", recorder);

        (await client.GetAsync(new Uri("http://local/mcp"), Ct)).Dispose();
        using (scope.Enter("jwt.value"))
        {
        }

        (await client.GetAsync(new Uri("http://local/mcp"), Ct)).Dispose();

        recorder.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Headers.Authorization == null);
    }

    [Test]
    public async Task Send_HeaderAlreadyOnTheRequestOutsideScope_IsRemoved()
    {
        var scope = new UserTokenScope();
        var recorder = new Recorder();
        using var client = ClientFor(scope, "Authorization", recorder);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://local/mcp"));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer stale-token-of-another-user");

        (await client.SendAsync(request, Ct)).Dispose();

        recorder.Requests.Single().Headers.Authorization.Should().BeNull();
    }

    [Test]
    public async Task Send_TwoParallelScopes_EachRequestCarriesItsOwnToken()
    {
        var scope = new UserTokenScope();
        var recorder = new Recorder();
        using var client = ClientFor(scope, "Authorization", recorder);
        var gate = new TaskCompletionSource();

        async Task CallAsync(string token)
        {
            using (scope.Enter(token))
            {
                await gate.Task;
                (await client.GetAsync(new Uri("http://local/mcp?" + token), Ct)).Dispose();
            }
        }

        var calls = new[] { CallAsync("anna"), CallAsync("ben") };
        gate.SetResult();
        await Task.WhenAll(calls);

        recorder.Requests.ToDictionary(r => r.RequestUri?.Query ?? string.Empty, r => r.Headers.Authorization?.ToString())
            .Should().BeEquivalentTo(new Dictionary<string, string?> { ["?anna"] = "Bearer anna", ["?ben"] = "Bearer ben" });
    }

    [Test]
    public void Enter_NestedScopes_RestoreThePreviousToken()
    {
        var scope = new UserTokenScope();

        using (scope.Enter("outer"))
        {
            using (scope.Enter("inner"))
            {
                scope.Current.Should().Be("inner");
            }

            scope.Current.Should().Be("outer");
        }

        scope.Current.Should().BeNull();
    }
}
