using System;

namespace Acme.Helpers;

/// <summary>
/// Global helper for the script-race-lab fixture.
/// <para>
/// Its body is irrelevant; its existence is the point. A workflow that declares
/// <c>scripts.helpers</c> makes every script it compiles share the helper set's
/// singleton-lifetime AssemblyLoadContext, and a shared context cannot hold two assemblies with
/// the same simple name — which is the collision the fixture reproduces.
/// </para>
/// </summary>
public static class RaceHelper
{
    /// <summary>Deterministic stamp, so a test can assert the helper really resolved.</summary>
    public static string Stamp(string testId) => "race:" + (testId ?? "none");
}
