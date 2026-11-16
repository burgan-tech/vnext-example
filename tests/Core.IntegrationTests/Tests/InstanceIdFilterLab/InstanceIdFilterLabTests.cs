using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.InstanceIdFilterLab;

/// <summary>
/// instance-id-filter-lab (vnext#1080): filtering the instance list by the uuid <c>id</c> column.
/// <para>
/// Before the fix the column builder bound the operand as text, so every <c>id</c> filter answered 500
/// (PostgreSQL 42883, <c>uuid = text</c>) — the TTS team could not look an instance up by the id they held.
/// The fix binds the operand as <c>uuid</c>; <c>eq</c>/<c>ne</c>/<c>in</c>/<c>nin</c> match, a non-Guid value and
/// the range / text-match operators are the caller's error (400 <c>Validation:900011</c>).
/// </para>
/// <para>
/// Each test seeds its own three <c>idf-target</c> instances under a fresh <c>testId</c>, and every negative or
/// set filter is ANDed with that <c>testId</c>, so a test sees only its own rows on a shared database. The
/// GetInstances half runs the same filters through <c>idf-reader</c>'s <c>SetFilter</c> (the issue's fourth
/// acceptance criterion).
/// </para>
/// </summary>
public class InstanceIdFilterLabTests : WorkflowTestBase
{
    private const string Target = "idf-target";
    private const string Reader = "idf-reader";
    private const string FilterInvalid = "Validation:900011";

    public InstanceIdFilterLabTests(VNextTestEnvironment environment) : base(environment) { }

    // ── list endpoint ────────────────────────────────────────────────────────

    /// <summary>Acceptance 1: <c>{"id":{"eq":"&lt;existing id&gt;"}}</c> → 200 and exactly that instance.</summary>
    [Fact]
    public async Task List_IdEq_ReturnsThatInstance()
    {
        var seed = await SeedAsync();

        var ids = await ListIdsAsync(Json(new { id = new { eq = seed.A } }));

        Assert.Equal([seed.A], ids);
    }

    /// <summary>Acceptance 2: <c>{"id":{"in":["&lt;id1&gt;","&lt;id2&gt;"]}}</c> → 200 and both instances.</summary>
    [Fact]
    public async Task List_IdIn_ReturnsBothInstances()
    {
        var seed = await SeedAsync();

        var ids = await ListIdsAsync(Json(new { id = new { @in = new[] { seed.A, seed.B } } }));

        Assert.Equal(Sorted(seed.A, seed.B), ids);
    }

    /// <summary><c>ne</c> and <c>nin</c> exclude by uuid, inside this test's own population.</summary>
    [Fact]
    public async Task List_IdNeAndNin_ExcludeByUuid()
    {
        var seed = await SeedAsync();

        var ne = await ListIdsAsync(Json(new
        {
            and = new object[] { OwnRows(seed.TestId), new { id = new { ne = seed.A } } }
        }));
        Assert.Equal(Sorted(seed.B, seed.C), ne);

        var nin = await ListIdsAsync(Json(new
        {
            and = new object[] { OwnRows(seed.TestId), new { id = new { nin = new[] { seed.A, seed.B } } } }
        }));
        Assert.Equal([seed.C], nin);
    }

    /// <summary>
    /// Acceptance 3: a value that is not a Guid is the caller's error — 400 with the filter code, never 500. Also for
    /// one bad entry inside an <c>in</c> list.
    /// </summary>
    [Theory]
    [InlineData("""{"id":{"eq":"not-an-id"}}""")]
    [InlineData("""{"id":{"in":["00000000-0000-0000-0000-000000000001","kimlik-degil"]}}""")]
    public async Task List_IdNotAGuid_Answers400(string filter)
    {
        var (status, body) = await ListAsync(filter);

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400, got {(int)status}: {body}");
        Assert.Contains(FilterInvalid, body);
    }

    /// <summary>Ordering and text-matching a uuid mean nothing: rejected with 400, not run as text (500 before).</summary>
    [Theory]
    [InlineData("gt")]
    [InlineData("startswith")]
    public async Task List_IdUnsupportedOperator_Answers400(string op)
    {
        var (status, body) = await ListAsync($$$"""{"id":{"{{{op}}}":"00000000-0000-0000-0000-000000000001"}}""");

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400, got {(int)status}: {body}");
        Assert.Contains(FilterInvalid, body);
    }

    // ── GetInstances task (SetFilter) ────────────────────────────────────────

    /// <summary>Acceptance 4: the same <c>in</c> filter from a GetInstances task finds both instances.</summary>
    [Fact]
    public async Task Task_SetFilterIdIn_FindsBothInstances()
    {
        var seed = await SeedAsync();

        var list = await ReadThroughTaskAsync(Json(new { id = new { @in = new[] { seed.A, seed.B } } }));

        Assert.True(list.GetProperty("isSuccess").GetBoolean(), $"GetInstances failed: {list}");
        var ids = list.GetProperty("ids").EnumerateArray().Select(e => e.GetString()!).OrderBy(x => x).ToList();
        Assert.Equal(Sorted(seed.A, seed.B), ids);
    }

    /// <summary>
    /// A non-Guid id through the task: the read fails and is recorded as a client error (400), the same
    /// classification the list endpoint gives — not a server error.
    /// </summary>
    [Fact]
    public async Task Task_SetFilterIdNotAGuid_FailsAsClientError()
    {
        var list = await ReadThroughTaskAsync("""{"id":{"eq":"not-an-id"}}""");

        Assert.False(list.GetProperty("isSuccess").GetBoolean(), $"expected the read to fail: {list}");
        Assert.Equal(400, list.GetProperty("statusCode").GetInt32());
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private sealed record Seed(string TestId, string A, string B, string C);

    private async Task<Seed> SeedAsync()
    {
        var testId = $"idf-{Guid.NewGuid():N}";
        var ids = new List<string>();
        foreach (var suffix in new[] { "a", "b", "c" })
            ids.Add(await StartAsync(Target, new { key = $"{testId}-{suffix}", attributes = new { testId } }));
        return new Seed(testId, ids[0], ids[1], ids[2]);
    }

    private static object OwnRows(string testId) => new { attributes = new { testId = new { eq = testId } } };

    private static string Json(object filter) => JsonSerializer.Serialize(filter);

    private static List<string> Sorted(params string[] ids) => ids.OrderBy(x => x).ToList();

    private Task<(HttpStatusCode Status, string Body)> ListAsync(string filter) =>
        SendRawAsync(HttpMethod.Get,
            $"api/v1/{TestDomain.Name}/workflows/{Target}/instances?pageSize=50&filter={Uri.EscapeDataString(filter)}",
            headers: Headers());

    private async Task<List<string>> ListIdsAsync(string filter)
    {
        var (status, body) = await ListAsync(filter);
        Assert.True(status == HttpStatusCode.OK, $"filter {filter} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!)
            .OrderBy(x => x)
            .ToList();
    }

    /// <summary>Runs <c>idf-reader</c>'s <c>read-list</c> with the filter and returns its <c>readerList</c> record.</summary>
    private async Task<JsonElement> ReadThroughTaskAsync(string filter)
    {
        var readerId = await StartAsync(Reader, new { attributes = new { purpose = "vnext#1080" } });
        await RunAcceptedAsync(Reader, readerId, "read-list",
            new { targetDomain = TestDomain.Name, targetFlow = Target, filter });
        await AssertNotFaultedAsync(Reader, readerId);

        var attributes = await GetAttributesAsync(Reader, readerId);
        Assert.True(attributes.TryGetProperty("readerList", out var list),
            $"idf-reader recorded no readerList — {await DescribeAsync(Reader, readerId)}");
        return list;
    }
}
