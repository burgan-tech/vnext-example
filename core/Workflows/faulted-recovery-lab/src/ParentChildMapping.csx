using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// faulted-recovery-lab: the parent's SubFlow mapping. The input passes nothing on. The output throws when
/// the completed child's <c>note</c> is <c>"fail-output"</c> — a parent faulted by its SubFlow output mapping,
/// with the child already Completed — and otherwise copies the note back as <c>childNote</c>.
/// </summary>
public class ParentChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = new ExpandoObject() });

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        var note = body != null && body.TryGetValue("note", out var value) ? value?.ToString() : null;
        if (note == "fail-output")
            throw new InvalidOperationException("faulted-recovery-lab: the child asked the output mapping to throw");

        dynamic output = new ExpandoObject();
        output.childNote = note;
        return Task.FromResult(new ScriptResponse { Data = output });
    }
}
