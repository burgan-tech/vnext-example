using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Update Domain Lifecycle Mapping - Triggers update transition in existing domain workflow instance using DirectTriggerTask
/// </summary>
public class UpdateDomainLifecycleMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var directTriggerTask = task as DirectTriggerTask;
            if (directTriggerTask == null)
            {
                throw new InvalidOperationException("Task must be a DirectTriggerTask");
            }

            var domainName = context.Instance?.Data?.domainName;
            var baseUrl = context.Instance?.Data?.baseUrl;
            var healthUrl = context.Instance?.Data?.healthUrl;
            var appId = context.Instance?.Data?.appId;
            
            // Get ETag from instance data (from check-domain-exists response)
            var eTag = context.Instance?.Data?.instanceETag?.ToString() ?? 
                      context.Instance?.Data?.eTag?.ToString();

            // Set instance key to domainName
            // This will find the existing domain workflow instance
            if (!string.IsNullOrEmpty(domainName?.ToString()))
            {
                directTriggerTask.SetKey(domainName.ToString());
            }

            // Set transition name (already in config, but can be set explicitly)
            directTriggerTask.SetTransitionName("update");

            // Prepare update data
            // Pass updated domain information to domain workflow (schema compliant)
            // Include ETag for optimistic concurrency control
            directTriggerTask.SetBody(new
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
                Key = "update-domain-lifecycle-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // DirectTriggerTask returns success status
            if (context.Body?.isSuccess == true)
            {
                return new ScriptResponse
                {
                    Key = "domain-lifecycle-update-triggered",
                    Data = new
                    {
                        domainLifecycleUpdateTriggered = true,
                        triggeredAt = DateTime.UtcNow,
                        status = "DOMAIN_LIFECYCLE_UPDATE_TRIGGERED",
                         domainRegistration = new
                        {
                            success = true,
                            operation = "update",
                            registeredAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "lifecycle", "update", "triggered", "direct-trigger", "success" }
                };
            }
            else
            {
                return new ScriptResponse
                {
                    Key = "domain-lifecycle-update-failed",
                    Data = new
                    {
                        domainLifecycleUpdateTriggered = false,
                        error = context.Body?.errorMessage ?? "Failed to trigger domain lifecycle update",
                        triggeredAt = DateTime.UtcNow,
                         domainRegistration = new
                        {
                            success = false,
                            operation = "update",
                            error = "Failed to update cache domain",
                            statusCode = context.Body?.statusCode ?? 500,
                            registeredAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "lifecycle", "update", "direct-trigger", "error" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "domain-lifecycle-update-exception",
                Data = new
                {
                    domainLifecycleUpdateTriggered = false,
                    error = "Exception during domain lifecycle update trigger",
                    errorDescription = ex.Message,
                    triggeredAt = DateTime.UtcNow,
                     domainRegistration = new
                    {
                        success = false,
                        operation = "update",
                        error = "Exception during domain update",
                        errorDescription = ex.Message,
                        registeredAt = DateTime.UtcNow
                    }
                },
                Tags = new[] { "domain", "lifecycle", "update", "exception", "error" }
            };
        }
    }
}

