using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Mapping for start-transfer-from-absence-entry StartTask (Type 11).
/// Conditionally starts a rezervation-transfer subflow when absenceType is personal-leave.
/// Determines transfer type: annual-leave (endDateTime present) or termination (endDateTime null).
/// </summary>
public class StartTransferFromAbsenceEntryMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var startTask = task as StartTask;
        if (startTask == null)
            throw new InvalidOperationException("Task must be a StartTask");

        var data = context.Instance?.Data;
        if (data == null)
            return Task.FromResult(new ScriptResponse { Data = new { skipped = true, reason = "no-instance-data" } });

        var absenceType = GetString(data, "absenceType");
        if (absenceType != "personal-leave")
            return Task.FromResult(new ScriptResponse { Data = new { skipped = true, reason = "not-personal-leave" } });

        var advisor = GetString(data, "advisor");
        if (string.IsNullOrEmpty(advisor))
            return Task.FromResult(new ScriptResponse { Data = new { skipped = true, reason = "no-advisor" } });

        var startDateTime = GetString(data, "startDateTime");
        if (string.IsNullOrEmpty(startDateTime))
            return Task.FromResult(new ScriptResponse { Data = new { skipped = true, reason = "no-startDateTime" } });

        var endDateTime = GetString(data, "endDateTime");

        var advisorType = ExtractAdvisorType(advisor);
        var transferType = string.IsNullOrEmpty(endDateTime) ? "termination" : "annual-leave";

        var safeStart = startDateTime.Replace(":", "-");
        var transferKey = $"transfer-leave-{advisor}-{safeStart}";

        startTask.SetDomain("touch");
        startTask.SetFlow("rezervation-transfer");
        startTask.SetKey(transferKey);

        var body = new System.Collections.Generic.Dictionary<string, object>
        {
            ["sourceAdvisorId"] = advisor,
            ["advisorType"] = advisorType,
            ["startDate"] = startDateTime
        };
        if (!string.IsNullOrEmpty(endDateTime))
            body["endDate"] = endDateTime;

        startTask.SetBody(body);

        return Task.FromResult(new ScriptResponse
        {
            Data = new { skipped = false, transferType, transferKey }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    private string ExtractAdvisorType(string advisor)
    {
        if (string.IsNullOrEmpty(advisor)) return null;
        var parts = advisor.Split('.');
        if (parts.Length >= 3)
            return parts[1];
        if (parts.Length == 2)
            return parts[0];
        return advisor;
    }

    private string GetString(dynamic obj, string name)
    {
        if (obj == null) return null;
        try
        {
            if (HasProperty(obj, name))
            {
                var v = GetPropertyValue(obj, name);
                return v?.ToString();
            }
        }
        catch { }
        return null;
    }
}