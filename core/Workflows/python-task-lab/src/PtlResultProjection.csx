using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Shared mapping for every python-task-lab case (vnext PythonTask, type 23).
/// <para>
/// <b>InputHandler</b> — the standard-mapping path: when the transition body carries <c>values</c>,
/// it replaces the task's authored <c>config.input</c> through <see cref="PythonTask.SetInput"/>;
/// otherwise the authored input is sent as is.
/// </para>
/// <para>
/// <b>OutputHandler</b> — writes one fixed-shape projection of the task result
/// (<c>context.Body</c> is the <c>StandardTaskResponse</c>): <c>ptlCase</c>, <c>ptlIsSuccess</c>,
/// <c>ptlStatusCode</c>, <c>ptlErrorMessage</c>, <c>ptlData</c> (the handler output on success),
/// <c>ptlTaskType</c>. It runs for failed calls too, so a test reads the contract error
/// (<c>"&lt;code&gt;: &lt;message&gt;"</c>) from instance data.
/// </para>
/// </summary>
public class PtlResultProjection : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        if (task is PythonTask python && body != null && body.TryGetValue("values", out var values) && values != null)
        {
            python.SetInput(new { values });
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["ptlCase"] = context.Transition?.Key ?? "unknown";
        target["ptlIsSuccess"] = Read(body, "isSuccess");
        target["ptlStatusCode"] = Read(body, "statusCode");
        target["ptlErrorMessage"] = Read(body, "errorMessage");
        target["ptlData"] = Read(body, "data");
        target["ptlTaskType"] = Read(body, "taskType")?.ToString() ?? string.Empty;

        return Task.FromResult(new ScriptResponse { Data = result });
    }

    private static object? Read(IDictionary<string, object>? body, string key) =>
        body != null && body.TryGetValue(key, out var raw) ? raw : null;
}
