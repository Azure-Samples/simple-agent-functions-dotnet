// Console chat client for the Simple Agent function app.
// Run with: dotnet run --project Chat.csproj
// Set AGENT_URL env var to point at a deployed instance (default: http://localhost:7071)

using System.Net.Http;
using System.Text;

var baseUrl = Environment.GetEnvironmentVariable("AGENT_URL") ?? "http://localhost:7071";
var functionKey = Environment.GetEnvironmentVariable("FUNCTION_KEY");
using var http = new HttpClient();

Console.WriteLine("=== Daily Repo Digest Chat ===");
Console.WriteLine($"Endpoint: {baseUrl}/api/ask");
Console.WriteLine("Type 'exit' or 'quit' to end.\n");

while (true)
{
    Console.Write("You: ");
    var message = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(message) || message.Equals("exit", StringComparison.OrdinalIgnoreCase) || message.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Goodbye!");
        break;
    }

    try
    {
        var url = $"{baseUrl}/api/ask";
        if (!string.IsNullOrEmpty(functionKey))
            url += $"?code={functionKey}";
        var response = await http.PostAsync(url, new StringContent(message, Encoding.UTF8, "text/plain"));
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"\nAgent: {body}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nError: {ex.Message}\n");
    }
}
