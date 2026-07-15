using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Azure.Identity;
using GitHub.Copilot;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace simple_agent_af;

public class Ask
{
    private static CopilotClient? _client;
    private static readonly Lock _clientLock = new();
    private static readonly GitHubDigestClient DigestClient = new();
    private const string DefaultRepository = "Azure/azure-functions-host";

    private static readonly string Instructions = """
        You create concise daily GitHub repository digests from live repository data.
        Focus on what changed, what needs attention, and useful next actions.
        Use clear sample language and do not invent activity that is not in the data.
        """;

    // On Azure (run-from-package), the filesystem is read-only so the native
    // binary loses its execute bit.  Copy it to /tmp and point the client there.
    private static CopilotClient GetOrCreateClient()
    {
        if (_client is not null) return _client;
        lock (_clientLock)
        {
            if (_client is not null) return _client;

            var opts = new CopilotClientOptions();
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var src = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "copilot");
                if (File.Exists(src))
                {
                    var dest = Path.Combine(Path.GetTempPath(), "copilot-cli", "copilot");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: true);
                    System.Diagnostics.Process.Start("chmod", ["+x", dest])?.WaitForExit();
                    opts.Connection = RuntimeConnection.ForStdio(dest);
                }
            }
            _client = new CopilotClient(opts);
            return _client;
        }
    }
    private static SessionConfig BuildSessionConfig()
    {
        var config = new SessionConfig
        {
            SystemMessage = new SystemMessageConfig { Content = Instructions },
            OnPermissionRequest = PermissionHandler.ApproveAll
        };
        var baseUrl = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var model = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
                    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL")
                    ?? "gpt-5-mini";
        if (!string.IsNullOrEmpty(baseUrl))
        {
            config.Model = model;
            var provider = new ProviderConfig
            {
                Type = "azure",
                BaseUrl = baseUrl
            };
            if (!string.IsNullOrEmpty(apiKey))
            {
                provider.ApiKey = apiKey;
            }
            else
            {
                var credential = new DefaultAzureCredential();
                var token = credential.GetToken(new Azure.Core.TokenRequestContext(
                    ["https://cognitiveservices.azure.com/.default"]));
                provider.BearerToken = token.Token;
            }
            config.Provider = provider;
        }
        return config;
    }

    private static string RepositoryFromPrompt(string prompt)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            prompt,
            @"\b([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)\b");

        return match.Success
            ? match.Groups[1].Value
            : Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? DefaultRepository;
    }

    private static async Task<string> RunDigestAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryFromPrompt(prompt);
        var context = await DigestClient.GetDigestContextAsync(repository, cancellationToken);
        var digestPrompt = $"""
            Create a concise daily repo digest for {repository}.

            User request:
            {prompt}

            Live GitHub data:
            {JsonSerializer.Serialize(context, GitHubDigestJson.Options)}

            Return:
            1. A one-line summary.
            2. Pull requests updated in the last 24 hours.
            3. Issues updated in the last 24 hours.
            4. Workflow failures from the last 24 hours.
            5. Suggested next actions.

            If a section has no items, say "None found".
            """;

        await using var session = await GetOrCreateClient().CreateSessionAsync(BuildSessionConfig());
        var reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = digestPrompt });

        var content = reply?.Data?.Content;
        return string.IsNullOrEmpty(content) ? "No response" : content;
    }

    [Function("ask")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ask")] HttpRequestData req)
    {
        var prompt = await new StreamReader(req.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = $"Create a concise daily repo digest for {DefaultRepository}.";

        HttpResponseData response;
        string content;
        try
        {
            content = await RunDigestAsync(prompt, req.FunctionContext.CancellationToken);
            response = req.CreateResponse(HttpStatusCode.OK);
        }
        catch (HttpRequestException ex)
        {
            content = ex.Message;
            response = req.CreateResponse(HttpStatusCode.BadGateway);
        }
        catch (ArgumentException ex)
        {
            content = ex.Message;
            response = req.CreateResponse(HttpStatusCode.BadRequest);
        }
        response.Headers.Add("Content-Type", "text/plain");
        await response.WriteStringAsync(content);
        return response;
    }

    [Function("daily_repo_digest")]
    public async Task DailyRepoDigest(
        [TimerTrigger("0 0 16,17 * * *", RunOnStartup = false, UseMonitor = true)] TimerInfo timer,
        FunctionContext context)
    {
        var logger = context.GetLogger("daily_repo_digest");
        var nowPacific = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, PacificTimeZone.Get());
        if (nowPacific.Hour != 9)
        {
            logger.LogInformation("Skipping daily repo digest because it is {Time} Pacific.", nowPacific.ToString("HH:mm"));
            return;
        }

        if (timer.IsPastDue)
        {
            logger.LogInformation("Daily repo digest timer is past due.");
        }

        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? DefaultRepository;
        var digest = await RunDigestAsync($"Create a concise daily repo digest for {repository}.", context.CancellationToken);
        logger.LogInformation("Daily repo digest:{NewLine}{Digest}", Environment.NewLine, digest);
    }
}
