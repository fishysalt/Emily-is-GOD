using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;

namespace ValleyTalkMemory;

/// <summary>
/// Minimal OpenAI-compatible chat client. Connection details default to whatever
/// ValleyTalk itself is configured with (config.json in its mod folder), so the memory
/// compressor talks to exactly the same model the player already trusts.
/// </summary>
internal sealed class LlmClient
{
    private static readonly HttpClient Http = new HttpClient();

    private readonly IMonitor _monitor;

    public LlmClient(IMonitor monitor)
    {
        _monitor = monitor;
    }

    public string Provider { get; private set; } = "";

    public string ServerAddress { get; private set; } = "";

    public string ApiKey { get; private set; } = "";

    public string ModelName { get; private set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerAddress) && !string.IsNullOrWhiteSpace(ModelName);

    public void Configure(ModConfig config, IModHelper helper)
    {
        Provider = config.Provider;
        ServerAddress = config.ServerAddress;
        ApiKey = config.ApiKey;
        ModelName = config.ModelName;

        try
        {
            string dir = FindValleyTalkFolder(helper);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string path = Path.Combine(dir, "config.json");
                if (File.Exists(path))
                {
                    JObject vt = JObject.Parse(File.ReadAllText(path));
                    if (string.IsNullOrWhiteSpace(ServerAddress)) ServerAddress = (string)vt["ServerAddress"] ?? "";
                    if (string.IsNullOrWhiteSpace(ApiKey)) ApiKey = (string)vt["ApiKey"] ?? "";
                    if (string.IsNullOrWhiteSpace(ModelName)) ModelName = (string)vt["ModelName"] ?? "";
                    if (string.IsNullOrWhiteSpace(Provider)) Provider = (string)vt["Provider"] ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Could not read ValleyTalk config.json: {ex.Message}", LogLevel.Warn);
        }

        ServerAddress = (ServerAddress ?? "").Trim().TrimEnd('/');
        _monitor?.Log($"LLM for memory compression: provider={Provider} model={ModelName} endpoint={ServerAddress}", LogLevel.Info);
    }

    /// <summary>
    /// Locates ValleyTalk's mod folder (where its own config.json lives) by matching the
    /// unique ID in nearby manifests. IModInfo does not expose the directory path.
    /// </summary>
    private static string FindValleyTalkFolder(IModHelper helper)
    {
        const string wantedId = "dandm1.ValleyTalk";
        // Our own folder is "<game>\Mods\ValleyTalkMemory", so its parent is the Mods root.
        string ownDir = helper?.DirectoryPath;
        if (string.IsNullOrWhiteSpace(ownDir))
            return null;
        string modsRoot = Path.GetDirectoryName(ownDir.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
            return null;

        var candidates = new List<string>();
        foreach (string level1 in Directory.EnumerateDirectories(modsRoot))
        {
            candidates.Add(Path.Combine(level1, "manifest.json"));
            foreach (string level2 in Directory.EnumerateDirectories(level1))
                candidates.Add(Path.Combine(level2, "manifest.json"));
        }

        foreach (string manifestPath in candidates)
        {
            try
            {
                if (!File.Exists(manifestPath))
                    continue;
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                if (string.Equals((string)manifest["UniqueID"], wantedId, StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(manifestPath);
            }
            catch
            {
                // Ignore malformed manifests from unrelated mods.
            }
        }
        return null;
    }

    /// <summary>Runs one chat completion. Returns null on any failure.</summary>
    public async Task<string> CompleteAsync(string system, string user, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            _monitor?.Log("No LLM configured for memory compression.", LogLevel.Warn);
            return null;
        }

        try
        {
            var payload = new JObject
            {
                ["model"] = ModelName,
                ["temperature"] = 0.3,
                ["stream"] = false,
                ["max_tokens"] = 1400,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = system },
                    new JObject { ["role"] = "user", ["content"] = user }
                }
            };

            string url = ServerAddress + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));

            using HttpResponseMessage response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _monitor?.Log($"Memory LLM HTTP {(int)response.StatusCode}: {Truncate(body, 300)}", LogLevel.Warn);
                return null;
            }

            JObject json = JObject.Parse(body);
            string content = (string)json["choices"]?[0]?["message"]?["content"];
            if (string.IsNullOrWhiteSpace(content))
            {
                _monitor?.Log($"Memory LLM returned no content: {Truncate(body, 300)}", LogLevel.Warn);
                return null;
            }
            return content;
        }
        catch (OperationCanceledException)
        {
            _monitor?.Log("Memory LLM call timed out.", LogLevel.Warn);
            return null;
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Memory LLM call failed: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "...";
}
