using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;
/// <summary>
/// Register Domain Mapping - Creates new domain in Redis
/// </summary>
public class RegisterDomainMapping :ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
            {
                throw new InvalidOperationException("Task must be an HttpTask");
            }

            var domainName = context.Instance?.Data?.domainName;
            var baseUrl = context.Instance?.Data?.baseUrl;
            var healthUrl = context.Instance?.Data?.healthUrl;
            var appId = context.Instance?.Data?.appId;
            // var flows = context.Instance?.Data?.flows;
            var daprHttpPort = GetConfigValue("DAPR_HTTP_PORT");
            var daprStateStore = GetConfigValue("DAPR_STATE_STORE_NAME");
            string url="http://localhost:"+daprHttpPort+"/v1.0/state/"+daprStateStore;
            httpTask.SetUrl(url);
            // Prepare domain data for Dapr State Store API
            // Dapr State Store expects array of state items
            var stateItem = new
            {
                key = $"domain:{domainName}",
                value = new
                {
                    name = domainName,
                    baseUrl = baseUrl,
                    healthUrl = healthUrl,
                    appId = appId,
                    isHealthy = true,
                    updatedAt = DateTime.UtcNow.ToString("o"),
                    // flows = flows ?? new { }
                }
            };

            // Set body for HTTP POST request
            httpTask.SetBody(new[] { stateItem });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "register-domain-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // Dapr State Store API returns 204 No Content on success
            // Check HTTP status code from context
            var statusCode = context.Body?.statusCode ?? 500;
            var isSuccess = statusCode >= 200 && statusCode < 300;

            // Successful registration (204 No Content is success for state store)
            if (isSuccess || statusCode == 204)
            {
                return new ScriptResponse
                {
                    Key = "domain-registered",
                    Data = new
                    {
                        domainRegistration = new
                        {
                            success = true,
                            operation = "register",
                            registeredAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "registered", "redis", "success" }
                };
            }
            // Registration failed
            else
            {
                return new ScriptResponse
                {
                    Key = "domain-registration-failed",
                    Data = new
                    {
                        domainRegistration = new
                        {
                            success = false,
                            operation = "register",
                            error = "Failed to register domain",
                            statusCode = statusCode,
                            registeredAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "registration-failed", "redis", "error" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "domain-registration-exception",
                Data = new
                {
                    domainRegistration = new
                    {
                        success = false,
                        operation = "register",
                        error = "Exception during domain registration",
                        errorDescription = ex.Message,
                        registeredAt = DateTime.UtcNow
                    }
                },
                Tags = new[] { "domain", "exception", "error" }
            };
        }
    }
}

