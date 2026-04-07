using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Register Domain Lifecycle Mapping - Starts domain workflow instance with domain name as key using StartTask
/// </summary>
public class RegisterDomainLifecycleMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var startTask = task as StartTask;
            if (startTask == null)
            {
                throw new InvalidOperationException("Task must be a StartTask");
            }

            var domainName = context.Instance?.Data?.domainName;
            var baseUrl = context.Instance?.Data?.baseUrl;
            var healthUrl = context.Instance?.Data?.healthUrl;
            var appId = context.Instance?.Data?.appId;

            // Set instance key to domainName
            // This ensures each domain has its own workflow instance
            if (!string.IsNullOrEmpty(domainName?.ToString()))
            {
                startTask.SetKey(domainName.ToString());
            }

            // Prepare initialization data
            // Pass domain information to domain workflow (schema compliant)
            startTask.SetBody(new
            {
                domainName = domainName,
                baseUrl = baseUrl,
                healthUrl = healthUrl,
                appId = appId
            });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "register-domain-lifecycle-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // StartTask returns instanceId on success
            if (context.Body?.isSuccess == true)
            {

                return new ScriptResponse
                {
                    Key = "domain-lifecycle-started",
                    Data = new
                    {
                         domainRegistration = new
                        {
                            success = true,
                            operation = "register",
                            registeredAt = DateTime.UtcNow
                        },
                        domainLifecycleStarted = true,
                        startedAt = DateTime.UtcNow,
                        status = "DOMAIN_LIFECYCLE_WORKFLOW_STARTED"
                    },
                    Tags = new[] { "domain", "lifecycle", "started", "start", "success" }
                };
            }
            else
            {
                return new ScriptResponse
                {
                    Key = "domain-lifecycle-start-failed",
                    Data = new
                    {
                           domainRegistration = new
                        {
                            success = false,
                            operation = "register",
                            error = "Failed to register domain",
                            statusCode = context.Body?.statusCode ?? 500,
                            registeredAt = DateTime.UtcNow
                        },
                        domainLifecycleStarted = false,
                        error = context.Body?.errorMessage ?? "Failed to start domain lifecycle workflow",
                        startedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "lifecycle", "start", "error" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "domain-lifecycle-start-exception",
                Data = new
                {
                    domainLifecycleStarted = false,
                    error = "Exception during domain lifecycle start",
                    errorDescription = ex.Message,
                    startedAt = DateTime.UtcNow,
                     domainRegistration = new
                    {
                        success = false,
                        operation = "register",
                        error = "Exception during domain registration",
                        errorDescription = ex.Message,
                        registeredAt = DateTime.UtcNow
                    }
                },
                Tags = new[] { "domain", "lifecycle", "exception", "error" }
            };
        }
    }
}

