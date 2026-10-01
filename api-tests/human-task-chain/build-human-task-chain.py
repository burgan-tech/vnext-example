#!/usr/bin/env python3
"""Generate the `human-task-chain` scenario's six workflow components.

The chain is A → B → C (core) → D (partner) → E → F (credit): six levels across three domains, so
one family exercises every shape the human-task leaf descent has to handle — several levels inside
one domain, a domain boundary, and a SECOND boundary crossed by a runtime other than the one the
client called.

How deep a given instance goes is data, not definition. The start payload carries `hops`; every
descent decrements it and the `descend` rule fires only while it is positive. So:

    hops = 2  →  leaf is C   (Senaryo 1, one domain)
    hops = 3  →  leaf is D   (Senaryo 2, one boundary)
    hops = 5  →  leaf is F   (Senaryo 3, two boundaries)
    hops = 0  →  leaf is A   (the root is its own leaf)

Every level writes its OWN `humanTask.title`, which is what lets a test prove the listed title came
from the leaf rather than from the root — the defect this scenario exists for.

Generated files are committed; re-run after editing this script, then run
`labs/cross-domain/encode-scripts.py` to fill each script's base64 `code` from its `.csx`.

Usage: python3 api-tests/human-task-chain/build-human-task-chain.py
"""
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
VERSION = "1.0.6"
SCENARIO = "human-task-chain"

# level key, domain, next level key (None at the leaf end of the definition chain)
CHAIN = [
    ("ht-a", "core", "ht-b"),
    ("ht-b", "core", "ht-c"),
    ("ht-c", "core", "ht-d"),
    ("ht-d", "partner", "ht-e"),
    ("ht-e", "credit", "ht-f"),
    ("ht-f", "credit", None),
]

DOMAIN_OF = {key: domain for key, domain, _ in CHAIN}

DESCEND_RULE = '''using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Descend while `hops` is positive. Every subflow mapping decrements it, so the payload's initial
/// value alone decides which level ends up holding the human task — the definition chain is the
/// same for all three scenarios.
/// </summary>
public class DescendRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var hops = 0;
        if (data != null && data.TryGetValue("hops", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out hops);
        }

        var descend = hops > 0;
        LogInformation($"DescendRule: hops={hops} descend={descend}");
        return Task.FromResult(descend);
    }
}
'''

SPAWN_RULE = '''using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Fires the root's SubProcess branch when the payload asks for it (<c>mode = "process"</c>).
/// A SubProcess is fire-and-forget: the root does not wait for it and nothing projects its state
/// upward, so the two become independent units of work and the list must carry BOTH.
/// </summary>
public class SpawnProcessRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var spawn = data != null
                    && data.TryGetValue("mode", out var mode)
                    && mode != null
                    && mode.ToString() == "process";

        LogInformation($"SpawnProcessRule: spawn={spawn}");
        return Task.FromResult(spawn);
    }
}
'''

SPAWN_MAPPING = '''using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Input for the spawned SubProcess. It gets its OWN hop budget, because it is not a continuation
/// of the root's chain — it is a separate unit of work that may itself descend through SubFlows.
/// </summary>
/// <remarks>
/// <c>ISubProcessMapping</c>, not <c>ISubFlowMapping</c>: a <c>type: "P"</c> subflow block is
/// resolved against this interface, and it declares only <c>InputHandler</c> — a SubProcess returns
/// nothing to its parent, which is fire-and-forget expressed in the contract. Implementing the
/// SubFlow interface instead faults the parent with <c>Instance:100023</c>.
/// </remarks>
public class SpawnProcessMapping : ScriptBase, ISubProcessMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var hops = 0;
        if (data != null && data.TryGetValue("processHops", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out hops);
        }

        dynamic childInput = new ExpandoObject();
        childInput.hops = hops;

        if (data != null && data.TryGetValue("testId", out var testId) && testId != null)
        {
            childInput.testId = testId;
        }

        dynamic humanTask = new ExpandoObject();
        humanTask.title = "HT-D process step";
        humanTask.description = "HT-D process step description";
        childInput.humanTask = humanTask;

        LogInformation($"SpawnProcessMapping: spawning ht-d as SubProcess with hops={hops}");
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }
}
'''

SUBFLOW_MAPPING = '''using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Hands the chain down one level: decrements `hops`, carries `testId`, and gives the child its OWN
/// humanTask text. The distinct text per level is the point — a test can then tell whether the
/// listed title came from the leaf or was taken from an ancestor.
/// </summary>
public class {cls} : ScriptBase, ISubFlowMapping
{{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {{
        var data = context.Instance.Data as IDictionary<string, object>;

        var hops = 0;
        if (data != null && data.TryGetValue("hops", out var raw) && raw != null)
        {{
            int.TryParse(raw.ToString(), out hops);
        }}

        dynamic childInput = new ExpandoObject();
        childInput.hops = hops - 1;

        if (data != null && data.TryGetValue("testId", out var testId) && testId != null)
        {{
            childInput.testId = testId;
        }}

        dynamic humanTask = new ExpandoObject();
        humanTask.title = "{child} step";
        humanTask.description = "{child} step description";
        childInput.humanTask = humanTask;

        LogInformation($"{cls}: descending to {child} with hops={{hops - 1}}");
        return Task.FromResult(new ScriptResponse {{ Data = childInput }});
    }}

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {{
        return Task.FromResult(new ScriptResponse());
    }}
}}
'''


def label(text):
    return [{"language": "en-US", "label": text}]


def states(level, nxt, is_root=False):
    """initial → (spawn process) | (descend → subflow → next level) | (human, the rest point)."""
    initial_transitions = []

    if is_root:
        # Evaluated before descend, so `mode: "process"` wins over a hop budget. The root spawns a
        # SubProcess and carries straight on to its own human state: nothing waits for the child,
        # so the two are independent rows in the list.
        initial_transitions.append({
            "key": f"{level}-spawn-process",
            "target": f"{level}-spawn",
            "triggerType": 1,
            "versionStrategy": "Minor",
            "labels": label(f"{level}: spawn a SubProcess"),
            "rule": {"location": "./src/SpawnProcessRule.csx", "code": ""},
            "onExecutionTasks": []
        })

    if nxt is not None:
        initial_transitions.append({
            "key": f"{level}-descend",
            "target": f"{level}-subflow",
            "triggerType": 1,
            "versionStrategy": "Minor",
            "labels": label(f"{level}: descend to {nxt}"),
            "rule": {"location": "./src/DescendRule.csx", "code": ""},
            "onExecutionTasks": []
        })

    # The fallback. TransitionKind.DefaultAutoTransition (10) is the runtime's own answer to "run
    # this when no other automatic transition is satisfied" — it is the ONLY automatic transition
    # allowed to have no rule (WorkflowValidator.ValidateAutoTransition), and a state may declare at
    # most one. Writing an inverted rule instead would duplicate the descend condition and let the
    # two drift into a state with no way out.
    initial_transitions.append({
        "key": f"{level}-to-human",
        "target": f"{level}-human",
        "triggerType": 1,
        "triggerKind": 10,
        "versionStrategy": "Minor",
        "labels": label(f"{level}: rest in the human state"),
        "onExecutionTasks": []
    })

    result = [{
        "key": f"{level}-initial",
        "stateType": 1,
        "subType": 0,
        "versionStrategy": "Minor",
        "labels": label(f"{level} initial"),
        "view": None,
        "subFlow": None,
        "onEntries": [],
        "onExits": [],
        "transitions": initial_transitions
    }]

    if nxt is not None:
        cls = f"{level.replace('-', '').capitalize()}ToNextSubFlowMapping"
        result.append({
            "key": f"{level}-subflow",
            "stateType": 4,
            "subType": 0,
            "versionStrategy": "Minor",
            "labels": label(f"{level} subflow"),
            "view": None,
            "subFlow": {
                "type": "S",
                "process": {
                    "key": nxt,
                    "domain": DOMAIN_OF[nxt],
                    "version": VERSION,
                    "flow": "sys-flows"
                },
                "mapping": {"location": f"./src/{cls}.csx", "code": ""},
                # Parent-defined overrides, stamped onto the child at start by SubflowStarter.
                #
                # `states` is the one the human-task list reads: it narrows the CHILD's visibility
                # from the parent's point of view, and at a leaf it is the only way that narrowing
                # can be honoured — the parent-side reader needs an active correlation, which a leaf
                # by definition does not have. Keyed by the child's own state key.
                #
                # Authored only on the ht-d -> ht-e link. Two reasons: no other test rests on ht-e,
                # so no existing scenario changes meaning; and that link crosses a domain boundary
                # (partner -> credit), so the stamp has to survive a cross-domain start to be read
                # at the leaf at all.
                **({"overrides": {
                    "states": {
                        f"{nxt}-human": {
                            "queryRoles": [
                                {"role": "xd-override-only", "grant": "allow"},
                                {"role": "ht-blocked", "grant": "deny"},
                            ]
                        }
                    },
                    "transitions": {
                        f"{nxt}-approve": {
                            "roles": [{"role": "xd-override-only", "grant": "allow"}]
                        }
                    }
                }} if nxt == "ht-e" else {})
            },
            "onEntries": [],
            "onExits": [],
            # The only way out of the SubFlow state, taken when the child comes back — a fallback
            # by nature, so the same DefaultAutoTransition kind rather than an always-true rule.
            "transitions": [{
                "key": f"{level}-subflow-done",
                "target": f"{level}-completed",
                "triggerType": 1,
                "triggerKind": 10,
                "versionStrategy": "Minor",
                "labels": label(f"{level}: subflow finished"),
                "onExecutionTasks": []
            }]
        })

    if is_root:
        result.append({
            "key": f"{level}-spawn",
            "stateType": 4,
            "subType": 0,
            "versionStrategy": "Minor",
            "labels": label(f"{level} spawn SubProcess"),
            "view": None,
            "subFlow": {
                # type P — fire-and-forget. The parent gets no resume and no upward state
                # projection, which is exactly why the child has to be listed on its own.
                "type": "P",
                "process": {
                    "key": "ht-d",
                    "domain": DOMAIN_OF["ht-d"],
                    "version": VERSION,
                    "flow": "sys-flows"
                },
                "mapping": {"location": "./src/SpawnProcessMapping.csx", "code": ""}
            },
            "onEntries": [],
            "onExits": [],
            "transitions": [{
                "key": f"{level}-spawned",
                "target": f"{level}-human",
                "triggerType": 1,
                "triggerKind": 10,
                "versionStrategy": "Minor",
                "labels": label(f"{level}: carry on after spawning"),
                "onExecutionTasks": []
            }]
        })

    # The human rest point. subType 6 is what puts this instance — or its root — in the human-task
    # list, and the transition's roles are what the leaf-side authorization evaluates.
    result.append({
        "key": f"{level}-human",
        "stateType": 2,
        "subType": 6,
        "versionStrategy": "Minor",
        "labels": label(f"{level} human task"),
        # queryRoles is what the human-task list is decided by: it answers "may this caller SEE this
        # instance", which is the question a task list asks. The transition roles below are still
        # authored and still gate what the client is offered on open, but they no longer influence
        # the list. Same three grants, so the level-discrimination and DENY tests keep their meaning.
        "queryRoles": [
            {"role": "ht-approver", "grant": "allow"},
            {"role": f"{level}-approver", "grant": "allow"},
            {"role": "ht-blocked", "grant": "deny"},
        ],
        "view": None,
        "subFlow": None,
        "onEntries": [],
        "onExits": [],
        "transitions": [{
            "key": f"{level}-approve",
            "target": f"{level}-completed",
            "triggerType": 0,
            "versionStrategy": "Minor",
            "labels": label(f"{level}: approve"),
            # Three grants, each pinning a different rule of the evaluator:
            #   ht-approver    - the broad role every existing scenario uses
            #   {level}-approver - ONLY this level. A caller holding just this one must see a chain
            #                    whose leaf rests here and NOT one that rests elsewhere, which is
            #                    what proves the decision came from the LEAF's transition set.
            #   ht-blocked     - DENY, and DENY wins over the whole set however many ALLOWs match.
            "roles": [
                {"role": "ht-approver", "grant": "allow"},
                {"role": f"{level}-approver", "grant": "allow"},
                {"role": "ht-blocked", "grant": "deny"},
            ],
            "onExecutionTasks": []
        }]
    })

    result.append({
        "key": f"{level}-completed",
        "stateType": 3,
        "subType": 1,
        "versionStrategy": "Minor",
        "labels": label(f"{level} completed"),
        "view": None,
        "subFlow": None,
        "onEntries": [],
        "onExits": [],
        "transitions": []
    })

    result.append({
        "key": f"{level}-cancelled",
        "stateType": 3,
        "subType": 7,
        "versionStrategy": "Minor",
        "labels": label(f"{level} cancelled"),
        "view": None,
        "subFlow": None,
        "onEntries": [],
        "onExits": [],
        "transitions": []
    })

    return result


def component(level, domain, nxt, is_root):
    return {
        "key": level,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": domain,
        "version": VERSION,
        "tags": [SCENARIO, "human-task", "subflow", domain],
        "attributes": {
            "type": "F" if is_root else "S",
            "timeout": None,
            "labels": label(f"Human Task Chain {level.upper()} ({domain})"),
            "functions": [],
            "features": [],
            "extensions": [],
            "sharedTransitions": [],
            # cancel carries a role deliberately. GetAvailableUserTransitionKeys appends the
            # well-known cancel/updateData/exit from EVERY state, and an empty grant set allows
            # everyone — so a role-less cancel would authorize every caller for every instance and
            # make any "this caller must not see the task" assertion vacuous.
            "cancel": {
                "key": f"cancel-{level}",
                "target": f"{level}-cancelled",
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": label(f"Cancel {level}"),
                # The SAME set as the level's approve transition, deliberately. cancel is appended
                # to availableTransitions from every state, so a weaker grant set here would be a
                # back door: a caller denied on approve would still be authorized through cancel and
                # the task would appear anyway.
                "roles": [
                    {"role": "ht-approver", "grant": "allow"},
                    {"role": f"{level}-approver", "grant": "allow"},
                    {"role": "ht-blocked", "grant": "deny"},
                ]
            },
            "startTransition": {
                "key": f"start-{level}",
                "target": f"{level}-initial",
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": label(f"Start {level}"),
                "onExecutionTasks": []
            },
            "states": states(level, nxt, is_root=is_root)
        }
    }


def write(path, content):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(content)
    print(f"  {os.path.relpath(path, ROOT)}")


def main():
    print("human-task-chain components:")
    for index, (level, domain, nxt) in enumerate(CHAIN):
        folder = os.path.join(ROOT, domain, "Workflows", SCENARIO)
        write(
            os.path.join(folder, f"{level}.json"),
            json.dumps(component(level, domain, nxt, is_root=index == 0), indent=2, ensure_ascii=False) + "\n")

        src = os.path.join(folder, "src")
        write(os.path.join(src, "DescendRule.csx"), DESCEND_RULE)
        if index == 0:
            write(os.path.join(src, "SpawnProcessRule.csx"), SPAWN_RULE)
            write(os.path.join(src, "SpawnProcessMapping.csx"), SPAWN_MAPPING)
        if nxt is not None:
            cls = f"{level.replace('-', '').capitalize()}ToNextSubFlowMapping"
            write(
                os.path.join(src, f"{cls}.csx"),
                SUBFLOW_MAPPING.format(cls=cls, child=nxt.upper()))

    print("\nNow run: python3 labs/cross-domain/encode-scripts.py "
          + " ".join(sorted({f'{d}/Workflows/{SCENARIO}' for _, d, _ in CHAIN})))


if __name__ == "__main__":
    main()
