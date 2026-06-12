using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

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

            // Match prior instances whose data.targetIban equals the current targetIban.
            getInstancesTask.SetFilter(new[]
            {
                $"data.targetIban=={targetIban}"
            });

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
