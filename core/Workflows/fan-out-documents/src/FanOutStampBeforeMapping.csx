using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// First half of the single-write instrument. Runs as onEntry order 1 of
/// <c>documents-processing</c> — the task immediately BEFORE the fan-out batch, inside the same
/// state entry, with no transition, no state change and nothing else in between.
/// <para>
/// <b>Why this task exists at all.</b> It did not, originally. The fan-out mapping's
/// <c>OutputHandler</c> used to stamp the pre-batch version itself, because
/// <c>IFanOutMapping.OutputHandler</c> was an abstract member and the scenario had to hand-write
/// one anyway. It is optional now (default-interface implementation returning <c>null</c> ⇒ the
/// runtime's own default packaging), and the scenario deliberately stopped overriding it so that
/// the fallback is what the tests actually exercise. The default packaging emits exactly
/// <c>{resultKey}</c> and <c>{resultKey}Summary</c> and cannot carry a scenario's instrumentation,
/// so the version mark moved out of the batch and into this task.
/// </para>
/// <para>
/// <b>The arithmetic, and why it is still exact.</b> Reads
/// <c>Instance.LatestData.Version</c> BEFORE its own output is applied, so
/// <c>versionBeforeFanOutBatch</c> names the row this task is about to supersede. Under the
/// immediate-persist InstanceData model each task result is one patch, so:
/// </para>
/// <code>
///   V             = versionBeforeFanOutBatch   (what this task saw)
///   V + 1         = this task's own write       (exactly one, it is one task result)
///   V + 1 + K     = versionAfterFanOut          (what the next task saw), K = writes the BATCH made
/// </code>
/// <para>
/// so <c>patch(versionAfterFanOut) - patch(versionBeforeFanOutBatch) == 2</c> ⟺ <c>K == 1</c>.
/// A batch that wrote per item would make that delta <c>1 + N</c>. The known <c>+1</c> is not
/// assumed — the test probes <c>V+1</c> on the data endpoint and requires it to resolve, so the
/// constant is verified rather than trusted.
/// </para>
/// <para>
/// <b>Do not insert anything between orders 1, 2 and 3 of this state.</b> Any intervening task,
/// transition or state change silently widens the delta and the assertion keeps "passing" while
/// auditing nothing.
/// </para>
/// </summary>
public class FanOutStampBeforeMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;

        var version = context.Instance?.LatestData?.Version ?? string.Empty;

        target["versionBeforeFanOutBatch"] = version;
        target["batchArmedAtUtc"] = DateTime.UtcNow.ToString("o");

        LogInformation($"FanOutStampBeforeMapping: versionBeforeFanOutBatch={version}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
