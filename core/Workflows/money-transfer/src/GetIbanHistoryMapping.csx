using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Filtering;

// Queries prior money-transfer instances for the same targetIban (GetInstances task, type 15).
// InputHandler builds the targetIban filter; OutputHandler writes isFirstTransfer based on the
// number of returned instances (0 prior transfers => first transfer => push required).
public class GetIbanHistoryMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var getInstancesTask = task as GetInstancesTask;
            if (getInstancesTask == null)
                throw new InvalidOperationException("Task must be a GetInstancesTask");

            var targetIban = (string)context.Instance?.Data?.targetIban ?? string.Empty;

            // Match prior instances whose attributes.targetIban equals the current targetIban.
            // Built with the fluent InstanceQuery + SetFilterSpec so the Filter/Sort wire strings
            // are always well-formed GraphQL-filter JSON (the previous SetFilter(string[]) call
            // resolved to the SetFilter(object) overload, serializing to a JSON array instead of
            // the expected filter object, and left Sort on the task's static legacy "-CreatedAt"
            // config value, which is not JSON either — both rejected by InstanceQueryValidator).
            var spec = InstanceQuery.Create()
                .Where("attributes.targetIban", f => f.Eq(targetIban))
                .OrderByDescending("createdAt")
                .Build();

            getInstancesTask.SetFilterSpec(spec);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "iban-history-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var statusCode = (int?)(context.Body?.statusCode) ?? 200;
            dynamic payload = context.Body?.data ?? context.Body;

            long count = 0;
            try
            {
                // Prefer an explicit total/count if the runtime provides one.
                var total = payload?.totalCount ?? payload?.total ?? payload?.count;
                if (total != null)
                {
                    count = (long)total;
                }
                else
                {
                    dynamic items = payload?.data ?? payload?.instances ?? payload;
                    var list = items as IEnumerable<object>;
                    count = list?.LongCount() ?? 0;
                }
            }
            catch
            {
                count = 0;
            }

            bool isFirst = count <= 0;

            return Task.FromResult(new ScriptResponse
            {
                Key = isFirst ? "iban-history-first-transfer" : "iban-history-known-iban",
                Data = new { isFirstTransfer = isFirst, priorTransferCount = count, statusCode = statusCode },
                Tags = new[] { "money-transfer", "history", isFirst ? "first-transfer" : "known-iban" }
            });
        }
        catch (Exception ex)
        {
            // On error fail safe: treat as first transfer so the push step still protects the user.
            return Task.FromResult(new ScriptResponse
            {
                Key = "iban-history-exception",
                Data = new { isFirstTransfer = true, error = ex.Message },
                Tags = new[] { "money-transfer", "history", "exception" }
            });
        }
    }
}
