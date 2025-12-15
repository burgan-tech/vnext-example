using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Initial Transition Mapping - Initializes workflow with test data
/// </summary>
public class InitialTransitionMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var inputData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "initial-success",
                Data = new
                {
                    testId = inputData?.testId ?? Guid.NewGuid().ToString(),
                    workflowStartedAt = DateTime.UtcNow,
                    testConfiguration = new
                    {
                        testGetSecretAsync = true,
                        testGetConfigValue = true,
                        testTaskResponse = true,
                        testDaprPubSub = true,
                        testHttpTask = true,
                        testDaprServiceTask = true,
                        testNotificationTask = true,
                        testScriptTask = true,
                        testGetInstanceData = true,
                        testDirectTransition = true,
                        testStartWorkflow = true,
                        testSubProcess = true
                    },
                    taskTestResults = new { }
                },
                Tags = new[] { "task-test", "initialized" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "initial-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

