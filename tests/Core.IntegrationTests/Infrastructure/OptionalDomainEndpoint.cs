using System.Collections.Concurrent;
using System.Text.Json;

namespace Core.IntegrationTests.Infrastructure;

/// <summary>
/// Resolves the base url of an OPTIONAL second runtime (partner, credit, discovery) that some suites
/// need, and reports it only when that runtime is actually up and is the domain the suite expects.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: <c>test.runsettings</c> commits the cross-domain lab urls
/// (<c>VNEXT_PARTNER_BASE_URL</c>, <c>VNEXT_CREDIT_BASE_URL</c>, <c>VNEXT_DISCOVERY_BASE_URL</c>), so
/// "the variable is set" was always true and the suites' <c>Skip.If(url is null)</c> never fired. On a
/// core-only stack every cross-domain test then ran into a 60–120 s wait and failed — about 25 reds
/// and 30 minutes per full run that said nothing about the runtime.
/// </para>
/// <para>
/// The probe reads <c>GET {url}/health</c> once per url per test run (2 s budget) and checks the
/// <c>domain</c> field the runtime reports. The domain check matters: <c>run-docker.sh up</c> and
/// <c>labs/cross-domain/lab.sh</c> assign different offsets (run-docker put <c>discovery</c> on
/// offset 20, the lab puts <c>credit</c> there), so a live port can belong to the wrong domain.
/// </para>
/// </remarks>
public static class OptionalDomainEndpoint
{
    private static readonly ConcurrentDictionary<string, Lazy<string?>> Probes = new();

    /// <summary>
    /// The url from <paramref name="variable"/> when it is set, reachable and answers
    /// <c>/health</c> for <paramref name="expectedDomain"/>; otherwise <c>null</c> (and a one-line
    /// reason on the console, so a skipped suite says why).
    /// </summary>
    public static string? Resolve(string variable, string expectedDomain)
    {
        var url = Environment.GetEnvironmentVariable(variable)?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url)) return null;

        return Probes.GetOrAdd($"{url}|{expectedDomain}", _ => new Lazy<string?>(() => Probe(variable, url, expectedDomain))).Value;
    }

    private static string? Probe(string variable, string url, string expectedDomain)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = client.GetAsync(url + "/health").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[optional-domain] {variable}={url} answered /health {(int)response.StatusCode} — dependent tests skip");
                return null;
            }

            using var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var domain = body.RootElement.TryGetProperty("domain", out var d) ? d.GetString() : null;
            if (domain is not null && !string.Equals(domain, expectedDomain, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[optional-domain] {variable}={url} is domain '{domain}', not '{expectedDomain}' — dependent tests skip");
                return null;
            }

            return url;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"[optional-domain] {variable}={url} unreachable ({ex.GetType().Name}) — dependent tests skip");
            return null;
        }
    }
}
