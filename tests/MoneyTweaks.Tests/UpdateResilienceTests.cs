using System.Net;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Tests;

/// <summary>The update check survives GitHub API outages (retries, github.com fallback) and reports them honestly.</summary>
[Collection("update-http")]
public sealed class UpdateResilienceTests : IDisposable
{
    private readonly HttpClient _http = UpdateService.Http, _noRedirect = UpdateService.NoRedirect;
    private readonly TimeSpan[] _delays = UpdateService.RetryDelays;
    private readonly TimeSpan _timeout = UpdateService.CheckTimeout;
    private readonly FakeGitHub _gh = new();

    private const string Api = "https://api.github.com/repos/MoneyMan82/money-tweaks/releases?per_page=30";
    private const string Latest = "https://github.com/MoneyMan82/money-tweaks/releases/latest";
    private const string Dl = "https://github.com/MoneyMan82/money-tweaks/releases/download/v1.80.0/";
    private static readonly string Sums =
        new string('a', 64) + "  MoneyTweaks-Setup.exe\n" +
        new string('b', 64) + " *MoneyTweaks-1.80.0-win-x64.exe\n" +
        new string('c', 64) + "  MoneyTweaks-Rescue.exe\n" +
        new string('d', 64) + "  ../evil.exe\n";

    public UpdateResilienceTests()
    {
        UpdateService.Http = UpdateService.CreateClient(_gh);
        UpdateService.NoRedirect = UpdateService.CreateClient(_gh);
        UpdateService.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        UpdateService.CheckTimeout = TimeSpan.FromSeconds(2);
    }

    public void Dispose()
    {
        UpdateService.Http = _http; UpdateService.NoRedirect = _noRedirect;
        UpdateService.RetryDelays = _delays; UpdateService.CheckTimeout = _timeout;
    }

    private static string ApiJson(string tag) => $$"""
        [{"tag_name":"{{tag}}","draft":false,"prerelease":false,"body":"notes","html_url":"https://github.com/MoneyMan82/money-tweaks/releases/tag/{{tag}}",
          "assets":[{"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/MoneyMan82/money-tweaks/releases/download/{{tag}}/SHA256SUMS.txt"},
                    {"name":"MoneyTweaks-Setup.exe","browser_download_url":"https://github.com/MoneyMan82/money-tweaks/releases/download/{{tag}}/MoneyTweaks-Setup.exe"}]}]
        """;

    [Fact]
    public async Task Api_server_errors_are_retried_and_then_succeed()
    {
        _gh.On(Api, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout, ApiJson("v1.80.0"));
        var u = await UpdateService.CheckAsync(new Version(1, 79, 0));
        Assert.NotNull(u);
        Assert.Equal("v1.80.0", u!.Tag);
        Assert.Equal(3, _gh.Count(Api));
        Assert.Equal(0, _gh.Count(Latest));
    }

    [Fact]
    public async Task Api_down_falls_back_to_the_release_page_and_checksum_list()
    {
        _gh.On(Api, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        _gh.Redirect(Latest, "https://github.com/MoneyMan82/money-tweaks/releases/tag/v1.80.0");
        _gh.On(Dl + "SHA256SUMS.txt", Sums);

        var u = await UpdateService.CheckAsync(new Version(1, 79, 0));

        Assert.NotNull(u);
        Assert.Equal(new Version(1, 80, 0), u!.Version);
        Assert.Equal(Dl + "MoneyTweaks-Setup.exe", u.SetupUrl);
        Assert.Equal(Dl + "MoneyTweaks-1.80.0-win-x64.exe", u.PortableUrl);   // never the Rescue exe or a path-like name
        Assert.Equal(Dl + "SHA256SUMS.txt", u.SumsUrl);
        Assert.True(UpdateService.IsTrustedDownloadUrl(u.SetupUrl) && UpdateService.IsTrustedDownloadUrl(u.PortableUrl));
        Assert.True(UpdateService.IsTrustedPageUrl(u.HtmlUrl));
    }

    [Fact]
    public async Task Fallback_reports_up_to_date_when_latest_is_not_newer()
    {
        _gh.On(Api, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway);
        _gh.Redirect(Latest, "/MoneyMan82/money-tweaks/releases/tag/v1.79.0");   // relative Location
        Assert.Null(await UpdateService.CheckAsync(new Version(1, 79, 0)));
    }

    [Fact]
    public async Task Fallback_rejects_a_redirect_to_another_repository()
    {
        _gh.On(Api, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        _gh.Redirect(Latest, "https://github.com/someone/else/releases/tag/v9.9.9");
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => UpdateService.CheckAsync(new Version(1, 79, 0)));
        Assert.True(ex.Transient);
    }

    [Fact]
    public async Task Both_routes_down_is_a_transient_github_problem_with_the_status_code()
    {
        _gh.On(Api, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        _gh.On(Latest, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => UpdateService.CheckAsync(new Version(1, 79, 0)));
        Assert.True(ex.Transient);
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task A_hanging_request_times_out_instead_of_waiting_minutes()
    {
        UpdateService.CheckTimeout = TimeSpan.FromMilliseconds(200);
        _gh.Hang(Api); _gh.Hang(Latest);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => UpdateService.CheckAsync(new Version(1, 79, 0)));
        Assert.True(ex.Transient);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task Rate_limit_is_described_and_retried()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
        limited.Headers.Add("X-RateLimit-Remaining", "0");
        limited.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString());
        Assert.Contains("limiting update checks", UpdateService.DescribeStatus(limited));
        _gh.On(Api, HttpStatusCode.TooManyRequests, ApiJson("v1.80.0"));
        Assert.Equal("v1.80.0", (await UpdateService.CheckAsync(new Version(1, 79, 0)))!.Tag);
    }

    [Theory]
    [InlineData("https://github.com/MoneyMan82/money-tweaks/releases/tag/v1.80.0", "v1.80.0")]
    [InlineData("https://github.com/MoneyMan82/money-tweaks/releases/tag/1.80", "1.80")]
    [InlineData("https://github.com/MoneyMan82/money-tweaks/releases/tag/v1.80.0%2F..%2F..%2Fx", null)]
    [InlineData("https://github.com/MoneyMan82/money-tweaks/releases", null)]
    [InlineData("http://github.com/MoneyMan82/money-tweaks/releases/tag/v1.80.0", null)]
    [InlineData("https://evil.example/MoneyMan82/money-tweaks/releases/tag/v1.80.0", null)]
    public void Tag_is_only_taken_from_this_repositorys_release_page(string url, string? expected) =>
        Assert.Equal(expected, UpdateService.TagFromReleaseUrl(new Uri(url)));

    /// <summary>Scripted responses per URL; the last one repeats.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Func<HttpResponseMessage?>>> _routes = new();
        private readonly Dictionary<string, int> _counts = new();

        public void On(string url, params object[] responses)
        {
            var q = new Queue<Func<HttpResponseMessage?>>();
            foreach (var r in responses)
                q.Enqueue(r switch
                {
                    HttpStatusCode c => () => new HttpResponseMessage(c),
                    string body => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) },
                    _ => throw new ArgumentException("bad response"),
                });
            _routes[url] = q;
        }

        public void Redirect(string url, string location) =>
            _routes[url] = new Queue<Func<HttpResponseMessage?>>([() =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return r;
            }]);

        public void Hang(string url) => _routes[url] = new Queue<Func<HttpResponseMessage?>>([() => null]);

        public int Count(string url) => _counts.GetValueOrDefault(url);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (_counts) _counts[url] = Count(url) + 1;
            if (!_routes.TryGetValue(url, out var q)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var next = q.Count > 1 ? q.Dequeue() : q.Peek();
            var resp = next();
            if (resp is null) { await Task.Delay(Timeout.Infinite, ct); throw new OperationCanceledException(ct); }
            resp.RequestMessage = request;
            return resp;
        }
    }
}
