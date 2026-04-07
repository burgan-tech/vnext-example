using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Scripting;

/// <summary>
/// Process Page Mapping - Extracts domains from page items and creates pendingDomains queue
/// </summary>
public class ProcessPageMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // No input handling needed for this mapping
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var items = context.Instance?.Data?.items;
            
            if (items == null)
            {
                return new ScriptResponse
                {
                    Key = "no-domains",
                    Data = new
                    {
                        pendingDomains = new List<object>(),
                        processedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "process", "empty" }
                };
            }

            // Initialize result lists if first page
            var healthyDomains = context.Instance?.Data?.HealthyDomains as List<object> ?? new List<object>();
            var notHealthyDomains = context.Instance?.Data?.NotHealthyDomains as List<object> ?? new List<object>();

            // Extract domains from items array
            var pendingDomains = new List<object>();
            
            // Iterate through items and extract domainName and healthUrl
            if (items is IEnumerable<object> itemsEnumerable)
            {
                foreach (var item in itemsEnumerable)
                {
                    if (item is Dictionary<string, object> itemDict)
                    {
                        var data = itemDict.ContainsKey("data") ? itemDict["data"] : null;
                        if (data is Dictionary<string, object> dataDict)
                        {
                            var domainName = dataDict.ContainsKey("domainName") ? dataDict["domainName"]?.ToString() : null;
                            var healthUrl = dataDict.ContainsKey("healthUrl") ? dataDict["healthUrl"]?.ToString() : null;

                            if (!string.IsNullOrEmpty(domainName) && !string.IsNullOrEmpty(healthUrl))
                            {
                                pendingDomains.Add(new Dictionary<string, object>
                                {
                                    { "domainName", domainName },
                                    { "healthUrl", healthUrl }
                                });
                            }
                        }
                    }
                }
            }

            // Initialize currentDomainIndex
            var currentDomainIndex = 0;

            return new ScriptResponse
            {
                Key = pendingDomains.Count > 0 ? "to-check-health" : "no-domains",
                Data = new
                {
                    pendingDomains = pendingDomains,
                    currentDomainIndex = currentDomainIndex,
                    HealthyDomains = healthyDomains,
                    NotHealthyDomains = notHealthyDomains,
                    processedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "process", "success" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "process-page-error",
                Data = new
                {
                    error = "Exception during page processing",
                    errorDescription = ex.Message,
                    processedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "process", "exception", "error" }
            };
        }
    }
}

