using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Transition-level event mapping for <c>approve-by-event</c> (<c>triggerType: 3</c>).
/// <para>
/// Correlates by business key: the runtime looks up the ACTIVE instance whose key equals
/// <c>InstanceKey</c>. No match is a normal answer — the endpoint acks so the broker does not
/// redeliver forever — which is why the test asserts on the instance rather than on the response.
/// </para>
/// </summary>
public class ApproveEventMapping : IEventMapping
{
    public async Task<EventMappingResult> Handler(ScriptContext context)
    {
        var payload = context.EventPayload;

        return await Task.FromResult(new EventMappingResult
        {
            InstanceKey = (string)payload.orderId,
            Body = new
            {
                decision = (string)payload.decision,
                approvedBy = (string)payload.approvedBy
            }
        });
    }
}
