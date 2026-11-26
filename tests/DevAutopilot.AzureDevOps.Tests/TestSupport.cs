using System.Net;
using System.Text;
using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Gate;

namespace DevAutopilot.AzureDevOps.Tests;

/// <summary>One request the fake handler saw, with its body read into a string.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string Body);

/// <summary>
/// Answers requests from a list of routes (method + URL prefix, in order of registration). A route can
/// answer several times or be used once. Every request is recorded. No network is touched.
/// </summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Prefix, Queue<Func<HttpResponseMessage>> Responses, Func<HttpResponseMessage>? Repeat)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Always answers matching requests with this JSON.</summary>
    public FakeHandler On(HttpMethod method, string urlPrefix, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((method, urlPrefix, new Queue<Func<HttpResponseMessage>>(), () => JsonResponse(json, status)));
        return this;
    }

    /// <summary>Answers matching requests with these responses in order; the last one repeats.</summary>
    public FakeHandler OnSequence(HttpMethod method, string urlPrefix, params Func<HttpResponseMessage>[] responses)
    {
        var queue = new Queue<Func<HttpResponseMessage>>(responses);
        _routes.Add((method, urlPrefix, queue, responses[^1]));
        return this;
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body));

        var url = request.RequestUri!.AbsoluteUri;
        foreach (var (method, prefix, responses, repeat) in _routes)
        {
            if (method == request.Method && url.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (responses.Count > 0)
                {
                    return responses.Dequeue()();
                }

                if (repeat is not null)
                {
                    return repeat();
                }
            }
        }

        return JsonResponse("{\"message\":\"no fake route for " + request.Method + " " + url + "\"}", HttpStatusCode.NotFound);
    }
}

public sealed class FixedTokenProvider(string token, bool bearer = false) : IAzureDevOpsTokenProvider
{
    public Task<AzureDevOpsCredential> GetCredentialAsync(CancellationToken ct) =>
        Task.FromResult(bearer ? AzureDevOpsCredential.FromBearer(token) : AzureDevOpsCredential.FromPat(token));

    public string Describe() => "test token";
}

public sealed class RecordingPusher : IGitPusher
{
    public List<(string Path, string Remote, string Branch, AzureDevOpsCredential Credential)> Pushes { get; } = [];

    public Exception? Throw { get; set; }

    public Task PushAsync(string repositoryPath, string remote, string branch, string branchPrefix, AzureDevOpsCredential credential, CancellationToken ct)
    {
        GitPusher.EnsureAgentBranch(branch, branchPrefix);
        if (Throw is not null)
        {
            throw Throw;
        }

        Pushes.Add((repositoryPath, remote, branch, credential));
        return Task.CompletedTask;
    }
}

public static class Ado
{
    public const string Org = "https://dev.azure.com/contoso";
    public const string Project = "Shop";
    public const string Repo = "shop-api";
    public const string ProjectId = "6ce954b1-ce1f-45d1-b94d-e6bf2464ba2c";
    public const string RepoId = "5febef5a-833d-4e14-b9c0-14cb638f91e6";

    /// <summary>A dummy token that is easy to search for in messages.</summary>
    public const string Token = "dummytoken1234567890abcdefDUMMY";

    public static string TokenBase64 => Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Token));

    public static AzureDevOpsSettings Settings(string? repository = Repo) => new()
    {
        OrganizationUrl = new Uri(Org),
        Project = Project,
        Repository = repository,
    };

    public static AzureDevOpsClient Client(FakeHandler handler, List<TimeSpan>? delays = null, IAzureDevOpsTokenProvider? tokens = null) =>
        new(new HttpClient(handler), new Uri(Org), Project, tokens ?? new FixedTokenProvider(Token), (d, _) =>
        {
            delays?.Add(d);
            return Task.CompletedTask;
        });

    public static ModuleConfig Module(params (string Key, string Value)[] settings) =>
        new() { Type = "azure-devops", Settings = settings.ToDictionary(s => s.Key, s => s.Value) };

    public static string RepositoryJson => $$"""
        {
          "id": "{{RepoId}}",
          "name": "{{Repo}}",
          "url": "{{Org}}/_apis/git/repositories/{{RepoId}}",
          "project": {
            "id": "{{ProjectId}}",
            "name": "{{Project}}",
            "url": "{{Org}}/_apis/projects/{{ProjectId}}",
            "state": "wellFormed",
            "revision": 293012730
          },
          "defaultBranch": "refs/heads/main",
          "remoteUrl": "{{Org}}/{{Project}}/_git/{{Repo}}",
          "_links": {
            "web": { "href": "{{Org}}/{{Project}}/_git/{{Repo}}" }
          }
        }
        """;

    public static string ProjectJson => $$"""
        {
          "id": "{{ProjectId}}",
          "name": "{{Project}}",
          "description": "Made-up project for tests",
          "url": "{{Org}}/_apis/projects/{{ProjectId}}",
          "state": "wellFormed",
          "revision": 411518573,
          "visibility": "private"
        }
        """;

    public static string WiqlJson(params int[] ids) =>
        "{\"queryType\":\"flat\",\"asOf\":\"2026-11-25T10:00:00.000Z\",\"columns\":[{\"referenceName\":\"System.Id\",\"name\":\"ID\",\"url\":\"" + Org + "/_apis/wit/fields/System.Id\"}],\"workItems\":["
        + string.Join(",", ids.Select(id => $"{{\"id\":{id},\"url\":\"{Org}/_apis/wit/workItems/{id}\"}}"))
        + "]}";

    public static PublishRequest Request(string branch = "autopilot/42", GateOutcome outcome = GateOutcome.Passed)
    {
        var item = new WorkItem("42", "Return 404 for unknown products", "The product endpoint returns 500 for an unknown id.", ["autopilot"]);
        var triage = new TriageResult(TriageLabel.AgentReady, "Small, clear change.", ["GET /products/999 returns 404"], RiskLevel.Low);
        var evidence = new Evidence("42", triage, new SelfReview("Return NotFound when the product is missing.", ["ProductsTests.Unknown_id_returns_404"], []), new ReviewResult(ReviewVerdict.Approve, []));
        return new PublishRequest(item, branch, "diff --git a/x b/x", new GateDecision(outcome, []), evidence);
    }
}
