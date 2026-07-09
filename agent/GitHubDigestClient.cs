using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace simple_agent_af;

internal sealed class GitHubDigestClient
{
    private const string GitHubApi = "https://api.github.com";
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<RepoDigestContext> GetDigestContextAsync(string repository, CancellationToken cancellationToken = default)
    {
        var parts = repository.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException("Repository must use owner/name format.", nameof(repository));
        }

        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var sinceText = since.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var owner = Uri.EscapeDataString(parts[0]);
        var repo = Uri.EscapeDataString(parts[1]);

        var repoInfoTask = GetAsync<GitHubRepository>($"/repos/{owner}/{repo}", cancellationToken);
        var pullsTask = GetAsync<List<GitHubPullRequest>>(
            $"/repos/{owner}/{repo}/pulls?state=open&sort=updated&direction=desc&per_page=20",
            cancellationToken);
        var issuesTask = GetAsync<List<GitHubIssue>>(
            $"/repos/{owner}/{repo}/issues?state=open&sort=updated&direction=desc&since={Uri.EscapeDataString(sinceText)}&per_page=20",
            cancellationToken);
        var runsTask = GetAsync<GitHubWorkflowRuns>(
            $"/repos/{owner}/{repo}/actions/runs?status=completed&per_page=20",
            cancellationToken);

        await Task.WhenAll(repoInfoTask, pullsTask, issuesTask, runsTask);

        return new RepoDigestContext(
            Repository: repository,
            GeneratedAt: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Lookback: "24 hours",
            Stars: repoInfoTask.Result.StargazersCount,
            Forks: repoInfoTask.Result.ForksCount,
            OpenIssues: repoInfoTask.Result.OpenIssuesCount,
            RecentOpenPullRequests: pullsTask.Result
                .Where(item => item.UpdatedAt >= since)
                .Take(10)
                .Select(item => new DigestPullRequest(item.Number, item.Title, item.User?.Login ?? "unknown", FormatUtc(item.UpdatedAt), item.HtmlUrl))
                .ToList(),
            RecentOpenIssues: issuesTask.Result
                .Where(item => item.PullRequest is null)
                .Take(10)
                .Select(item => new DigestIssue(item.Number, item.Title, item.User?.Login ?? "unknown", FormatUtc(item.UpdatedAt), item.HtmlUrl))
                .ToList(),
            RecentWorkflowFailures: runsTask.Result.WorkflowRuns
                .Where(item => item.CreatedAt >= since && IsFailure(item.Conclusion))
                .Take(10)
                .Select(item => new DigestWorkflowRun(item.Name, item.Conclusion, item.HeadBranch, FormatUtc(item.CreatedAt), item.HtmlUrl))
                .ToList());
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(GitHubApi), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("simple-agent-functions-dotnet");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    private static async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(pathAndQuery, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub API returned {(int)response.StatusCode} for {GitHubApi}{pathAndQuery}: {body}",
                null,
                response.StatusCode);
        }

        return JsonSerializer.Deserialize<T>(body, GitHubDigestJson.Options)
               ?? throw new JsonException($"GitHub API returned an empty response for {GitHubApi}{pathAndQuery}.");
    }

    private static bool IsFailure(string? conclusion) =>
        conclusion is "failure" or "timed_out" or "cancelled" or "action_required";

    private static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}

internal static class GitHubDigestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}

internal sealed record RepoDigestContext(
    string Repository,
    string GeneratedAt,
    string Lookback,
    int Stars,
    int Forks,
    int OpenIssues,
    IReadOnlyList<DigestPullRequest> RecentOpenPullRequests,
    IReadOnlyList<DigestIssue> RecentOpenIssues,
    IReadOnlyList<DigestWorkflowRun> RecentWorkflowFailures);

internal sealed record DigestPullRequest(int Number, string Title, string Author, string UpdatedAt, string Url);

internal sealed record DigestIssue(int Number, string Title, string Author, string UpdatedAt, string Url);

internal sealed record DigestWorkflowRun(string? Name, string? Conclusion, string? Branch, string CreatedAt, string? Url);

internal sealed record GitHubRepository(
    [property: JsonPropertyName("stargazers_count")] int StargazersCount,
    [property: JsonPropertyName("forks_count")] int ForksCount,
    [property: JsonPropertyName("open_issues_count")] int OpenIssuesCount);

internal sealed record GitHubUser(string Login);

internal sealed record GitHubPullRequest(int Number, string Title, GitHubUser? User, DateTimeOffset UpdatedAt, string HtmlUrl);

internal sealed record GitHubIssue(int Number, string Title, GitHubUser? User, DateTimeOffset UpdatedAt, string HtmlUrl, object? PullRequest);

internal sealed record GitHubWorkflowRuns(IReadOnlyList<GitHubWorkflowRun> WorkflowRuns);

internal sealed record GitHubWorkflowRun(string? Name, string? Conclusion, string? HeadBranch, DateTimeOffset CreatedAt, string? HtmlUrl);
