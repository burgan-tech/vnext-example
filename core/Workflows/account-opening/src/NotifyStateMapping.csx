using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;


public class NotifyStateMapping : ScriptBase, IStateNotificationMapping
{
    public Task<StateNotificationMetadata> EnrichAsync(ScriptContext context)
    {
        return Task.FromResult(new StateNotificationMetadata()
        {
            Metadata = new Dictionary<string, string>()
            {
                {"X-Device-Id", context.Headers["x-device-id"]},
                {"X-Installation-Id", context.Headers["x-installation-id"]},
            }
        });
    }
}
