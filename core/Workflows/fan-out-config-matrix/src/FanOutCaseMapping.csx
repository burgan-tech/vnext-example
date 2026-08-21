using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// <c>IFanOutMapping</c> shared by EVERY case of the fan-out configurable-surface matrix
/// (TaskType 21, inline mode).
/// <para>
/// <b>One mapping, nine task components — deliberately.</b> Everything this matrix varies
/// (<c>join.policy</c>, <c>join.minSuccess</c>, <c>mode</c>, <c>execution.*</c>, per-item
/// <c>errorBoundary</c>) lives in the FanOut TASK component's own config, which is static per
/// component and cannot be supplied by the caller at runtime. So the config axis has to be one
/// component per variant. The mapping, by contrast, is identical for all of them: it only has to
/// tell each item which document it owns. Duplicating it per case would create nine files that
/// must not drift, so all nine task references in
/// <c>fan-out-config-matrix.json</c> point at this one.
/// </para>
/// <para>
/// <b>Why a mapping is required at all.</b> The zero-script path only sets the per-item branch
/// context's <c>Body</c>, and an <c>HttpTask</c> does not read its URL from the script body — the
/// URL lives on the cloned task instance. Telling item N which document it owns therefore needs an
/// <c>ItemInputHandler</c> that mutates the clone. Same reason as the sibling
/// <c>fan-out-documents</c> scenario; see that mapping's remarks.
/// </para>
/// <para>
/// <b>Purity.</b> <c>ItemInputHandler</c> runs N times concurrently, each on its own discarded
/// branch context and its own DI scope. It writes nothing to instance data — the batch's single
/// output step is the only write point.
/// </para>
/// <para>
/// <b>No <c>OutputHandler</c> — deliberately.</b> <c>IFanOutMapping.OutputHandler</c> carries a
/// default implementation returning <c>null</c>, which the executor reads as "not overridden" and
/// answers with its own <c>BuildDefaultOutput</c>: item rows under <c>join.resultKey</c>
/// (<c>caseResults</c> for every case here) plus <c>caseResultsSummary</c> of
/// <c>{total, succeeded, failed, timedOut}</c>. Every assertion in
/// <c>FanOutConfigMatrixTests</c> reads output the RUNTIME produced, which is the point: a
/// hand-written handler would make the join-policy assertions self-referential.
/// </para>
/// </summary>
public class FanOutCaseMapping : ScriptBase, IFanOutMapping
{
    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        var httpTask = task as HttpTask;
        if (httpTask != null)
        {
            // Base url is configuration-driven: task definitions ship the API_BASEURL
            // placeholder so the same component runs against any environment.
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            var url = httpTask.Url.Replace("API_BASEURL", apiBaseUrl);

            // FanOutItem.ItemKey is derived by the runtime (FanOutItemsResolver.ExtractItemKey):
            // the item object's `id` string property when present, else `key`, else the index.
            // The matrix feeds { id, url } objects, so ItemKey IS the document id.
            var documentId = item.ItemKey ?? item.Index.ToString();

            // The MockLab seed decides an item's fate from its id, so the CASE picks its item mix
            // and the mapping stays case-agnostic:
            //   DOC-*       -> 200, fast          (a success)
            //   DOC-FAIL*   -> 500, fast          (a deterministic failure, no timing games)
            //   DOC-SLOW*   -> 200 after 1500ms   (the straggler the timeout cases need)
            // delayMs is a MOCK-level field in MockLab and cannot be expressed on a rule, which is
            // why the straggler is a separate route rather than a rule on the default one.
            //
            // The straggler lives under a SIBLING segment (slow-documents/process), not as a suffix
            // of the fast route (documents/process-slow), because MockLab matches routes by PREFIX:
            // anything starting with "documents/process" is answered by the fast mock, so the old
            // suffix route was unreachable and the delay never applied. Do not "tidy" this back
            // into a suffix — it silently makes every timeout and concurrency assertion vacuous.
            if (documentId.StartsWith("DOC-SLOW", StringComparison.Ordinal))
            {
                url = url.Replace("/documents/process", "/slow-documents/process");
            }

            httpTask.SetUrl(url + "?documentId=" + Uri.EscapeDataString(documentId));
        }

        // Audit data only — never merged into instance data.
        return Task.FromResult(new ScriptResponse());
    }

    // NO OutputHandler — deliberately. See the class remarks.
}
