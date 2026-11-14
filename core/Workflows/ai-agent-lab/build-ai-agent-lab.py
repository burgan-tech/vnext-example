#!/usr/bin/env python3
"""Builds the ai-agent-lab fixtures (vnext#1105, AI Agent state, stateType 6).

Outputs, all from this one file so the three flows and the agent stay consistent:
  core/Workflows/ai-agent-lab/ai-agent-lab.json         model decision, tools, flow memory, re-entry ceiling
  core/Workflows/ai-agent-lab/ai-agent-lab-script.json  script decision (IAgentDecisionMapping) and its fallback
  core/Workflows/ai-agent-lab/ai-agent-lab-rules.json   rules mode: the transitions' rules read the agent's output
  core/Agents/ai-agent-lab/ai-triage-agent.json         the sys-agents component (provider profile "local")

The agent runs on the runtime's deterministic "local" provider: it calls every tool once with arguments
built from the tool's parameter schema, then answers from the output schema (const, default, enum[0]).
So the model decision is always the FIRST candidate and category is always "question".

Run: python3 core/Workflows/ai-agent-lab/build-ai-agent-lab.py   (from the repo root)
"""
import base64
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[3]
SRC = pathlib.Path(__file__).resolve().parent / "src"
VERSION = "1.0.0"


def code(name):
    return {"location": f"./src/{name}", "code": base64.b64encode((SRC / name).read_bytes()).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def auto(key, target, text, rule=None, description=None):
    t = {"key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor", "labels": label(text), "onExecutionTasks": []}
    if description:
        t["description"] = description
    if rule:
        t["rule"] = rule
    return t


def manual(key, target, text):
    return {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label(text), "onExecutionTasks": []}


def state(key, state_type, text, transitions=None, agent=None, sub_type=0):
    s = {"key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major", "labels": label(text),
         "view": None, "subFlow": None, "onEntries": [], "onExits": [], "transitions": transitions or []}
    if agent:
        s["agent"] = agent
    return s


def finish(key, text):
    return state(key, 3, text, sub_type=1)


AGENT_REF = {"key": "ai-triage-agent", "domain": "core", "flow": "sys-agents", "version": VERSION}


def flow(key, text, states):
    return {
        "key": key, "flow": "sys-flows", "flowVersion": "1.0.0", "domain": "core", "version": VERSION,
        "tags": ["integration-test", "ai-agent-lab", "ai-agent"],
        "attributes": {
            "type": "F", "timeout": None, "labels": label(text), "functions": [], "features": [], "extensions": [],
            "startTransition": {"key": f"start-{key}", "target": states[0]["key"], "triggerType": 0, "versionStrategy": "Major",
                                "labels": label(f"Start {key}"), "onExecutionTasks": []},
            "states": states,
        },
    }


def write(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2, ensure_ascii=False) + "\n")
    print("wrote", path.relative_to(ROOT))


agent = {
    "key": "ai-triage-agent", "flow": "sys-agents", "flowVersion": "1.0.0", "domain": "core", "version": VERSION,
    "tags": ["integration-test", "ai-agent-lab"],
    "attributes": {
        "provider": "local",
        "instructions": {"text": "You classify customer messages for the ai-agent-lab integration test."},
        "model": {"temperature": 0, "maxOutputTokens": 512},
        "tools": [
            {"kind": 2, "name": "bump", "description": "Adds one to count.",
             "parameters": {"type": "object", "properties": {"count": {"type": "integer", "default": 41}}, "required": ["count"]},
             "binding": {"code": {"location": "dynamicExpresso", "encoding": "NAT", "code": "args.count + 1"}}},
            {"kind": 1, "name": "lab_script", "description": "Runs the lab script task.",
             "parameters": {"type": "object", "properties": {"note": {"type": "string", "default": "from-agent"}}, "required": ["note"]},
             "binding": {"task": {"key": "sched-script-task", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"}}},
        ],
        "memory": {"mode": "none"},
        "output": {"schema": {"type": "object",
                              "properties": {"category": {"type": "string", "enum": ["question", "fraud", "other"]},
                                             "summary": {"type": "string", "default": "classified by the local provider"}},
                              "required": ["category"]}},
        "policy": {"maxIterations": 4, "maxToolCalls": 4, "timeoutSeconds": 30},
        "audit": {"transcript": "none", "persistToolResults": True},
    },
}

model_flow = flow("ai-agent-lab", "AI Agent Lab (model decision)", [
    state("classifying", 6, "Classifying (AiAgent, model decision)", sub_type=5, agent={
        "agent": AGENT_REF,
        "prompt": {"template": "<message>{{ data.message }}</message>"},
        "output": {"path": "$.triage"},
        "memory": {"mode": "flow", "maxMessages": 20, "retentionDays": 1},
        "decision": {"mode": "model", "fallback": "classify-failed", "onExhausted": "too-many",
                     "hints": {"is-question": "The customer asks for information."}},
        "policy": {"maxEntries": 2},
    }, transitions=[
        auto("is-question", "answered", "Is a question"),
        auto("is-fraud", "escalated", "Is fraud", description="The customer reports fraud."),
        auto("classify-failed", "failed", "Classification failed"),
        auto("too-many", "exhausted", "Entered too often"),
    ]),
    state("answered", 2, "Answered", transitions=[
        manual("ask-again", "classifying", "Ask again"),
        manual("close", "closed", "Close"),
    ]),
    finish("escalated", "Escalated"),
    finish("failed", "Failed"),
    finish("exhausted", "Exhausted"),
    finish("closed", "Closed"),
])

script_flow = flow("ai-agent-lab-script", "AI Agent Lab (script decision)", [
    state("classifying", 6, "Classifying (AiAgent, script decision)", sub_type=5, agent={
        "agent": AGENT_REF,
        "prompt": {"template": "<message>{{ data.message }}</message>"},
        "output": {"path": "$.triage"},
        "tools": {"exclude": ["lab_script"]},
        "decision": {"mode": "script", "fallback": "classify-failed", "mapping": code("ScriptDecision.csx")},
    }, transitions=[
        auto("is-question", "answered", "Is a question"),
        auto("is-fraud", "escalated", "Is fraud"),
        auto("classify-failed", "failed", "Classification failed"),
    ]),
    finish("answered", "Answered"),
    finish("escalated", "Escalated"),
    finish("failed", "Failed"),
])

rules_flow = flow("ai-agent-lab-rules", "AI Agent Lab (rules decision)", [
    state("classifying", 6, "Classifying (AiAgent, rules decision)", sub_type=5, agent={
        "agent": AGENT_REF,
        "prompt": {"template": "<message>{{ data.message }}</message>"},
        "output": {"path": "$.triage"},
        "tools": {"exclude": ["lab_script", "bump"]},
        "decision": {"mode": "rules", "fallback": "classify-failed"},
    }, transitions=[
        auto("to-other", "other", "Not a question (never)", rule=code("NeverRule.csx")),
        auto("to-question", "answered", "Category is question", rule=code("CategoryRule.csx")),
        auto("classify-failed", "failed", "No rule matched"),
    ]),
    finish("answered", "Answered"),
    finish("other", "Other"),
    finish("failed", "Failed"),
])

base = pathlib.Path(__file__).resolve().parent
write(base / "ai-agent-lab.json", model_flow)
write(base / "ai-agent-lab-script.json", script_flow)
write(base / "ai-agent-lab-rules.json", rules_flow)
write(ROOT / "core" / "Agents" / "ai-agent-lab" / "ai-triage-agent.json", agent)
