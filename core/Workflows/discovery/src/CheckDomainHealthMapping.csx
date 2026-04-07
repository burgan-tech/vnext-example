using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Check Domain Health Mapping - Checks health for one domain at a time (sequential processing)
/// </summary>
public class CheckDomainHealthMapping : IMapping
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

            // Get current domain from pendingDomains array using currentDomainIndex
            var pendingDomains = context.Instance?.Data?.pendingDomains as List<object>;
            var currentDomainIndex = Convert.ToInt32(context.Instance?.Data?.currentDomainIndex ?? 0);

            if (pendingDomains == null || currentDomainIndex >= pendingDomains.Count)
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "page-complete",
                    Data = new { error = "No more domains to process" }
                });
            }

            // Get current domain
            var currentDomain = pendingDomains[currentDomainIndex] as Dictionary<string, object>;
            if (currentDomain == null)
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "page-complete",
                    Data = new { error = "Invalid domain data" }
                });
            }

            var healthUrl = currentDomain.ContainsKey("healthUrl") ? currentDomain["healthUrl"]?.ToString() : null;

            if (string.IsNullOrEmpty(healthUrl))
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "health-check-error",
                    Data = new { error = "Health URL is missing" }
                });
            }

            // Set the health URL for the HTTP request
            httpTask.SetUrl(healthUrl);

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
            var pendingDomains = context.Instance?.Data?.pendingDomains as List<object> ?? new List<object>();
            var currentDomainIndex = Convert.ToInt32(context.Instance?.Data?.currentDomainIndex ?? 0);
            var healthyDomains = context.Instance?.Data?.HealthyDomains as List<object> ?? new List<object>();
            var notHealthyDomains = context.Instance?.Data?.NotHealthyDomains as List<object> ?? new List<object>();

            // Get current domain
            if (currentDomainIndex >= pendingDomains.Count)
            {
                return new ScriptResponse
                {
                    Key = "page-complete",
                    Data = new
                    {
                        HealthyDomains = healthyDomains,
                        NotHealthyDomains = notHealthyDomains,
                        processedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "health-check", "complete" }
                };
            }

            var currentDomain = pendingDomains[currentDomainIndex] as Dictionary<string, object>;
            var domainName = currentDomain?.ContainsKey("domainName") == true ? currentDomain["domainName"]?.ToString() : null;

            // Determine health status based on HTTP status code
            // 200 = healthy, otherwise unhealthy
            bool isHealthy = statusCode == 200;

            // Add to appropriate list
            if (isHealthy)
            {
                if (!healthyDomains.Contains(domainName))
                {
                    healthyDomains.Add(domainName);
                }
            }
            else
            {
                if (!notHealthyDomains.Contains(domainName))
                {
                    notHealthyDomains.Add(domainName);
                }
            }

            // Increment index for next domain
            var nextIndex = currentDomainIndex + 1;
            var hasMoreDomains = nextIndex < pendingDomains.Count;

            return new ScriptResponse
            {
                Key = hasMoreDomains ? "has-more-domains" : "page-complete",
                Data = new
                {
                    currentDomainIndex = nextIndex,
                    HealthyDomains = healthyDomains,
                    NotHealthyDomains = notHealthyDomains,
                    isHealthy = isHealthy,
                    domainName = domainName,
                    statusCode = statusCode,
                    checkedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "health-check", isHealthy ? "healthy" : "unhealthy", hasMoreDomains ? "continue" : "complete" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "health-check-error",
                Data = new
                {
                    error = "Exception during health check",
                    errorDescription = ex.Message,
                    checkedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "health-check", "exception", "error" }
            };
        }
    }
}

