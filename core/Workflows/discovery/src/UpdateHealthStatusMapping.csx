using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;
/// <summary>
/// Update Health Status Mapping - Updates health status in Redis, preserving other fields
/// </summary>
public class UpdateHealthStatusMapping :ScriptBase, IMapping
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
            var domainData = context.Instance?.Data?.domainData;
            var healthCheck = context.Instance?.Data?.healthCheck;
            
            // Determine health status
            bool isHealthy = false;
            if (healthCheck != null)
            {
                isHealthy = healthCheck.isHealthy == true;
            }
            // If no healthCheck data but we have domainData, mark as unhealthy
            // This happens when health URL is missing or health check couldn't be performed
            else if (domainData != null)
            {
                isHealthy = false;
            }
            // If no healthCheck and no domainData, check if we're explicitly marking as unhealthy
            else if (context.Instance?.Data?.markUnhealthy == true)
            {
                isHealthy = false;
            }

            // Merge existing domain data with health status update
            var updateData = new Dictionary<string, dynamic>();
            
            // Copy existing domain data to preserve all fields
            if (domainData != null)
            {
                if (domainData.name != null) updateData["name"] = domainData.name;
                if (domainData.baseUrl != null) updateData["baseUrl"] = domainData.baseUrl;
                if (domainData.healthUrl != null) updateData["healthUrl"] = domainData.healthUrl;
                if (domainData.appId != null) updateData["appId"] = domainData.appId;
                if (domainData.flows != null) updateData["flows"] = domainData.flows;
                // Preserve any other fields that might exist
                foreach (var prop in domainData.GetType().GetProperties())
                {
                    var propName = prop.Name;
                    if (propName != "isHealthy" && propName != "updatedAt" && 
                        propName != "name" && propName != "baseUrl" && 
                        propName != "healthUrl" && propName != "appId" && propName != "flows")
                    {
                        var value = prop.GetValue(domainData);
                        if (value != null)
                        {
                            updateData[propName] = value;
                        }
                    }
                }
            }
            
            // Update only health-related fields
            updateData["isHealthy"] = isHealthy;
            updateData["updatedAt"] = DateTime.UtcNow.ToString("o");
            
            // Ensure name is set
            if (domainName != null)
            {
                updateData["name"] = domainName;
            }

            // Prepare state item for Dapr State Store API
            var stateItem = new
            {
                key = $"domain:{domainName}",
                value = updateData
            };

            // Set body for HTTP POST request
            httpTask.SetBody(new[] { stateItem });
            var daprHttpPort = GetConfigValue("DAPR_HTTP_PORT");
            var daprStateStore = GetConfigValue("DAPR_STATE_STORE_NAME");
            string url="http://localhost:"+daprHttpPort+"/v1.0/state/"+daprStateStore;
            httpTask.SetUrl(url);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "update-health-status-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // Dapr State Store API returns 204 No Content on success
            var statusCode = context.Body?.statusCode ?? 500;
            var isSuccess = statusCode >= 200 && statusCode < 300;

            // Successful update (204 No Content is success for state store)
            if (isSuccess || statusCode == 204)
            {
                return new ScriptResponse
                {
                    Key = "health-status-updated",
                    Data = new
                    {
                        healthStatusUpdate = new
                        {
                            success = true,
                            updatedAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "health-check", "updated", "redis", "success" }
                };
            }
            // Update failed
            else
            {
                return new ScriptResponse
                {
                    Key = "health-status-update-failed",
                    Data = new
                    {
                        healthStatusUpdate = new
                        {
                            success = false,
                            error = "Failed to update health status",
                            statusCode = statusCode,
                            updatedAt = DateTime.UtcNow
                        }
                    },
                    Tags = new[] { "domain", "health-check", "update-failed", "redis", "error" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "health-status-update-exception",
                Data = new
                {
                    healthStatusUpdate = new
                    {
                        success = false,
                        error = "Exception during health status update",
                        errorDescription = ex.Message,
                        updatedAt = DateTime.UtcNow
                    }
                },
                Tags = new[] { "domain", "health-check", "exception", "error" }
            };
        }
    }
}

