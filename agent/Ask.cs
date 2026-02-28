using System.Net;
using System.Runtime.InteropServices;
using Azure.Identity;
using GitHub.Copilot.SDK;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace simple_agent_af;

public class Ask
{
    private static CopilotClient? _client;
    private static readonly Lock _clientLock = new();

    private static readonly string Instructions = """
        1. A robot may not injure a human being...
        2. A robot must obey orders given it by human beings...
        3. A robot must protect its own existence...
        
        Objective: Give me the TLDR in exactly 5 words.
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
                    opts.CliPath = dest;
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

    [Function("ask")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ask")] HttpRequestData req)
    {
        var prompt = await new StreamReader(req.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = "What are the laws?";

        await using var session = await GetOrCreateClient().CreateSessionAsync(BuildSessionConfig());
        var reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt });

        var content = (reply?.Data?.Content) ?? "No response";
        if (string.IsNullOrEmpty(content))
            content = "No response";

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/plain");
        await response.WriteStringAsync(content);
        return response;
    }
}
