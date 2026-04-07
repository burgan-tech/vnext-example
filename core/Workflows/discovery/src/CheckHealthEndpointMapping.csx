using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Check Health Endpoint Mapping - Performs HTTP GET to health endpoint and determines health status
/// </summary>
public class CheckHealthEndpointMapping : IMapping
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

            var fullHealthUrl = context.Instance?.Data?.fullHealthUrl;
            
            if (string.IsNullOrEmpty(fullHealthUrl))
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "health-check-error",
                    Data = new { error = "Health URL is missing" }
                });
            }

            // Set the health URL for the HTTP request
            httpTask.SetUrl(fullHealthUrl);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "health-check-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var statusCode = context.Body?.statusCode ?? 500;
            var responseData = context.Body;
            
            // Determine health status based on HTTP status code
            // 200-299: Healthy
            // 300-399: Degraded (redirects, etc.)
            // 400-599: Unhealthy
            // Timeout or connection error: Unhealthy
            
            string healthStatus = null;
            bool isHealthy = false;
            bool checkPassed = false;

            if (statusCode >= 200 && statusCode < 300)
            {
                healthStatus = "Healthy";
                isHealthy = true;
                checkPassed = true;
            }
            else if (statusCode >= 300 && statusCode < 400)
            {
                healthStatus = "Degraded";
                isHealthy = false;
                checkPassed = false;
            }
            else if (statusCode >= 400)
            {
                healthStatus = "Unhealthy";
                isHealthy = false;
                checkPassed = false;
            }
            else
            {
                // Timeout or connection error
                healthStatus = "Unhealthy";
                isHealthy = false;
                checkPassed = false;
            }

            if (checkPassed)
            {
                return new ScriptResponse
                {
                    Key = "health-check-passed",
                    Data = new
                    {
                        healthCheck = new
                        {
                            status = healthStatus,
                            isHealthy = isHealthy,
                            statusCode = statusCode,
                            responseData = responseData,
                            checkedAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "health-check", "passed", "healthy", "success" }
                };
            }
            else
            {
                return new ScriptResponse
                {
                    Key = "health-check-failed",
                    Data = new
                    {
                        healthCheck = new
                        {
                            status = healthStatus,
                            isHealthy = isHealthy,
                            statusCode = statusCode,
                            error = $"Health check failed with status code {statusCode}",
                            responseData = responseData,
                            checkedAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "health-check", "failed", "unhealthy", "error" }
                };
            }
        }
        catch (Exception ex)
        {
            // Timeout or connection error
            return new ScriptResponse
            {
                Key = "health-check-failed",
                Data = new
                {
                    healthCheck = new
                    {
                        status = "Unhealthy",
                        isHealthy = false,
                        error = "Exception during health check",
                        errorDescription = ex.Message,
                        checkedAt = DateTime.UtcNow
                    }
                },
                Tags = new[] { "domain", "health-check", "failed", "exception", "error" }
            };
        }
    }
}

