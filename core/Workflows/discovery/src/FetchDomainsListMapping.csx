using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;
/// <summary>
/// Fetch Domains List Mapping - Fetches domain list from API endpoint
/// </summary>
public class FetchDomainsListMapping :ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var appId = GetConfigValue("OrchestrationApi:AppId");
            var httpTask = task as HttpTask;
            if (httpTask == null)
            {
                throw new InvalidOperationException("Task must be an HttpTask");
            }

            // Get URL from context (could be initial URL or next page URL)
            var url = context.Instance?.Data?.currentPageUrl?.ToString() ?? 
                     context.Instance?.Data?.nextPageUrl?.ToString() ??
                     "{{discoveryBaseUrl}}/api/v1/discovery/workflows/domain/functions/data?Page=1&PageSize=10";

            // Set the URL for the HTTP request
            httpTask.SetUrl(url);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "fetch-domains-error",
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

            // Successful response (200 OK)
            if (statusCode >= 200 && statusCode < 300 && responseData != null)
            {
                // Extract items and links from response
                var items = responseData.items;
                var links = responseData.links;
                var nextUrl = links?.next?.ToString();

                return new ScriptResponse
                {
                    Key = "domains-fetched",
                    Data = new
                    {
                        items = items,
                        links = links,
                        nextPageUrl = nextUrl,
                        currentPageUrl = context.Instance?.Data?.currentPageUrl,
                        fetchedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "fetch", "success", "api" }
                };
            }
            // Error response
            else
            {
                return new ScriptResponse
                {
                    Key = "fetch-failed",
                    Data = new
                    {
                        error = "Failed to fetch domains list",
                        statusCode = statusCode,
                        responseData = responseData,
                        fetchedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "fetch", "error", "failed" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "fetch-failed",
                Data = new
                {
                    error = "Exception during domain list fetch",
                    errorDescription = ex.Message,
                    fetchedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "fetch", "exception", "error" }
            };
        }
    }
}

