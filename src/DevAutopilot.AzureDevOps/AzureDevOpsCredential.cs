using System.Net.Http.Headers;
using System.Text;
using DevAutopilot.Core.Process;

namespace DevAutopilot.AzureDevOps;

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
            // stderr of az never contains the token; stdout is not shown on purpose.
            throw new AzureDevOpsAuthException(
                "The Azure CLI did not return a token for Azure DevOps (exit code " + result.ExitCode + "). " +
                "Install the Azure CLI, run 'az login' as the agent identity and try again.");
        }

        return AzureDevOpsCredential.FromBearer(token);
    }

    public string Describe() => "Azure CLI sign-in (az account get-access-token)";

    private static Task<ProcessResult> RunAzAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        ProcessRunner.RunAsync(
            OperatingSystem.IsWindows() ? "az.cmd" : "az",
            arguments,
            Environment.CurrentDirectory,
            Timeout,
            new Dictionary<string, string?> { ["AZURE_CORE_ONLY_SHOW_ERRORS"] = "true" },
            ct);
}
