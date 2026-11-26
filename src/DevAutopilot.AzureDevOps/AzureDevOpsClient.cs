using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DevAutopilot.AzureDevOps;

/// <summary>
/// Thin typed wrapper over the Azure DevOps REST API (api-version 7.1). Every request carries the token
/// from <see cref="IAzureDevOpsTokenProvider"/> in the Authorization header only. Requests are retried
/// on 429 and 5xx (honouring Retry-After), never on other errors.
/// </summary>
public sealed class AzureDevOpsClient
{
    public const string ApiVersion = "7.1";

    /// <summary>The comments endpoint exists only as a preview in 7.1.</summary>
    public const string CommentsApiVersion = "7.1-preview.4";

    public const int MaxAttempts = 4;

    /// <summary>The batch endpoint takes at most 200 ids.</summary>
    public const int BatchSize = 200;

    public const int DefaultTop = 50;

    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(60);
    private const int MaxErrorTextLength = 300;
    private const int MaxPolicyPages = 20;

    private readonly HttpClient _http;
    private readonly IAzureDevOpsTokenProvider _tokens;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _organization;
    private readonly string _project;

    public AzureDevOpsClient(
        HttpClient http,
        Uri organizationUrl,
        string project,
        IAzureDevOpsTokenProvider tokens,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(tokens);
        _http = http;
        _tokens = tokens;
        _delay = delay ?? Task.Delay;
        _organization = organizationUrl.AbsoluteUri.TrimEnd('/');
        _project = project;
        OrganizationUrl = new Uri(_organization);
    }

    public Uri OrganizationUrl { get; }

    public string Project => _project;

    public string TokenSource => _tokens.Describe();

    /// <summary>Checks that a token can be obtained (without sending it anywhere).</summary>
    public async Task EnsureTokenAsync(CancellationToken ct) =>
        _ = await _tokens.GetCredentialAsync(ct).ConfigureAwait(false);

    /// <summary>Gets the credential for uses outside HTTP (git push). The caller must not store it.</summary>
    internal Task<AzureDevOpsCredential> GetCredentialAsync(CancellationToken ct) => _tokens.GetCredentialAsync(ct);

    // ---------- Work items ----------

    /// <summary>Runs a flat WIQL query and returns the work item ids in the query's order.</summary>
    public async Task<IReadOnlyList<int>> QueryWorkItemIdsAsync(string wiql, int top, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wiql);
        var url = ProjectUrl("_apis/wit/wiql", ("$top", top.ToString(CultureInfo.InvariantCulture)));
        using var doc = await SendJsonAsync("run the work item query", HttpMethod.Post, url, Json(new { query = wiql }), ct).ConfigureAwait(false);
        var ids = new List<int>();
        if (doc.RootElement.TryGetProperty("workItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    /// <summary>Gets work items with the module's fields, in the order of <paramref name="ids"/>. Missing items are left out.</summary>
    public async Task<IReadOnlyList<AdoWorkItem>> GetWorkItemsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var found = new Dictionary<int, AdoWorkItem>();
        foreach (var chunk in ids.Distinct().Chunk(BatchSize))
        {
            var url = ProjectUrl("_apis/wit/workitemsbatch");
            var body = Json(new { ids = chunk, fields = WorkItemFields.All, errorPolicy = "omit" });
            using var doc = await SendJsonAsync("read work items", HttpMethod.Post, url, body, ct).ConfigureAwait(false);
            foreach (var element in Items(doc.RootElement))
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
                {
                    continue;
                }

                var rev = element.TryGetProperty("rev", out var revElement) && revElement.TryGetInt32(out var r) ? r : 0;
                var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (element.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in fieldsElement.EnumerateObject())
                    {
                        fields[field.Name] = field.Value.Clone();
                    }
                }

                found[id] = new AdoWorkItem(id, rev, fields);
            }
        }

        return ids.Distinct().Where(found.ContainsKey).Select(id => found[id]).ToList();
    }

    /// <summary>
    /// Sets <c>System.Tags</c> with a JSON Patch document. The patch first tests the revision, so a change
    /// made by someone else in between fails instead of being overwritten.
    /// </summary>
    public async Task UpdateTagsAsync(int workItemId, int expectedRev, string tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var url = ProjectUrl($"_apis/wit/workitems/{workItemId.ToString(CultureInfo.InvariantCulture)}");
        object[] patch =
        [
            new { op = "test", path = "/rev", value = expectedRev },
            new { op = "add", path = "/fields/" + WorkItemFields.Tags, value = tags },
        ];
        var content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");
        using var _ = await SendJsonAsync($"update the tags of work item {workItemId}", HttpMethod.Patch, url, content, ct).ConfigureAwait(false);
    }

    /// <summary>Adds a comment (HTML text) to a work item.</summary>
    public async Task AddCommentAsync(int workItemId, string htmlText, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlText);
        var url = ProjectUrl(
            $"_apis/wit/workItems/{workItemId.ToString(CultureInfo.InvariantCulture)}/comments",
            apiVersion: CommentsApiVersion);
        using var _ = await SendJsonAsync($"add a comment to work item {workItemId}", HttpMethod.Post, url, Json(new { text = htmlText }), ct).ConfigureAwait(false);
    }

    // ---------- Project and repository ----------

    public async Task<AdoProject> GetProjectAsync(CancellationToken ct)
    {
        var url = $"{_organization}/_apis/projects/{Uri.EscapeDataString(_project)}?api-version={ApiVersion}";
        using var doc = await SendJsonAsync($"read project \"{_project}\"", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return new AdoProject(String(root, "id") ?? string.Empty, String(root, "name") ?? _project);
    }

    public async Task<AdoRepository> GetRepositoryAsync(string repository, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var url = ProjectUrl($"_apis/git/repositories/{Uri.EscapeDataString(repository)}");
        using var doc = await SendJsonAsync($"read repository \"{repository}\"", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var projectId = root.TryGetProperty("project", out var project) ? String(project, "id") : null;
        var webUrl = String(root, "webUrl");
        if (webUrl is null && root.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var web))
        {
            webUrl = String(web, "href");
        }

        return new AdoRepository(
            String(root, "id") ?? string.Empty,
            String(root, "name") ?? repository,
            projectId ?? string.Empty,
            webUrl,
            String(root, "remoteUrl"),
            String(root, "defaultBranch"));
    }

    // ---------- Pull requests ----------

    /// <summary>
    /// Creates a draft pull request linked to one work item. The body never sets auto-complete,
    /// reviewers' votes or completion options.
    /// </summary>
    public async Task<AdoPullRequest> CreateDraftPullRequestAsync(string repositoryId, PullRequestCreate request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(request);
        var url = ProjectUrl($"_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullrequests");
        var body = Json(new
        {
            sourceRefName = "refs/heads/" + request.SourceBranch,
            targetRefName = "refs/heads/" + request.TargetBranch,
            title = request.Title,
            description = request.Description,
            isDraft = true,
            workItemRefs = new[] { new { id = request.WorkItemId } },
        });
        using var doc = await SendJsonAsync("create the draft pull request", HttpMethod.Post, url, body, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var id = root.TryGetProperty("pullRequestId", out var idElement) && idElement.TryGetInt32(out var value) ? value : 0;
        string? webUrl = null;
        if (root.TryGetProperty("repository", out var repo))
        {
            var repoWeb = String(repo, "webUrl");
            if (repoWeb is not null && id > 0)
            {
                webUrl = repoWeb.TrimEnd('/') + "/pullrequest/" + id.ToString(CultureInfo.InvariantCulture);
            }
        }

        return new AdoPullRequest(id, webUrl);
    }

    // ---------- Policies and permissions ----------

    /// <summary>Policy configurations that apply to <paramref name="refName"/> in a repository, optionally of one type.</summary>
    public async Task<IReadOnlyList<AdoPolicyConfiguration>> GetBranchPoliciesAsync(
        string repositoryId, string refName, string? policyType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refName);
        var result = new List<AdoPolicyConfiguration>();
        string? continuation = null;
        for (var page = 0; page < MaxPolicyPages; page++)
        {
            var query = new List<(string, string)> { ("repositoryId", repositoryId), ("refName", refName) };
            if (policyType is not null)
            {
                query.Add(("policyType", policyType));
            }

            if (continuation is not null)
            {
                query.Add(("continuationToken", continuation));
            }

            var url = ProjectUrl("_apis/git/policy/configurations", [.. query]);
            var response = await SendAsync($"read the branch policies of {refName}", () => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            using (var doc = Parse(response.Body))
            {
                foreach (var element in Items(doc.RootElement))
                {
                    result.Add(ReadPolicy(element));
                }
            }

            continuation = response.ContinuationToken;
            if (string.IsNullOrEmpty(continuation))
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the caller (the identity behind the token) has <paramref name="permissionBit"/> on
    /// <paramref name="securityToken"/>. Administrators are not treated as always allowed.
    /// </summary>
    public async Task<bool> HasPermissionAsync(string namespaceId, int permissionBit, string securityToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityToken);
        var url = $"{_organization}/_apis/permissions/{Uri.EscapeDataString(namespaceId)}/{permissionBit.ToString(CultureInfo.InvariantCulture)}"
            + $"?tokens={Uri.EscapeDataString(securityToken)}&alwaysAllowAdministrators=false&api-version={ApiVersion}";
        using var doc = await SendJsonAsync("check the agent identity's permissions", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var values = Items(doc.RootElement).ToList();
        if (values.Count == 0 || values[0].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new AzureDevOpsException("Azure DevOps returned an unexpected answer to the permission check.", null);
        }

        return values[0].GetBoolean();
    }

    // ---------- Transport ----------

    internal sealed record Response(HttpStatusCode Status, string Body, string? ContinuationToken);

    private async Task<JsonDocument> SendJsonAsync(string operation, HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        // The content is read into memory once so each retry can send a fresh copy.
        byte[]? bytes = null;
        MediaTypeHeaderValue? contentType = null;
        if (content is not null)
        {
            using (content)
            {
                bytes = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                contentType = content.Headers.ContentType;
            }
        }

        var response = await SendAsync(operation, () =>
        {
            var request = new HttpRequestMessage(method, url);
            if (bytes is not null)
            {
                var body = new ByteArrayContent(bytes);
                body.Headers.ContentType = contentType;
                request.Content = body;
            }

            return request;
        }, ct).ConfigureAwait(false);
        return Parse(response.Body);
    }

    internal async Task<Response> SendAsync(string operation, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var credential = await _tokens.GetCredentialAsync(ct).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            using var request = createRequest();
            request.Headers.Authorization = credential.ToAuthorizationHeader();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new AzureDevOpsException(
                    $"Could not reach Azure DevOps at {_organization} to {operation}: {credential.Redact(ex.Message)} Check the network and \"organizationUrl\".",
                    null);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AzureDevOpsException($"Azure DevOps did not answer in time while trying to {operation}. Try again later.", null);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = response.StatusCode;
                if (response.IsSuccessStatusCode && LooksLikeSignInPage(response))
                {
                    // An invalid token can get a sign-in page with a success code instead of a 401.
                    status = HttpStatusCode.Unauthorized;
                }

                if ((int)status is >= 200 and < 300)
                {
                    var continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
                    return new Response(status, body, continuation);
                }

                if (IsRetryable(status) && attempt < MaxAttempts)
                {
                    await _delay(RetryDelay(response, attempt), ct).ConfigureAwait(false);
                    continue;
                }

                throw new AzureDevOpsException(ErrorMessage(operation, status, credential.Redact(ServerMessage(body))), status);
            }
        }
    }

    internal static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    internal static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? wait = retryAfter?.Delta;
        if (wait is null && retryAfter?.Date is { } date)
        {
            wait = date - DateTimeOffset.UtcNow;
        }

        wait ??= TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        return wait > MaxRetryWait ? MaxRetryWait : wait.Value;
    }

    private string ErrorMessage(string operation, HttpStatusCode status, string serverMessage)
    {
        var said = string.IsNullOrWhiteSpace(serverMessage) ? string.Empty : $" Azure DevOps said: {serverMessage}";
        return status switch
        {
            HttpStatusCode.Unauthorized =>
                $"Azure DevOps did not accept the token from {_tokens.Describe()} while trying to {operation} (401 Unauthorized). " +
                $"Make sure it is a valid, unexpired token of the agent identity for {_organization}; if it expired, create a new one and update the variable, or run 'az login' when \"auth\" is \"az\".{said}",
            HttpStatusCode.Forbidden =>
                $"The agent identity is not allowed to {operation} (403 Forbidden). Give the token the scopes Work Items (read & write) and Code (read & write), " +
                $"and give the identity the matching permission in project \"{_project}\" (see docs/AZURE-DEVOPS.md).{said}",
            HttpStatusCode.NotFound =>
                $"Azure DevOps could not find what was needed to {operation} (404 Not Found). Check \"organizationUrl\", \"project\" and \"repository\" in .autopilot/autopilot.json, " +
                $"and that the agent identity is a member of project \"{_project}\".{said}",
            HttpStatusCode.Conflict =>
                $"Azure DevOps refused to {operation} because of a conflict (409); for a pull request this usually means one is already open for this branch.{said}",
            HttpStatusCode.TooManyRequests =>
                $"Azure DevOps kept limiting the request rate while trying to {operation} (429 Too Many Requests) after {MaxAttempts} attempts. Wait a few minutes and try again.{said}",
            _ when (int)status >= 500 =>
                $"Azure DevOps had a server error while trying to {operation} ({(int)status}) after {MaxAttempts} attempts. Try again later.{said}",
            _ => $"Azure DevOps refused to {operation} ({(int)status} {status}).{said}",
        };
    }

    private static bool LooksLikeSignInPage(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.NonAuthoritativeInformation
        || response.Content.Headers.ContentType?.MediaType is "text/html";

    private static string ServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && String(doc.RootElement, "message") is { } message)
            {
                return message.Length > MaxErrorTextLength ? message[..MaxErrorTextLength] + "..." : message;
            }
        }
        catch (JsonException)
        {
            // Not JSON (for example an HTML page); not shown.
        }

        return string.Empty;
    }

    private string ProjectUrl(string path, params (string Name, string Value)[] query) =>
        ProjectUrl(path, ApiVersion, query);

    private string ProjectUrl(string path, string apiVersion, params (string Name, string Value)[] query)
    {
        var text = new StringBuilder();
        text.Append(_organization).Append('/').Append(Uri.EscapeDataString(_project)).Append('/').Append(path).Append('?');
        foreach (var (name, value) in query)
        {
            text.Append(name).Append('=').Append(Uri.EscapeDataString(value)).Append('&');
        }

        text.Append("api-version=").Append(apiVersion);
        return text.ToString();
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static JsonDocument Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw new AzureDevOpsException("Azure DevOps returned a response that is not JSON. Check \"organizationUrl\".", null);
        }
    }

    /// <summary>A list response is either a bare array or <c>{ "count": n, "value": [...] }</c>.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray();
        }

        return [];
    }

    private static AdoPolicyConfiguration ReadPolicy(JsonElement element)
    {
        var typeId = string.Empty;
        string? typeName = null;
        if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Object)
        {
            typeId = String(type, "id") ?? string.Empty;
            typeName = String(type, "displayName");
        }

        return new AdoPolicyConfiguration(
            element.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) ? value : 0,
            typeId,
            typeName,
            Bool(element, "isEnabled"),
            Bool(element, "isBlocking"),
            Bool(element, "isDeleted"),
            element.TryGetProperty("settings", out var settings) ? settings.Clone() : default);
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>An Azure DevOps request failed. The message says what to fix and never contains the token.</summary>
public sealed class AzureDevOpsException(string message, HttpStatusCode? statusCode) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
