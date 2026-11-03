"""Handlers for python-task-lab (vnext-example tests/Core.IntegrationTests/Tests/PythonTaskLab)."""

import time

from dateutil import parser as date_parser
from vnext_runtime import HandlerContext, handler


@handler("ptl_sum")
def ptl_sum(input):
    values = input["values"]
    return {"total": sum(values), "count": len(values)}


@handler("ptl_context")
def ptl_context(input, context: HandlerContext):
    return {
        "domain": context.domain,
        "workflow": context.workflow,
        "workflowVersion": context.workflow_version,
        "instanceId": context.instance_id,
        "taskKey": context.task_key,
        "timeoutMs": context.timeout_ms,
    }


@handler("ptl_fail")
def ptl_fail(input):
    raise ValueError(f"ptl rejected {input.get('reason', 'no reason')}")


@handler("ptl_slow")
def ptl_slow(input):
    time.sleep(input.get("seconds", 3))
    return {"slept": True}


@handler("ptl_nan")
def ptl_nan(input):
    return {"value": float("nan")}


@handler("ptl_dateutil")
def ptl_dateutil(input):
    parsed = date_parser.isoparse(input["date"])
    return {"year": parsed.year, "weekday": parsed.strftime("%A")}
