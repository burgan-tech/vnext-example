#!/usr/bin/env python3
"""
authorization-chain-lab akislarini uretir (csx -> base64 gomulu workflow JSON).

    python3 core/Workflows/authorization-chain-lab/build-authorization-chain-lab.py

Uretilenler:
  core/Workflows/authorization-chain-lab/authorization-chain-lab-root.json
  core/Workflows/authorization-chain-lab/authorization-chain-lab-root-plain.json
  core/Workflows/authorization-chain-lab/authorization-chain-lab-mid.json
  core/Workflows/authorization-chain-lab/authorization-chain-lab-leaf.json
  core/Workflows/authorization-chain-lab/src/*.csx

Bu lab TEK BIR SEYI olcer: `authorize` fonksiyonunun AKTIF KORELASYON ZINCIRI boyunca
verdigi cevabin, okuma yuzeylerinin fiilen uyguladigi kapiyla ayni olup olmadigini.

Neden ayri bir akis: `subflow-orchestration` zaten A->B->C bir zincir ve A'nin B icin bir
`overrides.states` girdisi var — ama o suite YESIL ve testleri rol header'i gondermeden
okuyor. Ara/yaprak seviyelere `queryRoles` eklemek o testleri kirardi. Bu lab, ayni
zincir sekline sahip AMA her seviyesi yetkilendirme icin tasarlanmis bagimsiz bir kopya.

Zincir ve grant tasarimi
------------------------
  ROOT (F)  waiting --subFlow--> MID (S)  mid-waiting --subFlow--> LEAF (S)  leaf-waiting

  * ROOT.queryRoles          = allow [chain.reader, chain.admin]
  * ROOT overrides MID:        states["mid-waiting"].queryRoles = allow [chain.admin]
  * MID.queryRoles           = allow [chain.reader, chain.admin, chain.mid-only]
  * MID overrides LEAF:        states["leaf-waiting"].queryRoles = allow [chain.leaf-admin]
  * LEAF.queryRoles          = allow [chain.reader, chain.admin, chain.leaf-only]

Bu tek zincir, talep edilen UC vakanin ucunu de ayni anda kanitlar:

  (1) A -> B -> C, C'ye kadar inilir.
      `chain.reader` ROOT'ta gecer, ROOT'un MID override'inda DUSER  => deny.
      Iniş olmasaydi ROOT tek basina ALLOW derdi; konjonksiyon boyle gorunur.

  (2) A'nin B'ye override'i C'ye TASINMAZ.
      `chain.admin` ROOT'ta gecer, ROOT->MID override'inda gecer, ama LEAF'te MID'in
      KENDI override'i (`chain.leaf-admin`) uygulanir => deny.
      Eger A'nin override'i asagi tasinsaydi `chain.admin` LEAF'te de gecerdi.

  (3) B, C'ye kendi kurallarina uygun override bildirir ve C'de ele alinir.
      `chain.leaf-admin` ROOT'ta DUSER (ROOT.queryRoles'da yok) => zincir zaten orada biter.
      Bu yuzden LEAF override'inin tek basina gorunur olmasi icin `root-plain` vardir:
      ayni MID/LEAF'i override BILDIRMEDEN baslatir, boylece MID'in kendi queryRoles'u ve
      MID'in LEAF override'i yalin halde olculur.

  * `root-plain`: ayni MID'i override'SIZ baslatir. "Override yoksa nesnenin kendi tanimi
    uygulanir" yarisini kanitlar — REPLACE semantiginin diger yakasi.

  * LEAF `leaf-waiting` state'i `interaction.longPoll` tasir (roles kolu = [chain.admin]).
    `authorize?ack=true` ile ack endpoint'inin AYNI verdikti verdigini olcmek icin.
    RULE kolu burada YOK: sema onu tanimiyor (bkz. main()'deki not).

  * ROOT `cancel` / `updateData` / `exit` ve bir `record-note` shared transition'i tasir:
    aktif subflow varken bunlarin PARENT'ta cevaplandigini (inilmedigini) olcmek icin —
    execution tarafinda da boyle davranirlar.
"""

import base64
import json
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "src"
SRC.mkdir(exist_ok=True)

ALLOW = "allow"

READER = "chain.reader"
ADMIN = "chain.admin"
MID_ONLY = "chain.mid-only"
LEAF_ONLY = "chain.leaf-only"
LEAF_ADMIN = "chain.leaf-admin"
# Granted by the ROOT's override of the mid and by the mid's OWN grants, but deliberately absent from
# the mid's override of the leaf. It is the probe for "an ancestor's override does not travel past its
# direct child": if the root's narrowing reached the leaf, this role would be admitted there.
MID_ADMIN = "chain.mid-admin"


def grants(*roles, grant=ALLOW):
    return [{"role": r, "grant": grant} for r in roles]


def label(text):
    return [{"language": "en-US", "label": text}]


def csx(name: str, body: str) -> dict:
    """Writes the script beside the flow and embeds it base64, as every other lab does."""
    path = SRC / f"{name}.csx"
    path.write_text(body, encoding="utf-8")
    return {
        "location": f"./src/{name}.csx",
        "code": base64.b64encode(body.encode("utf-8")).decode("ascii"),
    }


SUBFLOW_MAPPING = """using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Pass-through: this lab measures authorization, not data flow. The child only needs to
// start; nothing downstream reads what it was started with.
public class ChainSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        input.startedByChainLab = true;
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
        => Task.FromResult(new ScriptResponse());
}
"""

ALWAYS_TRUE = """using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// The chain must assemble itself on start: an authorization test wants the instance already
// sitting at the deepest leaf, not a fixture the test has to drive hop by hop.
public class ChainAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
"""

ACK_RULE = """using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// The RULE arm of interaction.longPoll. Deliberately keyed on a header rather than on
// instance data: the point of the arm is that it can read things no role set contains, and
// a header is the cheapest way for a test to flip it. Fail-closed by contract — anything
// other than an explicit "yes" denies.
public class ChainAckRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var header = context.Headers != null && context.Headers.ContainsKey("x-chain-ack")
            ? context.Headers["x-chain-ack"]
            : null;

        return Task.FromResult(header == "yes");
    }
}
"""


# Publish is VERSION-IMMUTABLE: re-publishing the same key at the same version is a 409
# (Instance:100002) that the tooling reports as success-with-failures, so an edited flow silently
# keeps serving the old definition. Every fixture change needs a patch bump — this constant is the
# one place to do it, and the subflow `process.version` references below read it too.
FIXTURE_VERSION = "1.0.1"


def envelope(key: str, attributes: dict, extra_tags=()) -> dict:
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": FIXTURE_VERSION,
        "tags": ["integration-test", "authorization-chain-lab", *extra_tags],
        "attributes": attributes,
    }


def initial_state(prefix: str, target: str):
    """
    Every flow needs exactly one stateType 1. It carries the automatic hop that walks the chain
    down, so a test only has to start the root and wait for the leaf.
    """
    return {
        "key": f"{prefix}-initial",
        "stateType": 1,
        "subType": 0,
        "versionStrategy": "Major",
        "labels": label(f"{prefix} initial"),
        "view": None,
        "subFlow": None,
        "transitions": [
            {
                "key": f"auto-{prefix}-to-{target}",
                "target": target,
                "triggerType": 1,
                "versionStrategy": "Minor",
                "labels": label("Auto"),
                "rule": csx("ChainAlwaysTrueRule", ALWAYS_TRUE),
            }
        ],
    }


def finish_states(prefix: str):
    return [
        {
            "key": f"{prefix}-done",
            "stateType": 3,
            "subType": 1,
            "versionStrategy": "Major",
            "labels": label(f"{prefix} done"),
            "view": None,
            "subFlow": None,
            "transitions": [],
        },
        {
            "key": f"{prefix}-cancelled",
            "stateType": 3,
            "subType": 7,
            "versionStrategy": "Major",
            "labels": label(f"{prefix} cancelled"),
            "view": None,
            "subFlow": None,
            "transitions": [],
        },
    ]


def subflow_state(key: str, child_key: str, mapping: dict, overrides=None):
    state = {
        "key": key,
        "stateType": 4,
        "subType": 0,
        "versionStrategy": "Major",
        "labels": label(key),
        "view": None,
        "transitions": [],
        "subFlow": {
            "type": "S",
            "process": {
                "key": child_key,
                "domain": "core",
                "version": FIXTURE_VERSION,
                "flow": "sys-flows",
            },
            "mapping": mapping,
        },
    }
    if overrides is not None:
        state["subFlow"]["overrides"] = overrides
    return state


def well_known(prefix: str):
    """cancel / updateData / exit on the root, so the parent-retention rule is measurable."""
    return {
        "cancel": {
            "key": f"cancel-{prefix}",
            "target": f"{prefix}-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel"),
        },
        "updateData": {
            "key": f"update-{prefix}-data",
            "target": "$self",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Update Data"),
        },
        "exit": {
            "key": f"exit-{prefix}",
            "target": f"{prefix}-done",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Exit"),
        },
    }


def build_root(key: str, overrides, tag: str):
    mapping = csx("ChainSubFlowMapping", SUBFLOW_MAPPING)
    attributes = {
        "type": "F",
        "timeout": None,
        "labels": label("Authorization Chain Lab Root"),
        "functions": [],
        "features": [],
        "extensions": [],
        # A shared transition available everywhere: with an active SubFlow the parent RETAINS
        # it, so authorize must answer against the root rather than descending.
        "sharedTransitions": [
            {
                "key": "record-note",
                "target": "$self",
                "triggerType": 0,
                "versionStrategy": "Minor",
                "labels": label("Record Note"),
                "availableIn": ["waiting"],
                "roles": grants(READER, ADMIN),
            }
        ],
        **well_known("root"),
        "startTransition": {
            "key": f"start-{key}",
            "target": "root-initial",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start"),
        },
        "states": [
            initial_state("root", "waiting"),
            subflow_state("waiting", "authorization-chain-lab-mid", mapping, overrides),
            *finish_states("root"),
        ],
        # ROOT allowlist. `chain.leaf-admin` and `chain.mid-only` are deliberately ABSENT:
        # a caller who would pass deeper down still has to get past the root first, which is
        # what makes the conjunction observable from the top.
        "queryRoles": grants(READER, ADMIN, MID_ADMIN),
    }
    return envelope(key, attributes, (tag,))


def build_mid():
    mapping = csx("ChainSubFlowMapping", SUBFLOW_MAPPING)
    attributes = {
        "type": "S",
        "timeout": None,
        "labels": label("Authorization Chain Lab Mid"),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": [],
        "startTransition": {
            "key": "start-authorization-chain-lab-mid",
            "target": "mid-initial",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Mid"),
        },
        "states": [
            initial_state("mid", "mid-waiting"),
            subflow_state(
                "mid-waiting",
                "authorization-chain-lab-leaf",
                mapping,
                # MID declares its OWN override for LEAF. This is the half that proves a
                # parent's override does not travel further down: ROOT narrowed MID to
                # chain.admin, and MID independently narrows LEAF to chain.leaf-admin.
                # ADMIN is included so ONE role survives the whole chain. Without it the leaf
                # refuses everyone and the conjunction denies at every level above, which makes
                # the root/mid distinctions unobservable — the first version of this fixture had
                # exactly that flaw and most of its assertions were unprovable rather than wrong.
                {"states": {"leaf-waiting": {"queryRoles": grants(LEAF_ADMIN, ADMIN)}}},
            ),
            *finish_states("mid"),
        ],
        "queryRoles": grants(READER, ADMIN, MID_ONLY, MID_ADMIN),
    }
    return envelope("authorization-chain-lab-mid", attributes, ("mid",))


def build_mid_terminal():
    """
    A mid with NO SubFlow beneath it.
    <para>
    The override semantics can only be observed on an instance that has no active SubFlow of its
    own. `IsQueryAllowedAsync` keys the lookup on `EffectiveState`, which for a parent is a
    DESCENDANT's state — so the stamped override (keyed by the state the parent declared) never
    matches while the child is itself parked on a subflow, and the gate degrades to the workflow
    root's grants. Measured on the bench: the root narrows the mid to chain.admin and chain.mid-only
    still reads the mid 200.
    </para>
    <para>
    That is a runtime defect and it is NOT this lab's subject, so the override tests address a level
    where the mechanism is reachable. When the defect is fixed, the three-level assertions can move
    back up.
    </para>
    """
    attributes = {
        "type": "S",
        "timeout": None,
        "labels": label("Authorization Chain Lab Mid (terminal)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": [],
        "startTransition": {
            "key": "start-authorization-chain-lab-mid-terminal",
            "target": "mid-initial",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Mid Terminal"),
        },
        "states": [
            initial_state("mid", "mid-waiting"),
            {
                "key": "mid-waiting",
                "stateType": 2,
                "subType": 6,
                "versionStrategy": "Major",
                "labels": label("Mid Waiting (terminal)"),
                "view": None,
                "subFlow": None,
                "transitions": [
                    {
                        "key": "finish-mid",
                        "target": "mid-done",
                        "triggerType": 0,
                        "versionStrategy": "Minor",
                        "labels": label("Finish Mid"),
                    }
                ],
            },
            *finish_states("mid"),
        ],
        "queryRoles": grants(READER, ADMIN, MID_ONLY),
    }
    return envelope("authorization-chain-lab-mid-terminal", attributes, ("mid-terminal",))


def build_two_level_root(key: str, overrides, tag: str):
    """A root whose child is the TERMINAL mid — two levels, so the child's gate is reachable."""
    mapping = csx("ChainSubFlowMapping", SUBFLOW_MAPPING)
    attributes = {
        "type": "F",
        "timeout": None,
        "labels": label("Authorization Chain Lab Two-Level Root"),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": [],
        **well_known("root"),
        "startTransition": {
            "key": f"start-{key}",
            "target": "root-initial",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start"),
        },
        "states": [
            initial_state("root", "waiting"),
            subflow_state("waiting", "authorization-chain-lab-mid-terminal", mapping, overrides),
            *finish_states("root"),
        ],
        "queryRoles": grants(READER, ADMIN, MID_ONLY),
    }
    return envelope(key, attributes, (tag,))


def build_leaf(key: str, interaction: dict, tag: str):
    attributes = {
        "type": "S",
        "timeout": None,
        "labels": label("Authorization Chain Lab Leaf"),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": [],
        "startTransition": {
            "key": f"start-{key}",
            "target": "leaf-initial",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Leaf"),
        },
        "states": [
            initial_state("leaf", "leaf-waiting"),
            {
                "key": "leaf-waiting",
                "stateType": 2,
                "subType": 6,
                "versionStrategy": "Major",
                "labels": label("Leaf Waiting"),
                "view": None,
                "subFlow": None,
                "interaction": interaction,
                "transitions": [
                    {
                        "key": "finish-leaf",
                        "target": "leaf-done",
                        "triggerType": 0,
                        "versionStrategy": "Minor",
                        "labels": label("Finish Leaf"),
                    }
                ],
            },
            *finish_states("leaf"),
        ],
        "queryRoles": grants(READER, ADMIN, LEAF_ONLY),
    }
    return envelope(key, attributes, (tag,))


def write(doc: dict):
    path = HERE / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("wrote", path.relative_to(HERE.parents[2]))


def main():
    write(build_root(
        "authorization-chain-lab-root",
        {"states": {"mid-waiting": {"queryRoles": grants(ADMIN, MID_ADMIN)}}},
        "root",
    ))
    # The A/B partner: same MID and LEAF, started with NO overrides, so the mid's own
    # queryRoles apply. Without this the "override REPLACES" assertion has no control group.
    # Two-level pair: same terminal child, one root overriding it and one not. This is the only
    # shape in which "override REPLACES" and "no override → own grants" are both observable today.
    write(build_two_level_root("authorization-chain-lab-root-plain", None, "root-plain"))
    write(build_two_level_root(
        "authorization-chain-lab-root-narrow",
        {"states": {"mid-waiting": {"queryRoles": grants(ADMIN)}}},
        "root-narrow",
    ))
    write(build_mid_terminal())
    write(build_mid())
    write(build_leaf(
        "authorization-chain-lab-leaf",
        {"longPoll": {"terminate": True, "fallbackTimeoutSeconds": 30, "roles": grants(ADMIN)}},
        "leaf",
    ))
    # NOT BUILT: a `rule`-arm leaf. `interaction.longPoll.rule` shipped in the runtime with
    # issue #936 (0.0.92) but `@burgan-tech/vnext-schema` never grew the property — the installed
    # 0.0.52 AND the sibling repo at 1.0.0 both declare `required: [terminate, roles]` with
    # `additionalProperties: false`, so a rule-armed state cannot pass `npm run validate` and no
    # domain team can author one. `vnext-meta/features.json` claims the opposite ("the vnext-schema
    # longPoll contract enforces exactly one of roles|rule at authoring time"); that claim is false.
    # Rule-arm parity for `authorize?ack=true` is therefore covered by unit tests until the schema
    # ships the arm. Do not add it back here before checking the installed schema version.


if __name__ == "__main__":
    main()
