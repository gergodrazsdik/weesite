// The MCP test runner for the mcp-test skill: every test of both MCP endpoints over HTTP, each
// call timed. It signs in with its own OAuth client (the editor consents in the browser), keeps
// that client and its grant in ~/.cache/weesite-mcp-test, and writes the results as JSON.
//
//   dotnet run .claude/skills/mcp-test/McpTest.cs -- --site https://example.com --consent drafts|publish
//       [--lifecycle] [--report results.json] [--wait 180] [--open]
//
// --consent says which grant the run needs. When the cached grant allows something else, the
// runner revokes it and asks for a new consent. --lifecycle ends the run with the token tests
// (refresh rotation, reuse detection, revocation), which revoke the grant: the next run asks again.
#:property TreatWarningsAsErrors=false
#:property PublishAot=false
#:property EnableNETAnalyzers=false
#:property GenerateDocumentationFile=false
#:property NoWarn=$(NoWarn);CS1591;IDE0005;IDE0008;IDE0058;IDE0011;IDE0022;IDE0046;IDE0045;IDE0010;IDE0072

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var options = Options.Parse(args);
var runner = new Runner(options);
var exit = await runner.RunAsync();
return exit;

internal sealed record Options(Uri Site, string Consent, bool Lifecycle, string Report, int Wait, bool Open)
{
    public static Options Parse(string[] args)
    {
        string? Get(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var site = Get("--site") ?? throw new ArgumentException("--site https://<site> is required.");
        var consent = Get("--consent") ?? "drafts";
        if (consent is not ("drafts" or "publish"))
        {
            throw new ArgumentException("--consent is drafts or publish.");
        }

        return new Options(
            new Uri(site.TrimEnd('/')),
            consent,
            args.Contains("--lifecycle"),
            Get("--report") ?? Path.Combine(Path.GetTempPath(), $"mcp-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"),
            int.TryParse(Get("--wait"), out var wait) ? wait : 180,
            args.Contains("--open"));
    }
}

internal sealed record Result(string Group, string Name, bool Passed, string Detail, double Ms, int? Status);

internal sealed record Cache(string? ClientId, string? RefreshToken);

internal sealed class Runner(Options options)
{
    // A 64×40 grey gradient PNG (ImageMagick, metadata stripped).
    private const string TestImage =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAoEAIAAACRPLf9AAAAz0lEQVRo3u3ayw3DMAwE0QRgRWIpdnlJTz74mFqWOaiIOXheBQIEr/jx+7ru+/d7CVLTc86HPsZzVVYOL4BT6ZzxAjCVnnO+9DGeq9I5/AI4NSs+wqAdQV4AxgiCVYwgVKXnjFUQptI2YqSaNoJIlTV2wiAjCFYxglBGEKymcziM4ziOhjkLgrkRgxlBMJfysD2OtgrCOI6GWQXB3IjBXMrDLENhlR4bMdAeR1uGYirLCCLVGEEoH2GYnTDMKgi2I8gqCOOPWbDKMoJIVkGwP+ZSpiRERKkxAAAAAElFTkSuQmCC";

    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private readonly List<Result> _results = [];
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "weesite-mcp-test", options.Site.Host + ".json");

    private string? _access;
    private string _siteName = "";
    private int _id;

    private string Base => options.Site.ToString().TrimEnd('/');

    public async Task<int> RunAsync()
    {
        Console.WriteLine($"MCP tests on {Base}, consent: {options.Consent}{(options.Lifecycle ? ", with the token lifecycle" : "")}");
        await WakeAsync();
        await DiscoveryAsync();
        await PublicAsync();
        if (!await SignInAsync())
        {
            Write();
            return 2;
        }

        await EditorReadAsync();
        await EditorWritesAsync();
        if (options.Consent == "drafts")
        {
            await DraftsOnlyAsync();
        }
        else
        {
            await PublishCycleAsync();
        }

        if (options.Lifecycle)
        {
            await LifecycleAsync();
        }

        Write();
        return _results.All(r => r.Passed) ? 0 : 1;
    }

    // An environment that scales to zero needs up to a minute to start (F-0063).
    private async Task WakeAsync()
    {
        var watch = Stopwatch.StartNew();
        int? status = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(options.Wait))
        {
            try
            {
                using var response = await _http.GetAsync(new Uri(Base + "/health"));
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                status = null;
            }

            Console.WriteLine($"  waiting for the site to start ({watch.Elapsed.TotalSeconds:0} s)…");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Add("Warm-up", "The site answers /health", status == 200, $"ready after {watch.Elapsed.TotalSeconds:0.0} s", watch.Elapsed.TotalMilliseconds, status);
    }

    private async Task DiscoveryAsync()
    {
        const string G = "Discovery and sign-in";
        await StepAsync(G, "Authorization server metadata", async () =>
        {
            var (status, json) = await GetJsonAsync("/.well-known/oauth-authorization-server");
            var ok = status == 200 && json?["issuer"]?.GetValue<string>() == Base
                && json["code_challenge_methods_supported"]?.AsArray().Any(m => m?.GetValue<string>() == "S256") == true;
            return (ok, $"issuer {json?["issuer"]}", status);
        });
        await StepAsync(G, "Protected resource metadata names the server", async () =>
        {
            var (status, json) = await GetJsonAsync("/.well-known/oauth-protected-resource/mcp/editor");
            var ok = status == 200 && json?["resource"]?.GetValue<string>() == Base + "/mcp/editor"
                && json["authorization_servers"]?[0]?.GetValue<string>() == Base;
            return (ok, $"resource {json?["resource"]}", status);
        });
        await StepAsync(G, "/mcp/editor without a token: 401 with the metadata URL", async () =>
        {
            using var response = await PostRpcRawAsync("/mcp/editor", "initialize", Initialize(), null);
            var header = response.Headers.WwwAuthenticate.ToString();
            return ((int)response.StatusCode == 401 && header.Contains("resource_metadata=", StringComparison.Ordinal), header, (int)response.StatusCode);
        });
        await StepAsync(G, "/mcp/editor with a bad token: 401 invalid_token", async () =>
        {
            using var response = await PostRpcRawAsync("/mcp/editor", "initialize", Initialize(), "not-a-token");
            var header = response.Headers.WwwAuthenticate.ToString();
            return ((int)response.StatusCode == 401 && header.Contains("invalid_token", StringComparison.Ordinal), header, (int)response.StatusCode);
        });
        await StepAsync(G, "Registration refuses a non-loopback http redirect", async () =>
        {
            using var response = await _http.PostAsJsonAsync(new Uri(Base + "/oauth/register"), new { client_name = "refused", redirect_uris = new[] { "http://example.com/cb" } });
            var json = await ReadJsonAsync(response);
            return ((int)response.StatusCode == 400 && json?["error"]?.GetValue<string>() == "invalid_redirect_uri", $"{json?["error"]}", (int)response.StatusCode);
        });
        await StepAsync(G, "The token endpoint refuses an unknown code", async () =>
        {
            using var form = new FormUrlEncodedContent([new("grant_type", "authorization_code"), new("code", "nope"), new("client_id", "nope"), new("code_verifier", new string('a', 43))]);
            using var response = await _http.PostAsync(new Uri(Base + "/oauth/token"), form);
            var json = await ReadJsonAsync(response);
            return ((int)response.StatusCode == 400 && json?["error"]?.GetValue<string>() == "invalid_grant", $"{json?["error"]}", (int)response.StatusCode);
        });
        await StepAsync(G, "Authorizing needs the editor sign-in", async () =>
        {
            using var response = await _http.GetAsync(new Uri(Base + "/oauth/authorize?response_type=code&client_id=x&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcb"));
            var location = response.Headers.Location?.ToString() ?? "";
            return ((int)response.StatusCode == 302 && location.Contains("/admin/sign-in", StringComparison.Ordinal), location, (int)response.StatusCode);
        });
    }

    private async Task PublicAsync()
    {
        const string G = "Public endpoint /mcp";
        await StepAsync(G, "initialize: the site introduces itself", async () =>
        {
            var (status, message) = await RpcAsync("/mcp", "initialize", Initialize(), null);
            var info = message?["result"]?["serverInfo"];
            _siteName = info?["title"]?.GetValue<string>() ?? "";
            return (status == 200 && _siteName.Length > 0, $"\"{_siteName}\" ({info?["name"]})", status);
        });
        await StepAsync(G, "tools/list: three read-only tools", async () =>
        {
            var (status, message) = await RpcAsync("/mcp", "tools/list", new JsonObject(), null);
            var tools = message?["result"]?["tools"]?.AsArray() ?? [];
            var names = tools.Select(t => t?["name"]?.GetValue<string>()).Order().ToList();
            var ok = names.SequenceEqual(["get_content", "list_content", "search_content"])
                && tools.All(t => t?["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true);
            return (ok, string.Join(", ", names), status);
        });
        string? url = null;
        await StepAsync(G, "list_content", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp", "list_content", new JsonObject { ["limit"] = 5 }, null);
            url = Regex.Match(text, "\"url\":\"([^\"]+)\"").Groups[1].Value;
            return (!isError && url.Length > 0, $"first: {url}", status);
        });
        await StepAsync(G, "search_content", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp", "search_content", new JsonObject { ["query"] = "the", ["limit"] = 3 }, null);
            return (!isError && text.StartsWith('['), $"{Regex.Matches(text, "\"url\"").Count} results", status);
        });
        await StepAsync(G, "get_content: Markdown with front matter", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp", "get_content", new JsonObject { ["url"] = url ?? "/" }, null);
            return (!isError && text.Contains("---", StringComparison.Ordinal), $"{text.Length} characters", status);
        });
    }

    private async Task EditorReadAsync()
    {
        const string G = "Editor endpoint: reading";
        await StepAsync(G, "initialize: \"<site> (editor)\" with instructions", async () =>
        {
            var (status, message) = await RpcAsync("/mcp/editor", "initialize", Initialize(), _access);
            var title = message?["result"]?["serverInfo"]?["title"]?.GetValue<string>() ?? "";
            var instructions = message?["result"]?["instructions"]?.GetValue<string>() ?? "";
            return (title == _siteName + " (editor)" && instructions.Length > 0, $"\"{title}\"", status);
        });
        await StepAsync(G, "tools/list: the seven editor tools", async () =>
        {
            var (status, message) = await RpcAsync("/mcp/editor", "tools/list", new JsonObject(), _access);
            var names = (message?["result"]?["tools"]?.AsArray() ?? []).Select(t => t?["name"]?.GetValue<string>()).Order().ToList();
            var ok = names.SequenceEqual(["create_item", "get_item", "list_items", "list_media", "set_status", "update_item", "upload_media"]);
            return (ok, string.Join(", ", names), status);
        });
        await StepAsync(G, "list_items", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "list_items", new JsonObject { ["limit"] = 50 }, _access);
            return (!isError, $"{Regex.Matches(text, "\"path\"").Count} items", status);
        });
        await StepAsync(G, "get_item", async () =>
        {
            var (_, _, list) = await ToolAsync("/mcp/editor", "list_items", new JsonObject { ["limit"] = 1 }, _access, record: false);
            var path = Regex.Match(list, "\"path\":\"([^\"]+)\"").Groups[1].Value;
            var (status, isError, text) = await ToolAsync("/mcp/editor", "get_item", new JsonObject { ["path"] = path }, _access);
            return (!isError && text.Contains("\"version\"", StringComparison.Ordinal), path, status);
        });
        await StepAsync(G, "list_media", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "list_media", new JsonObject { ["limit"] = 50 }, _access);
            return (!isError, $"{Regex.Matches(text, "\"name\"").Count} files", status);
        });
    }

    private async Task EditorWritesAsync()
    {
        const string G = "Editor endpoint: drafts";
        await StepAsync(G, "upload_media refuses an image without alt", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "upload_media", new JsonObject { ["fileName"] = "mcp-test.png", ["contentBase64"] = TestImage }, _access);
            return (isError && text.Contains("Describe the image", StringComparison.Ordinal), First(text), status);
        });
        await StepAsync(G, "upload_media with alt", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "upload_media", new JsonObject { ["fileName"] = "mcp-test.png", ["contentBase64"] = TestImage, ["alt"] = "A grey test rectangle" }, _access);
            return (!isError && text.Contains("![A grey test rectangle](/media/", StringComparison.Ordinal), Regex.Match(text, "\"path\":\"([^\"]+)\"").Groups[1].Value, status);
        });
        await StepAsync(G, "create_item refuses a post without a date", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "create_item", new JsonObject { ["markdown"] = "---\ntitle: No date\nslug: mcp-test-no-date\n---\n\nText.\n" }, _access);
            return (isError && text.Contains("needs a date", StringComparison.Ordinal), First(text), status);
        });

        var (path, version) = await EnsureItemAsync(G, "mcp-test-draft", "MCP test draft");
        if (path is null)
        {
            return;
        }

        string? newVersion = null;
        await StepAsync(G, "update_item at the version read; the status stays", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "update_item", new JsonObject { ["path"] = path, ["version"] = version, ["markdown"] = Post("MCP test draft", "mcp-test-draft", "status: published\n", $"Updated {DateTime.UtcNow:O}.") }, _access);
            newVersion = Value(text, "version");
            var itemStatus = Value(text, "status");
            return (!isError && itemStatus is "draft" or "archived", $"status {itemStatus}", status);
        });
        await StepAsync(G, "update_item at an old version is refused", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "update_item", new JsonObject { ["path"] = path, ["version"] = version, ["markdown"] = Post("MCP test draft", "mcp-test-draft") }, _access);
            return (isError && text.Contains("Someone saved", StringComparison.Ordinal), First(text), status);
        });
    }

    private async Task DraftsOnlyAsync()
    {
        const string G = "Editor endpoint: drafts only";
        await StepAsync(G, "set_status is refused", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = "posts/mcp-test-draft.md", ["version"] = "x", ["status"] = "published" }, _access);
            return (isError && text.Contains("drafts only", StringComparison.Ordinal), First(text), status);
        });
        await StepAsync(G, "update_item on a published post is refused", async () =>
        {
            var (_, _, list) = await ToolAsync("/mcp/editor", "list_items", new JsonObject { ["status"] = "published", ["limit"] = 1 }, _access, record: false);
            var path = Regex.Match(list, "\"path\":\"([^\"]+)\"").Groups[1].Value;
            var (_, _, item) = await ToolAsync("/mcp/editor", "get_item", new JsonObject { ["path"] = path }, _access, record: false);
            var (status, isError, text) = await ToolAsync("/mcp/editor", "update_item", new JsonObject { ["path"] = path, ["version"] = Value(item, "version"), ["markdown"] = Value(item, "markdown") }, _access);
            return (isError && text.Contains("drafts only", StringComparison.Ordinal), path, status);
        });
    }

    private async Task PublishCycleAsync()
    {
        const string G = "Editor endpoint: publishing";
        const string Slug = "mcp-test-cycle";
        var (path, version) = await EnsureItemAsync(G, Slug, "MCP test cycle");
        if (path is null)
        {
            return;
        }

        var stale = version;
        await StepAsync(G, "Back to draft before the cycle", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = version, ["status"] = "draft" }, _access);
            version = Value(text, "version") ?? version;
            return (!isError, Value(text, "status") ?? First(text), status);
        });
        await StepAsync(G, "set_status at an old version is refused", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = stale, ["status"] = "draft" }, _access);
            return (isError && text.Contains("Someone saved", StringComparison.Ordinal), First(text), status);
        });
        await StepAsync(G, "scheduled needs publish_at", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = version, ["status"] = "scheduled" }, _access);
            return (isError && text.Contains("needs publish_at", StringComparison.Ordinal), First(text), status);
        });
        await StepAsync(G, "scheduled in the past is refused", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = version, ["status"] = "scheduled", ["publishAt"] = "2020-01-01T00:00:00Z" }, _access);
            return (isError && text.Contains("in the past", StringComparison.Ordinal), First(text), status);
        });
        await StepAsync(G, "scheduled for 2099", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = version, ["status"] = "scheduled", ["publishAt"] = "2099-01-01T09:00:00Z" }, _access);
            version = Value(text, "version") ?? version;
            return (!isError && Value(text, "status") == "scheduled", Value(text, "status") ?? First(text), status);
        });
        await PageAsync(G, "A scheduled post isn't public", "/blog/" + Slug, 404);
        string? url = null;
        await StepAsync(G, "published", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = version, ["status"] = "published" }, _access);
            version = Value(text, "version") ?? version;
            url = Value(text, "url");
            return (!isError && url is not null, url ?? First(text), status);
        });
        await PageAsync(G, "The published post is public", "/blog/" + Slug, 200);
        await StepAsync(G, "The feed has it", async () =>
        {
            var watch = Stopwatch.StartNew();
            using var response = await _http.GetAsync(new Uri(Base + "/feed.xml"));
            var body = await response.Content.ReadAsStringAsync();
            return (response.IsSuccessStatusCode && body.Contains("MCP test cycle", StringComparison.Ordinal), $"{body.Length} characters", (int)response.StatusCode);
        });
        await StepAsync(G, "update_item renames the Slug of a public post", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "update_item", new JsonObject { ["path"] = path, ["version"] = version, ["markdown"] = Post("MCP test cycle", Slug + "-renamed") }, _access);
            version = Value(text, "version") ?? version;
            return (!isError && Value(text, "url")?.EndsWith(Slug + "-renamed", StringComparison.Ordinal) == true, Value(text, "url") ?? First(text), status);
        });
        await PageAsync(G, "The old address redirects", "/blog/" + Slug, 301);
        await StepAsync(G, "The Slug changes back", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "update_item", new JsonObject { ["path"] = path, ["version"] = version, ["markdown"] = Post("MCP test cycle", Slug) }, _access);
            version = Value(text, "version") ?? version;
            return (!isError, Value(text, "url") ?? First(text), status);
        });
        await StepAsync(G, "draft again", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = version, ["status"] = "draft" }, _access);
            version = Value(text, "version") ?? version;
            return (!isError && Value(text, "status") == "draft", Value(text, "status") ?? First(text), status);
        });
        await PageAsync(G, "A draft isn't public", "/blog/" + Slug, 404);
        await StepAsync(G, "archived (the test leaves nothing public)", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = path, ["version"] = version, ["status"] = "archived" }, _access);
            return (!isError && Value(text, "status") == "archived", Value(text, "status") ?? First(text), status);
        });
        await StepAsync(G, "list_items shows it under archived", async () =>
        {
            var (status, isError, text) = await ToolAsync("/mcp/editor", "list_items", new JsonObject { ["status"] = "archived", ["query"] = Slug }, _access);
            return (!isError && text.Contains(path, StringComparison.Ordinal), path, status);
        });
    }

    // The token tests end the grant: refresh rotates the refresh token, the old one used again
    // revokes the grant, and then the access token stops working.
    private async Task LifecycleAsync()
    {
        const string G = "Tokens";
        var cache = ReadCache();
        var old = cache.RefreshToken;
        string? next = null;
        await StepAsync(G, "Refresh gives a new refresh token", async () =>
        {
            var (status, json) = await RefreshAsync(old);
            next = json?["refresh_token"]?.GetValue<string>();
            _access = json?["access_token"]?.GetValue<string>() ?? _access;
            return (status == 200 && next is not null && next != old, "rotated", status);
        });
        await StepAsync(G, "The old refresh token again is refused", async () =>
        {
            var (status, json) = await RefreshAsync(old);
            return (status == 400 && json?["error"]?.GetValue<string>() == "invalid_grant", $"{json?["error"]}", status);
        });
        await StepAsync(G, "…and ends the grant: the new one is refused too", async () =>
        {
            var (status, json) = await RefreshAsync(next);
            return (status == 400, $"{json?["error"]}", status);
        });
        await StepAsync(G, "The access token stops working", async () =>
        {
            using var response = await PostRpcRawAsync("/mcp/editor", "initialize", Initialize(), _access);
            return ((int)response.StatusCode == 401, "401 after the grant ended", (int)response.StatusCode);
        });
        SaveCache(cache with { RefreshToken = null });
    }

    // A test post at a fixed slug, so runs don't pile them up: created the first time, then read.
    private async Task<(string? Path, string? Version)> EnsureItemAsync(string group, string slug, string title)
    {
        string? path = null, version = null;
        var (_, _, found) = await ToolAsync("/mcp/editor", "list_items", new JsonObject { ["query"] = slug, ["limit"] = 200 }, _access, record: false);
        var (_, _, archived) = await ToolAsync("/mcp/editor", "list_items", new JsonObject { ["query"] = slug, ["status"] = "archived" }, _access, record: false);
        var existing = Regex.Matches(found + archived, "\"path\":\"([^\"]+)\"[^}]*\"slug\":\"([^\"]+)\"")
            .FirstOrDefault(m => m.Groups[2].Value == slug)?.Groups[1].Value;
        if (existing is null)
        {
            await StepAsync(group, "create_item saves a draft despite status: published", async () =>
            {
                var (status, isError, text) = await ToolAsync("/mcp/editor", "create_item", new JsonObject { ["markdown"] = Post(title, slug, "status: published\n") }, _access);
                path = Value(text, "path");
                version = Value(text, "version");
                return (!isError && Value(text, "status") == "draft", $"{path}: {Value(text, "status") ?? First(text)}", status);
            });
        }
        else
        {
            await StepAsync(group, "get_item of the test post from an earlier run", async () =>
            {
                var (status, isError, text) = await ToolAsync("/mcp/editor", "get_item", new JsonObject { ["path"] = existing }, _access);
                path = existing;
                version = Value(text, "version");
                return (!isError && version is not null, existing, status);
            });
        }

        return (path, version);
    }

    private async Task PageAsync(string group, string name, string path, int expected) =>
        await StepAsync(group, name, async () =>
        {
            using var response = await _http.GetAsync(new Uri(Base + path));
            return ((int)response.StatusCode == expected, $"{path}: {(int)response.StatusCode}", (int)response.StatusCode);
        });

    // Signs in: the cached grant when it allows what the run needs; otherwise a new consent.
    private async Task<bool> SignInAsync()
    {
        var cache = ReadCache();
        if (cache.RefreshToken is not null)
        {
            var (status, json) = await RefreshAsync(cache.RefreshToken);
            if (status == 200)
            {
                _access = json!["access_token"]!.GetValue<string>();
                SaveCache(cache with { RefreshToken = json["refresh_token"]!.GetValue<string>() });
                var allows = await ProbeAsync();
                if (allows == options.Consent)
                {
                    Add("Sign-in", "The cached grant", true, $"allows {Describe(allows)}", 0, 200);
                    return true;
                }

                Console.WriteLine($"\nThe cached grant allows {Describe(allows)}, and this run needs {Describe(options.Consent)}: revoking it.");
                using var form = new FormUrlEncodedContent([new("token", ReadCache().RefreshToken!), new("client_id", cache.ClientId!)]);
                using var _ = await _http.PostAsync(new Uri(Base + "/oauth/revoke"), form);
                SaveCache(ReadCache() with { RefreshToken = null });
            }
            else
            {
                Console.WriteLine("\nThe cached grant is gone (revoked, expired, or ended by a test): a new consent is needed.");
                SaveCache(cache with { RefreshToken = null });
            }
        }

        return await ConsentAsync();
    }

    private async Task<bool> ConsentAsync()
    {
        var cache = ReadCache();
        var clientId = cache.ClientId;
        if (clientId is null)
        {
            using var registered = await _http.PostAsJsonAsync(new Uri(Base + "/oauth/register"), new { client_name = "Weesite MCP test", redirect_uris = new[] { "http://127.0.0.1/callback" } });
            clientId = (await ReadJsonAsync(registered))?["client_id"]?.GetValue<string>();
            if (clientId is null)
            {
                Add("Sign-in", "Registration", false, $"HTTP {(int)registered.StatusCode}", 0, (int)registered.StatusCode);
                return false;
            }

            SaveCache(cache with { ClientId = clientId });
        }

        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(12));
        var url = $"{Base}/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}"
            + $"&state={state}&code_challenge={challenge}&code_challenge_method=S256&resource={Uri.EscapeDataString(Base + "/mcp/editor")}";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var choice = options.Consent == "publish" ? "\"Also edit, publish, schedule and archive…\"" : "\"Read, write drafts and upload media…\"";
        Console.WriteLine($"\nCONSENT NEEDED: open this address, sign in as an editor, choose {choice}, then Allow:\n\n  {url}\n");
        if (options.Open)
        {
            TryOpen(url);
        }

        var watch = Stopwatch.StartNew();
        var contextTask = listener.GetContextAsync();
        if (await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(10))) != contextTask)
        {
            Add("Sign-in", "Consent in the browser", false, "no answer within 10 minutes", watch.Elapsed.TotalMilliseconds, null);
            return false;
        }

        var context = await contextTask;
        var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
        var page = Encoding.UTF8.GetBytes("<!doctype html><title>Weesite MCP test</title><p>Done. You can close this tab and go back to the test.</p>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();

        if (query["error"] is { } error)
        {
            Add("Sign-in", "Consent in the browser", false, $"the app got error={error} (declined?)", watch.Elapsed.TotalMilliseconds, null);
            return false;
        }

        var stateOk = query["state"] == state && query["iss"] == Base;
        using var exchange = new FormUrlEncodedContent([
            new("grant_type", "authorization_code"), new("code", query["code"] ?? ""), new("client_id", clientId),
            new("redirect_uri", redirect), new("code_verifier", verifier), new("resource", Base + "/mcp/editor"),
        ]);
        var timer = Stopwatch.StartNew();
        using var response = await _http.PostAsync(new Uri(Base + "/oauth/token"), exchange);
        var json = await ReadJsonAsync(response);
        Add("Sign-in", "The code is exchanged for tokens (state and iss checked)", response.IsSuccessStatusCode && stateOk, stateOk ? "tokens issued" : "state or iss didn't match", timer.Elapsed.TotalMilliseconds, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        _access = json!["access_token"]!.GetValue<string>();
        SaveCache(ReadCache() with { RefreshToken = json["refresh_token"]!.GetValue<string>() });
        var allows = await ProbeAsync();
        var matches = allows == options.Consent;
        Add("Sign-in", "The consent matches the run", matches, $"the grant allows {Describe(allows)}", 0, 200);
        if (!matches)
        {
            Console.WriteLine($"\nYou chose {Describe(allows)}; this run needs {Describe(options.Consent)}. Run it again and choose the other option.");
        }

        return matches;
    }

    // Which consent a token has: set_status answers "drafts only" before it looks for the item.
    private async Task<string> ProbeAsync()
    {
        var (_, _, text) = await ToolAsync("/mcp/editor", "set_status", new JsonObject { ["path"] = "posts/mcp-test-probe.md", ["version"] = "x", ["status"] = "draft" }, _access, record: false);
        return text.Contains("drafts only", StringComparison.Ordinal) ? "drafts" : "publish";
    }

    private static string Describe(string consent) => consent == "publish" ? "drafts and publishing" : "drafts only";

    private async Task<(int Status, JsonNode? Json)> RefreshAsync(string? refresh)
    {
        using var form = new FormUrlEncodedContent([new("grant_type", "refresh_token"), new("refresh_token", refresh ?? ""), new("client_id", ReadCache().ClientId ?? "")]);
        using var response = await _http.PostAsync(new Uri(Base + "/oauth/token"), form);
        return ((int)response.StatusCode, await ReadJsonAsync(response));
    }

    private async Task StepAsync(string group, string name, Func<Task<(bool Ok, string Detail, int? Status)>> step)
    {
        var watch = Stopwatch.StartNew();
        _lastCallMs = null;
        try
        {
            var (ok, detail, status) = await step();
            Add(group, name, ok, detail, _lastCallMs ?? watch.Elapsed.TotalMilliseconds, status);
        }
        catch (Exception ex)
        {
            Add(group, name, false, $"{ex.GetType().Name}: {ex.Message}", watch.Elapsed.TotalMilliseconds, null);
        }
    }

    private double? _lastCallMs;

    private void Add(string group, string name, bool ok, string detail, double ms, int? status)
    {
        _results.Add(new Result(group, name, ok, detail, Math.Round(ms, 1), status));
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {ms,7:0} ms  {group}: {name} ({detail})");
    }

    private async Task<(int Status, bool IsError, string Text)> ToolAsync(string path, string tool, JsonObject arguments, string? token, bool record = true)
    {
        var (status, message) = await RpcAsync(path, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, token, record);
        var result = message?["result"];
        var text = string.Concat((result?["content"]?.AsArray() ?? []).Select(c => c?["text"]?.GetValue<string>()));
        var isError = result?["isError"]?.GetValue<bool>() == true || message?["error"] is not null;
        return (status, isError, text.Length > 0 ? text : message?["error"]?["message"]?.GetValue<string>() ?? "");
    }

    private async Task<(int Status, JsonNode? Message)> RpcAsync(string path, string method, JsonNode @params, string? token, bool record = true)
    {
        using var response = await PostRpcRawAsync(path, method, @params, token);
        var body = await response.Content.ReadAsStringAsync();
        if (record)
        {
            // The attempt that got through, without any wait for the rate limit.
            _lastCallMs = _lastAttemptMs;
        }

        // Streamable HTTP answers with JSON or a short event stream whose data line is the message.
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).LastOrDefault()
            : body;
        return ((int)response.StatusCode, string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json));
    }

    private double _lastAttemptMs;

    // A 429 from the site's rate limit is waited out (Retry-After) and the call made again, up to
    // three times, so a run never stops halfway with a post left public (F-0068).
    private async Task<HttpResponseMessage> PostRpcRawAsync(string path, string method, JsonNode @params, string? token)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = @params };
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base + path))
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
            if (token is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var watch = Stopwatch.StartNew();
            var response = await _http.SendAsync(request);
            _lastAttemptMs = watch.Elapsed.TotalMilliseconds;
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 3)
            {
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            response.Dispose();
            _rateLimitWaits++;
            Console.WriteLine($"  rate limited on {path}: waiting {wait.TotalSeconds:0} s");
            await Task.Delay(wait + TimeSpan.FromSeconds(1));
        }
    }

    private int _rateLimitWaits;

    private static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "weesite-mcp-test", ["version"] = "1" },
    };

    private async Task<(int Status, JsonNode? Json)> GetJsonAsync(string path)
    {
        var watch = Stopwatch.StartNew();
        using var response = await _http.GetAsync(new Uri(Base + path));
        var json = await ReadJsonAsync(response);
        _lastCallMs = watch.Elapsed.TotalMilliseconds;
        return ((int)response.StatusCode, json);
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Post(string title, string slug, string extra = "", string body = "A test post from the MCP test skill. It's archived at the end of a run.") =>
        $"---\ntitle: {title}\nslug: {slug}\ndate: {DateTime.UtcNow:yyyy-MM-dd}\ntags:\n- notes\n{extra}---\n\n{body}\n";

    private static string? Value(string json, string name)
    {
        try
        {
            return JsonNode.Parse(json)?[name]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string First(string text) => text.Length > 120 ? text[..120] + "…" : text;

    private Cache ReadCache()
    {
        try
        {
            return File.Exists(_cachePath) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(_cachePath)) ?? new Cache(null, null) : new Cache(null, null);
        }
        catch (JsonException)
        {
            return new Cache(null, null);
        }
    }

    // The refresh token is a credential: the file is the user's only.
    private void SaveCache(Cache cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_cachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void TryOpen(string url)
    {
        foreach (var opener in new[] { "wslview", "xdg-open", "open" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(opener, url) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Write()
    {
        var report = new
        {
            site = Base,
            siteName = _siteName,
            consent = options.Consent,
            lifecycle = options.Lifecycle,
            at = DateTimeOffset.UtcNow,
            passed = _results.Count(r => r.Passed),
            failed = _results.Count(r => !r.Passed),
            rateLimitWaits = _rateLimitWaits,
            results = _results,
        };
        File.WriteAllText(options.Report, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"\n{report.passed} passed, {report.failed} failed, {report.rateLimitWaits} waits for the rate limit. Results: {options.Report}");
    }
}
