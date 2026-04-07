using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;
/// <summary>
/// Check Domain Exists Mapping - Queries domain workflow instance for existence using DaprServiceTask
/// Endpoint: /api/v1/discovery/workflows/domain/instances/{domainName}/functions/data
/// </summary>
public class CheckDomainExistsMapping :ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            string? appId = GetConfigValue("OrchestrationApi:AppId");
            string? daprAppId = GetConfigValue("DAPR_APP_ID");
           
            var  daprServiceTask= task as DaprServiceTask;
            if (daprServiceTask == null)
            {
                throw new InvalidOperationException("Task must be a DaprServiceTask");
            }
            LogInformation("AppId ariyorum dapr:"+daprAppId);
            LogInformation("AppId ariyorum exec set:"+appId);
            daprServiceTask.SetAppId(daprAppId);
            var domainName = context.Instance?.Data?.domainName;
            
            // Replace {domainName} placeholder in methodName
            // MethodName format: /api/v1/discovery/workflows/domain/instances/{domainName}/functions/data
            if (!string.IsNullOrEmpty(domainName?.ToString()))
            {
                var methodName = daprServiceTask.MethodName?.Replace("{domainName}", domainName.ToString()) ?? 
                                $"/api/v1/discovery/workflows/domain/instances/{domainName}/functions/data";
                daprServiceTask.SetMethodName(methodName);
            }

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "check-domain-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // Workflow Instance Data API returns:
            // - 200 OK with data (including etag) if instance exists
            // - 404 Not Found if instance doesn't exist
            var statusCode = context.Body?.statusCode ?? 500;
            var responseData = context.Body;
            
            // Domain workflow instance exists (200 OK with data)
            if (statusCode == 200 && responseData != null)
            {
                // Extract etag from response body (root level, lowercase)
                // Response format: { "data": { "appId": "...", "healthUrl": "...", "domainName": "...", "baseUrl": "..." }, "etag": "...", "extensions": {} }
                var eTag = responseData.data?.etag?.ToString();
                
                // Extract healthUrl and appId from existing domain data
                // Domain data structure: direct properties in data object (no underscore prefix)
                var existingHealthUrl = responseData.data?.data?.healthUrl?.ToString();
                var existingBaseUrl = responseData.data?.data?.baseUrl?.ToString();
                var existingAppId = responseData.data?.data?.appId?.ToString();
                
                return new ScriptResponse
                {
                    Key = "domain-exists",
                    Data = new
                    {
                        domainExists = true,
                        existingDomain = responseData,
                        instanceETag = eTag,
                        existingHealthUrl = existingHealthUrl,
                        existingBaseUrl=existingBaseUrl,
                        existingAppId = existingAppId,
                        checkedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "exists", "workflow", "instance", "found" }
                };
            }
            // Domain workflow instance not found (404 Not Found)
            else if (statusCode == 404)
            {
                return new ScriptResponse
                {
                    Key = "domain-not-exists",
                    Data = new
                    {
                        domainExists = false,
                        existingDomain = (object?)null,
                        instanceETag = (string?)null,
                        existingHealthUrl = (string?)null,
                        existingAppId = (string?)null,
                        checkedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "not-exists", "workflow", "instance", "not-found" }
                };
            }
            // Unexpected status code
            else
            {
                return new ScriptResponse
                {
                    Key = "domain-check-exception",
                    Data = new
                    {
                        domainExists = false,
                        existingDomain = (object?)null,
                        instanceETag = (string?)null,
                        existingHealthUrl = (string?)null,
                        existingAppId = (string?)null,
                        error = "Unexpected status code during domain instance check",
                        statusCode = statusCode,
                        check=responseData.data,
                        checkedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "domain", "exception", "error" }
                };
            }
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "domain-check-exception",
                Data = new
                {
                    domainExists = false,
                    existingDomain = (object?)null,
                    instanceETag = (string?)null,
                    existingHealthUrl = (string?)null,
                    existingAppId = (string?)null,
                    error = "Exception during domain instance check",
                    errorDescription = ex.Message,
                    checkedAt = DateTime.UtcNow
                },
                Tags = new[] { "domain", "exception", "error" }
            };
        }
    }
}

