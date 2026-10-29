namespace Uniform.Matrix.WebRtc.Tests;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>A headless Chromium driven through the <see href="https://chromedevtools.github.io/devtools-protocol/">DevTools protocol</see>, to run a browser's WebRTC against ours.</summary>
internal sealed partial class Chromium : IAsyncDisposable
{
    private readonly Process process;
    private readonly ClientWebSocket socket = new();
    private readonly string profile;
    private int nextId;

    /// <summary>Wrap a started browser.</summary>
    private Chromium(Process process, string profile)
    {
        // Process and profile, removed when disposed
        this.process = process;
        this.profile = profile;
    }

    /// <summary>Start Chromium with a blank page, and attach to the page; null when there is no Chromium.</summary>
    public static async Task<Chromium?> StartAsync(CancellationToken cancellationToken)
    {
        // A browser on PATH
        string? executable = new[] { "chromium", "chromium-browser", "google-chrome" }.Select(Find).FirstOrDefault(p => p is not null);
        if (executable is null) return null;
        string profile = Path.Combine(Path.GetTempPath(), "uniform-matrix-chromium-" + Guid.NewGuid().ToString("N"));
        ProcessStartInfo start = new(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "--headless=new", "--no-sandbox", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--autoplay-policy=no-user-gesture-required", $"--user-data-dir={profile}", "about:blank" }) start.ArgumentList.Add(argument);
        Process process = Process.Start(start)!;
        Chromium chromium = new(process, profile);

        // The DevTools port from what it prints, then the page's socket
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        int port = 0;
        List<string> output = [];
        while (port == 0 && await process.StandardError.ReadLineAsync(limit.Token) is string line)
        {
            output.Add(line);
            if (DevToolsLine().Match(line) is { Success: true } match) port = int.Parse(match.Groups[1].Value);
        }
        if (port == 0)
        {
            // On a browser that cannot run here, e.g. in a container: its own words
            await chromium.DisposeAsync();
            throw new ChromiumUnavailableException(string.Join('\n', output.TakeLast(10)));
        }
        _ = DrainAsync(process.StandardError);
        using HttpClient http = new();
        JsonArray targets = (await http.GetFromJsonAsync<JsonArray>($"http://127.0.0.1:{port}/json/list", limit.Token))!;
        string page = targets.First(t => t!["type"]!.GetValue<string>() == "page")!["webSocketDebuggerUrl"]!.GetValue<string>();
        await chromium.socket.ConnectAsync(new Uri(page), limit.Token);
        // Return browser
        return chromium;
    }

    /// <summary>Evaluate JavaScript in the page, awaiting a promise it returns; return its value as JSON.</summary>
    /// <exception cref="InvalidOperationException">When the script throws.</exception>
    public async Task<JsonNode?> EvaluateAsync(string script, CancellationToken cancellationToken)
    {
        // Runtime.evaluate, wrapped as an async function
        int id = ++nextId;
        JsonObject request = new()
        {
            ["id"] = id,
            ["method"] = "Runtime.evaluate",
            ["params"] = new JsonObject { ["expression"] = $"(async () => {{ {script} }})()", ["awaitPromise"] = true, ["returnByValue"] = true },
        };
        await socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken);

        // Its response, skipping events
        while (true)
        {
            JsonNode message = await ReceiveAsync(cancellationToken);
            if (message["id"]?.GetValue<int>() != id) continue;
            if (message["result"]?["exceptionDetails"] is JsonNode error) throw new InvalidOperationException(error.ToJsonString());
            // Return value
            return message["result"]?["result"]?["value"];
        }
    }

    /// <summary>Close the page's socket, and stop the browser.</summary>
    public async ValueTask DisposeAsync()
    {
        // Socket, process, profile
        socket.Dispose();
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
        try { Directory.Delete(profile, true); }
        catch (IOException) { }
    }

    /// <summary>Return the next whole message from the page's socket.</summary>
    private async Task<JsonNode> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Frames until the end of a message
        using MemoryStream message = new();
        byte[] buffer = new byte[65536];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        // Return parsed
        return JsonNode.Parse(message.ToArray())!;
    }

    /// <summary>Read what the browser prints, so that it never blocks on a full pipe.</summary>
    private static async Task DrainAsync(StreamReader reader)
    {
        // Until it exits
        while (await reader.ReadLineAsync() is not null) { }
    }

    /// <summary>Return the full path of an executable on PATH, or null.</summary>
    private static string? Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>The line Chromium prints with its DevTools port.</summary>
    [GeneratedRegex(@"DevTools listening on ws://[^:]+:(\d+)/")]
    private static partial Regex DevToolsLine();
}

/// <summary>Chromium is installed but did not start DevTools here, e.g. on a CI runner whose kernel denies its sandbox; the message has its last output.</summary>
internal sealed class ChromiumUnavailableException(string output) : Exception($"Chromium did not start DevTools:\n{output}");
