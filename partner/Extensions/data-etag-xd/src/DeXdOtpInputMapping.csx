using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// de-xd-otp-input — the partner child's screen input, the shape morph-idm's 3FA child exposes
// as extensions.otpInput (vnext#1084). It is derived ONLY from the child's own instance data, so a
// write in the child that leaves its state alone is the one thing that changes it.
// type=3 DefinedFlows + scope=1 GetInstance: runs on the child's instance reads, and on the parent's
// data function while the child is the parent's active SubFlow.
public class DeXdOtpInputMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
        => Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var wrongOtp = false;
        var attempt = 0;
        try { wrongOtp = Convert.ToBoolean(context.Instance?.Data?.wrongOtp ?? false); } catch { }
        try { attempt = Convert.ToInt32(context.Instance?.Data?.attempt ?? 0); } catch { }

        return Task.FromResult(new ScriptResponse
        {
            Data = new { maskedPhone = "*******4321", wrongOtp, attempt }
        });
    }
}
