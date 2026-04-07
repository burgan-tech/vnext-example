using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;
/// <summary>
/// Get Domain Health URL Mapping - Queries Redis for domain and extracts health URL
/// </summary>
public class GetDomainHealthUrlMapping :ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            //DAPR_HTTP_PORT
            var daprHttpPort = GetConfigValue("DAPR_HTTP_PORT");
            if (httpTask == null)
            {
                throw new InvalidOperationException("Task must be an HttpTask");
            }

            var domainName = context.Instance?.Data?.domainName;
            var daprStateStore = GetConfigValue("DAPR_STATE_STORE_NAME");
            string url="http://localhost:"+daprHttpPort+"/v1.0/state/"+daprStateStore+"/domain:"+domainName;
            httpTask.SetUrl(url);
            // // Replace {domainName} placeholder in URL
            // if (httpTask.Url != null)
            // {
            //     httpTask.SetUrl(httpTask.Url.Replace("{domainName}", domainName?.ToString() ?? ""));
            // }

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-health-url-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // Dapr State Store API returns:
            // - 200 OK with value if key exists
            // - 204 No Content if key doesn't exist
            // - 404 Not Found if key doesn't exist (alternative)
            var statusCode = context.Body?.statusCode ?? 500;
            var responseData = context.Body;
            
            // Domain exists (200 OK with data)
            if (statusCode == 200 && responseData != null)
            {
                var baseUrl = responseData.baseUrl;
                var healthUrl = responseData.healthUrl;
                
                // Construct full health URL
                string fullHealthUrl = null;
                if (!string.IsNullOrEmpty(healthUrl))
                {
                    // If healthUrl is absolute, use it directly
                    if (healthUrl.StartsWith("http://") || healthUrl.StartsWith("https://"))
                    {
                        fullHealthUrl = healthUrl;
                    }
                    // If healthUrl is relative, combine with baseUrl
                    else if (!string.IsNullOrEmpty(baseUrl))
                    {
                        var baseUrlTrimmed = baseUrl.TrimEnd('/');
                        var healthUrlTrimmed = healthUrl.TrimStart('/');
                        fullHealthUrl = $"{baseUrlTrimmed}/{healthUrlTrimmed}";
                    }
                    else
                    {
                        // Health URL provided but no base URL
                        return new ScriptResponse
                        {
                            Key = "health-url-missing",
                            Data = new
                            {
                                healthUrlRetrieved = false,
                                error = "Health URL found but base URL is missing",
                                checkedAt = DateTime.UtcNow
                            },
                            Tags = new[] { "domain", "health-check", "url-missing", "error" }
                        };
                    }
                }
                else
                {
                    // Health URL not found in domain data
                    return new ScriptResponse
                    {
                        Key = "health-url-missing",
                        Data = new
                        {
                            healthUrlRetrieved = false,
                            error = "Health URL not found in domain data",
                            checkedAt = DateTime.UtcNow
                        },
                        Tags = new[] { "domain", "health-check", "url-missing", "error" }
                    };
                }

                return new ScriptResponse
                {
                    Key = "health-url-retrieved",
                    Data = new
                    {
                        healthUrlRetrieved = true,
                        fullHealthUrl = fullHealthUrl,
                        baseUrl = baseUrl,
                        healthUrl = healthUrl,
                        domainData = responseData,
                        checkedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "health-check", "url-retrieved", "success" }
                };
            }
            // Domain not found (204 No Content or 404 Not Found)
            else if (statusCode == 204 || statusCode == 404)
            {
                return new ScriptResponse
                {
                    Key = "health-url-missing",
                    Data = new
                    {
                        healthUrlRetrieved = false,
                        error = "Domain not found in Redis",
                        statusCode = statusCode,
                        checkedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "health-check", "url-missing", "not-found" }
                };
            }
            // Unexpected status code
            else
            {
                return new ScriptResponse
                {
                    Key = "health-url-error",
                    Data = new
                    {
                        healthUrlRetrieved = false,
                        error = "Unexpected status code during domain lookup",
                        statusCode = statusCode,
                        checkedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "health-check", "error", "exception" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "health-url-exception",
                Data = new
                {
                    healthUrlRetrieved = false,
                    error = "Exception during health URL retrieval",
                    errorDescription = ex.Message,
                    checkedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "health-check", "exception", "error" }
            };
        }
    }
}

