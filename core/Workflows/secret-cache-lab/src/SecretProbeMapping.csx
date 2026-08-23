using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// secret-cache-lab probe — reads the SAME secret several times in a row through
/// <c>ScriptBase.GetSecretAsync</c> and records, per read, how long the read took.
/// <para>
/// What it proves. The first read of a TTL window pays a Dapr → Vault round trip
/// (hundreds/thousands of microseconds); every later read inside the window is served from the
/// in-process <c>ScriptSecretCache</c> bundle and costs a dictionary lookup (single-digit
/// microseconds). The gap between <c>microsPerRead[0]</c> and <c>microsPerRead[1..]</c> is the
/// observable signature of the cache.
/// </para>
/// <para>
/// It also stamps <c>secretValue</c>, so rotating the value in Vault and re-probing shows the
/// staleness window: the old value keeps coming back until the bundle's TTL
/// (<c>Scripting:SecretCache:TtlSeconds</c>, default 30) expires, then the fresh value appears.
/// </para>
/// <para>
/// Timing uses <c>DateTime.UtcNow.Ticks</c> (100 ns units) rather than <c>Stopwatch</c> so the
/// script stays inside the scripting sandbox's allowed assembly set.
/// </para>
/// </summary>
public class SecretProbeMapping : ScriptBase, IMapping
{
    private const string StoreName = "vnext-secret";
    private const string SecretStore = "workflow-secret";
    private const string SecretKey = "ApiSecret";
    private const int SampleCount = 3;

    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var instanceData = context.Instance?.Data as IDictionary<string, object>;

        var round = 0;
        if (instanceData != null && instanceData.TryGetValue("probeRound", out var rawRound) && rawRound != null)
        {
            int.TryParse(rawRound.ToString(), out round);
        }
        round++;

        var micros = new List<object>();
        var value = string.Empty;

        for (var i = 0; i < SampleCount; i++)
        {
            var startedTicks = DateTime.UtcNow.Ticks;
            value = await GetSecretAsync(StoreName, SecretStore, SecretKey);
            micros.Add(Math.Round((DateTime.UtcNow.Ticks - startedTicks) / 10.0, 1));
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["probeRound"] = round;
        target["secretValue"] = value;
        target["readAtUtc"] = DateTime.UtcNow.ToString("O");
        target["microsPerRead"] = micros;

        LogInformation($"SecretProbeMapping: round {round}, value '{value}', microsPerRead [{string.Join(", ", micros)}]");
        return new ScriptResponse { Data = result };
    }
}
