using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// <c>IFanOutMapping</c> for the fan-out-documents batch (TaskType 21, inline mode).
/// <para>
/// <b>Why a mapping is required here at all.</b> The zero-script path only sets the per-item
/// branch context's <c>Body</c>, and an <c>HttpTask</c> does not read its URL from the script
/// body — the URL lives on the cloned task instance. Any inner task whose OWN config has to
/// change per item (an HttpTask's url, a SoapTask's envelope, a DaprServiceTask's method)
/// therefore needs an <c>ItemInputHandler</c> that mutates the clone directly. That is what this
/// mapping exists for.
/// </para>
/// <para>
/// <b>Purity.</b> <c>ItemInputHandler</c> runs N times concurrently, each on its own discarded
/// branch context and its own DI scope. It must be pure with respect to instance data — it
/// writes nothing. <c>OutputHandler</c> is the batch's ONLY write point, and the whole design
/// exists to keep it that way: one fan-out execution ⇒ one InstanceData patch, whatever N is.
/// </para>
/// <para>
/// <b>The output shape deliberately mirrors the runtime's DEFAULT packaging</b>
/// (<c>{resultKey}</c> + <c>{resultKey}Summary{total,succeeded,failed,timedOut}</c>), because
/// supplying any mapping opts the task out of that default — <c>IFanOutMapping.OutputHandler</c>
/// has no default implementation. Mirroring it keeps the scenario's assertions valid against the
/// documented default contract even though we had to hand-write the handler. Two extra keys are
/// scenario instrumentation, not part of the default shape: <c>failedDocumentIds</c> and
/// <c>versionSeenByFanOut</c>.
/// </para>
/// </summary>
public class FanOutDocumentsMapping : ScriptBase, IFanOutMapping
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
            // Our documents are { id, url }, so ItemKey IS the document id — no dynamic digging.
            var documentId = item.ItemKey ?? item.Index.ToString();

            // DOC-SLOW ids go to MockLab's delayed route. MockLab can only express delayMs on a
            // MOCK, never on a rule, so a deliberate straggler has to be a different route — and
            // the load test needs one, because a batch's wall clock is set by its slowest item.
            if (documentId.StartsWith("DOC-SLOW", StringComparison.Ordinal))
            {
                url = url.Replace("/documents/process", "/documents/process-slow");
            }

            httpTask.SetUrl(url + "?documentId=" + Uri.EscapeDataString(documentId));
        }

        // Audit data only — never merged into instance data.
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result)
    {
        dynamic response = new ExpandoObject();
        var target = (IDictionary<string, object>)response;

        var rows = new List<object>();
        var failedIds = new List<string>();

        foreach (var item in result.Items)
        {
            rows.Add(new Dictionary<string, object>
            {
                ["index"] = item.Index,
                ["itemKey"] = item.ItemKey,
                ["isSuccess"] = item.IsSuccess,
                ["data"] = item.Data,
                ["errorCode"] = item.ErrorCode,
                ["errorMessage"] = item.ErrorMessage,
                ["durationMs"] = (long)item.Duration.TotalMilliseconds
            });

            if (!item.IsSuccess)
            {
                failedIds.Add(item.ItemKey);
            }
        }

        target["documentResults"] = rows;
        target["documentResultsSummary"] = new Dictionary<string, object>
        {
            ["total"] = result.Total,
            ["succeeded"] = result.Succeeded,
            ["failed"] = result.Failed,
            ["timedOut"] = result.TimedOut
        };

        // Flat mirror of the summary counters. The auto-transition rules read the nested summary
        // first and fall back to these: a snapshot's nested object can surface as a dictionary or
        // as a JsonElement depending on how it was persisted, and a branching rule must not be the
        // place that discovers which.
        target["documentsFailedCount"] = result.Failed;
        target["documentsSucceededCount"] = result.Succeeded;
        target["failedDocumentIds"] = failedIds;

        // Single-write instrument: the version this ONE batch write is about to supersede.
        target["versionSeenByFanOut"] = context.Instance?.LatestData?.Version ?? string.Empty;

        LogInformation(
            $"FanOutDocumentsMapping: total={result.Total} succeeded={result.Succeeded} " +
            $"failed={result.Failed} timedOut={result.TimedOut} versionSeenByFanOut={target["versionSeenByFanOut"]}");

        return Task.FromResult(new ScriptResponse { Data = response });
    }
}
