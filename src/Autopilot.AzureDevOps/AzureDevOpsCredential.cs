using System.Net.Http.Headers;
using System.Text;
using Autopilot.Core.Process;

namespace Autopilot.AzureDevOps;

/// <summary>
/// A token in memory only. It never appears in <see cref="ToString"/>; use <see cref="Redact"/> on any
/// text that might echo it before showing that text.
/// </summary>
public sealed class AzureDevOpsCredential
{
    private readonly string _token;
    private readonly string _headerValue;

    private AzureDevOpsCredential(string token, bool bearer)
    {
        _token = token;
        IsBearer = bearer;
        _headerValue = bearer
            ? token
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token));
    }

    public bool IsBearer { get; }

    /// <summary>A personal access token, sent as Basic with an empty user name.</summary>
    public static AzureDevOpsCredential FromPat(string token) => new(Validate(token), bearer: false);

    /// <summary>A Microsoft Entra access token (for example from the Azure CLI), sent as Bearer.</summary>
    public static AzureDevOpsCredential FromBearer(string token) => new(Validate(token), bearer: true);

    public AuthenticationHeaderValue ToAuthorizationHeader() =>
        new(IsBearer ? "Bearer" : "Basic", _headerValue);

    /// <summary>The value for git's <c>http.extraHeader</c>: <c>Authorization: Basic ...</c> or <c>Authorization: Bearer ...</c>.</summary>
    public string ToGitExtraHeader() => $"Authorization: {(IsBearer ? "Bearer" : "Basic")} {_headerValue}";

    /// <summary>Replaces the token and its encoded form in <paramref name="text"/>.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return text
            .Replace(_token, "***", StringComparison.Ordinal)
            .Replace(_headerValue, "***", StringComparison.Ordinal);
    }

    public override string ToString() => IsBearer ? "Bearer ***" : "Basic ***";

    private static string Validate(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var trimmed = token.Trim();
        if (trimmed.Length == 0)
        {
            throw new AzureDevOpsAuthException("The token is empty.");
        }

        if (trimmed.Any(c => char.IsControl(c) || c == ' '))
        {
            throw new AzureDevOpsAuthException("The token contains spaces or control characters. Copy it again without extra characters.");
        }

        return trimmed;
    }
}

/// <summary>Gets the token for each use. Implementations never cache it on disk.</summary>
public interface IAzureDevOpsTokenProvider
{
    Task<AzureDevOpsCredential> GetCredentialAsync(CancellationToken ct);

    /// <summary>Where the token comes from, for messages. Never the token itself.</summary>
    string Describe();
}

/// <summary>No token could be found or read. The message says what to do; it never contains a token.</summary>
public sealed class AzureDevOpsAuthException(string message) : Exception(message);

/// <summary>Reads a personal access token from an environment variable whose name starts with AUTOPILOT_.</summary>
public sealed class EnvironmentTokenProvider : IAzureDevOpsTokenProvider
{
    private readonly string _variable;
    private readonly Func<string, string?> _lookup;

    /// <param name="variable">Variable name; must start with AUTOPILOT_.</param>
    /// <param name="lookup">For tests. Default: the process environment, then (on Windows) the user environment.</param>
    public EnvironmentTokenProvider(string variable, Func<string, string?>? lookup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        if (!variable.StartsWith(AzureDevOpsSettings.RequiredTokenVariablePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The token variable must start with {AzureDevOpsSettings.RequiredTokenVariablePrefix}.", nameof(variable));
        }

        _variable = variable;
        _lookup = lookup ?? DefaultLookup;
    }

    public Task<AzureDevOpsCredential> GetCredentialAsync(CancellationToken ct)
    {
        var value = _lookup(_variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AzureDevOpsAuthException(
                $"The environment variable {_variable} is not set. Create a personal access token for the agent identity " +
                "(scopes: Work Items read & write, Code read & write) and store it as a user environment variable, " +
                $"for example in PowerShell: [Environment]::SetEnvironmentVariable('{_variable}', '<token>', 'User'), then open a new terminal.");
        }

        return Task.FromResult(AzureDevOpsCredential.FromPat(value));
    }

    public string Describe() => $"environment variable {_variable}";

    private static string? DefaultLookup(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        return value;
    }
}

/// <summary>Gets a Microsoft Entra access token for Azure DevOps from the signed-in Azure CLI.</summary>
public sealed class AzureCliTokenProvider : IAzureDevOpsTokenProvider
{
    /// <summary>The Azure DevOps resource (application) id that Entra tokens for Azure DevOps are issued for.</summary>
    public const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> _run;

    /// <param name="run">For tests: runs az with the given arguments.</param>
    public AzureCliTokenProvider(Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? run = null)
    {
        _run = run ?? RunAzAsync;
    }

    public static IReadOnlyList<string> Arguments { get; } =
        ["account", "get-access-token", "--resource", AzureDevOpsResource, "--query", "accessToken", "-o", "tsv"];

    public async Task<AzureDevOpsCredential> GetCredentialAsync(CancellationToken ct)
    {
        var result = await _run(Arguments, ct).ConfigureAwait(false);
        var token = result.StandardOutput.Trim();
        if (!result.Succeeded || token.Length == 0)
        {
            // stderr of az never contains the token; stdout is not shown on purpose (az.cmd prints some errors there).
            var reason = AzureCliLauncher.FirstLine(result.StandardError);
            if (reason.Length == 0 && !result.Succeeded)
            {
                reason = AzureCliLauncher.FirstLine(result.StandardOutput);
            }

            throw new AzureDevOpsAuthException(
                "The Azure CLI did not return a token for Azure DevOps (exit code " + result.ExitCode + ")" +
                (reason.Length > 0 ? ": " + reason : ".") + " " +
                "Install the Azure CLI, run 'az login' as the agent identity and try again.");
        }

        return AzureDevOpsCredential.FromBearer(token);
    }

    public string Describe() => "Azure CLI sign-in (az account get-access-token)";

    private static Task<ProcessResult> RunAzAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        AzureCliLauncher.RunAsync(arguments, Timeout, ct);
}

/// <summary>
/// Gets a Microsoft Entra access token for Azure DevOps with the client credentials flow of a service
/// principal. Tenant id, client id and client secret are read from environment variables whose names the
/// settings give; the values are never stored, logged or put in a message.
/// </summary>
public sealed class ServicePrincipalTokenProvider : IAzureDevOpsTokenProvider
{
    private readonly HttpClient _http;
    private readonly string _tenantVariable;
    private readonly string _clientIdVariable;
    private readonly string _clientSecretVariable;
    private readonly Func<string, string?> _lookup;

    public ServicePrincipalTokenProvider(
        HttpClient http, string tenantVariable, string clientIdVariable, string clientSecretVariable, Func<string, string?>? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _tenantVariable = tenantVariable;
        _clientIdVariable = clientIdVariable;
        _clientSecretVariable = clientSecretVariable;
        _lookup = lookup ?? DefaultLookup;
    }

    public async Task<AzureDevOpsCredential> GetCredentialAsync(CancellationToken ct)
    {
        var tenant = Require(_tenantVariable);
        var clientId = Require(_clientIdVariable);
        var secret = Require(_clientSecretVariable);
        if (tenant.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')))
        {
            throw new AzureDevOpsAuthException($"The value of {_tenantVariable} is not a tenant id or domain name.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = AzureCliTokenProvider.AzureDevOpsResource + "/.default",
            }),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The answer's error text can name the application; the secret is never part of it.
            throw new AzureDevOpsAuthException(
                $"Microsoft Entra refused the service principal (HTTP {(int)response.StatusCode}). Check the tenant id, client id and secret in {_tenantVariable}, {_clientIdVariable} and {_clientSecretVariable}, and that the secret has not expired.");
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var token = document.RootElement.TryGetProperty("access_token", out var value) ? value.GetString() : null;
            return AzureDevOpsCredential.FromBearer(token ?? throw new AzureDevOpsAuthException("Microsoft Entra answered without an access token."));
        }
        catch (System.Text.Json.JsonException)
        {
            throw new AzureDevOpsAuthException("Microsoft Entra answered with something that is not a token response.");
        }
    }

    public string Describe() => $"service principal (variables {_tenantVariable}, {_clientIdVariable}, {_clientSecretVariable})";

    private string Require(string variable)
    {
        var value = _lookup(variable);
        return string.IsNullOrWhiteSpace(value)
            ? throw new AzureDevOpsAuthException($"The environment variable {variable} is not set. The service principal needs {_tenantVariable}, {_clientIdVariable} and {_clientSecretVariable}; set them as user environment variables and open a new terminal.")
            : value.Trim();
    }

    private static string? DefaultLookup(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        return value;
    }
}
