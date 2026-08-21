using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Unconditional auto-transition gate shared by every case state: once the case's fan-out batch has
/// run, move to <c>case-settled</c>.
/// <para>
/// <b>Why it returns a bare <c>true</c>, and why that is not laziness.</b> Each case state holds
/// exactly ONE onEntry task — that case's fan-out batch — and the workflow declares no error
/// boundary at any level. So a failed join fails the task, fails the transition, and faults the
/// instance; automatic transitions (<c>RunAutomaticTransitionsStep</c>, order 90) are never
/// evaluated at all. Reaching this rule therefore already means the join succeeded, and there is
/// nothing left for the rule to decide. Re-deriving "did the batch succeed?" from the summary here
/// would be worse than redundant: it would let a rule bug mask a join verdict, and the whole test
/// class reads the join verdict off which state the instance lands in.
/// </para>
/// <para>
/// <b>Why the rule exists at all.</b> <c>WorkflowValidator</c> requires EVERY <c>triggerType: 1</c>
/// transition to carry a rule — "Auto transition '…' must have a rule defined." An earlier revision
/// of this workflow omitted the rule on the assumption that a lone auto transition could be
/// unconditional by omission; publish rejected it with 400 for all nine case states. Unconditional
/// means "a rule that always returns true", not "no rule". Do not remove this file to tidy up.
/// </para>
/// <para>
/// Contrast with the sibling <c>fan-out-documents</c> flow, whose two auto transitions
/// (<c>AllSucceededRule</c> / <c>PartialFailureRule</c>) are a genuinely complementary pair because
/// that scenario branches on the batch's summary. This one does not branch — the fault does the
/// branching.
/// </para>
/// </summary>
public class CaseSettledRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        LogInformation("CaseSettledRule: unconditional — the batch already succeeded to get here.");
        return Task.FromResult(true);
    }
}
