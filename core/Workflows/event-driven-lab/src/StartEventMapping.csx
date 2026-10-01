using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Workflow-level event mapping: turns a raw broker message into a new instance.
/// <para>
/// Reached through <c>POST .../instances/events?action=start</c>, which a Dapr Subscription routes
/// to from the topic. The runtime has already unwrapped the CloudEvent envelope, so
/// <c>context.EventPayload</c> is the message the publisher actually sent.
/// </para>
/// <para>
/// <c>InstanceKey</c> is the correlation contract for everything that follows: the approve event
/// later finds this instance by exactly this value, so it must come from the payload and not be
/// generated here.
/// </para>
/// </summary>
public class StartEventMapping : IEventMapping
{
    public async Task<EventMappingResult> Handler(ScriptContext context)
    {
        var payload = context.EventPayload;

        return await Task.FromResult(new EventMappingResult
        {
            InstanceKey = (string)payload.orderId,
            Body = new
            {
                orderId = (string)payload.orderId,
                amount = payload.amount,
                // Proves the instance was created by the event path rather than by an HTTP start.
                startedBy = "event",
                approvals = 0
            }
        });
    }
}
