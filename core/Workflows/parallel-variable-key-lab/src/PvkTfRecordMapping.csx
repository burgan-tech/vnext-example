using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Last order of pvk-trigger-family / pvk-trigger-mix: reads every slot the two parallel groups filled and records, per slot,
/// whether its task succeeded. No error boundary is defined on purpose — a failed task does not fault the
/// instance, so this record is the only place the failure is visible, exactly as in the field report.
/// </summary>
public class PvkTfRecordMapping : ScriptBase, IMapping
{
    private static readonly string[] Slots =
    {
        "list1", "list2", "list3", "list4",
        "get1", "get2", "data1", "data2", "start1", "start2", "sub1", "sub2", "trig1", "trig2"
    };

    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var failed = new List<string>();
        var slots = new Dictionary<string, object>();
        // pvk-trigger-mix runs the six-type group alone: it has no list slots to account for.
        var mixOnly = context.Instance?.Data is IDictionary<string, object> data
            && data.TryGetValue("mixOnly", out var flag) && string.Equals(flag?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var expected = mixOnly ? Slots.Where(s => !s.StartsWith("list")).ToArray() : Slots;
        foreach (var slot in expected)
        {
            context.TaskResponse.TryGetValue(slot, out var response);
            bool ok = false; object status = null; object error = null; object count = null;
            if (response != null)
            {
                try { ok = response.isSuccess == true; } catch { ok = false; }
                try { status = response.statusCode; } catch { }
                try { error = response.errorMessage; } catch { }
                if (slot.StartsWith("list"))
                {
                    try
                    {
                        object items = response.data?.items;
                        count = items is System.Collections.ICollection c ? c.Count : null;
                    }
                    catch { }
                }
            }
            if (!ok) failed.Add($"{slot}:{status ?? "no-response"}:{error}");
            slots[slot] = new { ok, statusCode = status, error = error?.ToString(), count };
        }

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                family = new
                {
                    recorded = true,
                    okCount = expected.Length - failed.Count,
                    slotCount = expected.Length,
                    failed,
                    slots
                }
            }
        });
    }
}
