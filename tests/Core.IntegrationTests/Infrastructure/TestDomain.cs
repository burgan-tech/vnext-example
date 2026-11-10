namespace Core.IntegrationTests.Infrastructure;

/// <summary>
/// The domain the suite publishes to and addresses: <c>VNEXT_DOMAIN</c> when set (environment
/// variable or <c>test.runsettings.local</c>), else <c>core</c>.
/// <para>
/// Lets a scenario run against a runtime brought up as a side domain
/// (<c>run-docker.sh up &lt;domain&gt; --offset N</c>) beside another checkout's <c>core</c>, which shares
/// the infrastructure: a second <c>core</c> would share Redis keys and event topics with it, a
/// differently named domain does not. The SDK publisher rewrites every component's <c>domain</c> to
/// this value. Suites that still spell <c>core</c> in their own URLs only run against <c>core</c>.
/// </para>
/// </summary>
public static class TestDomain
{
    public static string Name { get; } =
        Environment.GetEnvironmentVariable("VNEXT_DOMAIN") is { } d && !string.IsNullOrWhiteSpace(d)
            ? d.Trim()
            : "core";
}
