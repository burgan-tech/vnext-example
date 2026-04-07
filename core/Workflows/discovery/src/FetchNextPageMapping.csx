using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Fetch Next Page Mapping - Fetches next page using next URL from response
/// </summary>
public class FetchNextPageMapping : IMapping
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

            // Get next page URL from context
            var nextPageUrl = context.Instance?.Data?.nextPageUrl?.ToString();

            if (string.IsNullOrEmpty(nextPageUrl))
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "no-next-page",
                    Data = new { error = "Next page URL is missing" }
                });
            }

            // Set the next page URL for the HTTP request
            httpTask.SetUrl(nextPageUrl);

            // Update currentPageUrl
            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "fetch-next-page-error",
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
                    Key = "next-page-fetched",
                    Data = new
                    {
                        items = items,
                        links = links,
                        nextPageUrl = nextUrl,
                        currentPageUrl = context.Instance?.Data?.nextPageUrl,
                        fetchedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "fetch", "pagination", "success" }
                };
            }
            // Error response
            else
            {
                return new ScriptResponse
                {
                    Key = "fetch-next-page-failed",
                    Data = new
                    {
                        error = "Failed to fetch next page",
                        statusCode = statusCode,
                        responseData = responseData,
                        fetchedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "fetch", "pagination", "error" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "fetch-next-page-failed",
                Data = new
                {
                    error = "Exception during next page fetch",
                    errorDescription = ex.Message,
                    fetchedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "fetch", "pagination", "exception", "error" }
            };
        }
    }
}

