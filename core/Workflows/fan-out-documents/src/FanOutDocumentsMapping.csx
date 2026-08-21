using System;
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
/// writes nothing. The batch's output step is its ONLY write point, and the whole design exists
/// to keep it that way: one fan-out execution ⇒ one InstanceData patch, whatever N is.
/// </para>
/// <para>
/// <b>This mapping deliberately does NOT override <c>OutputHandler</c>.</b> That is the point of
/// half this scenario. <c>IFanOutMapping.OutputHandler</c> carries a default implementation
/// returning <c>null</c>, which the executor reads as "not overridden" and answers with its own
/// <c>BuildDefaultOutput</c> — item rows under <c>join.resultKey</c> plus a
/// <c>{resultKey}Summary</c> of <c>{total, succeeded, failed, timedOut}</c>, the identical shape a
/// task shipping no mapping at all produces. An earlier revision of this file hand-wrote a handler
/// that reproduced that shape byte-for-byte, because the member used to be abstract; it is not any
/// more. Keeping the duplicate would have meant the scenario never exercised the fallback, and the
/// runtime's default packaging would go untested end-to-end. Every downstream assertion — the
/// branching rules, the summary counters, the item rows — now reads output the RUNTIME produced.
/// </para>
/// <para>
/// <b>Where the single-write instrument went.</b> The removed handler also stamped
/// <c>versionSeenByFanOut</c>. The default packaging cannot carry scenario instrumentation, so the
/// mark moved OUT of the batch and into its own onEntry task immediately before it
/// (<c>FanOutStampBeforeMapping</c>, order 1). See that file for the arithmetic.
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

    // NO OutputHandler — deliberately. See the class remarks: the runtime's default packaging
    // produces the exact shape this scenario asserts, and letting it do so is what proves the
    // IFanOutMapping default-interface fallback works end-to-end. Do not "restore" it.
}
