using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Render Document Mapping - Renders document via HTTP API
/// </summary>
public class RenderDocumentMapping : IMapping
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

            var document = context.Instance?.Data?.document;
            var groupCode = context.Instance?.Data?.groupCode?.ToString() ?? "";
            
            // Mockoon API contract structure: contractId, contractType, contractName, templateId, contractData
            var requestBody = new
            {
                groupCode = groupCode,
                contractId = document?.contractId,
                contractType = document?.contractType,
                contractName = document?.contractName,
                templateId = document?.templateId,
                contractData = document?.contractData ?? new { },
                requestId = Guid.NewGuid().ToString(),
                timestamp = DateTime.UtcNow
            };

            httpTask.SetBody(requestBody);

            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Request-Id"] = Guid.NewGuid().ToString(),
                ["X-Contract-Id"] = document?.contractId?.ToString(),
                ["X-Correlation-Id"] = context.Instance.Id.ToString()
            };
            httpTask.SetHeaders(headers);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "render-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;
            var statusCode = response?.statusCode ?? 500;

            if (statusCode >= 200 && statusCode < 300)
            {
                // Mockoon API returns: documentId, documentUrl, documentSize, pageCount, checksum, expiresAt, renderedAt, format, templateVersion
                return new ScriptResponse
                {
                    Key = "render-success",
                    Data = new
                    {
                        renderStatus = "success",
                        documentId = response?.data?.data?.documentId,
                        documentUrl = response?.data?.data?.documentUrl,
                        documentSize = response?.data?.data?.documentSize,
                        pageCount = response?.data?.data?.pageCount,
                        checksum = response?.data?.data?.checksum,
                        expiresAt = response?.data?.data?.expiresAt,
                        format = response?.data?.data?.format,
                        templateVersion = response?.data?.data?.templateVersion,
                        renderedAt = response?.data?.data?.renderedAt ?? DateTime.UtcNow.ToString("o")
                    },
                    Tags = new[] { "contract", "document", "rendered" }
                };
            }

            return new ScriptResponse
            {
                Key = "render-failed",
                Data = new
                {
                    renderStatus = "failed",
                    error = response?.data?.error?.message ?? response?.error ?? "Failed to render document",
                    errorCode = response?.data?.error?.code,
                    statusCode = statusCode,
                    failedAt = DateTime.UtcNow
                }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "render-exception",
                Data = new
                {
                    renderStatus = "failed",
                    error = ex.Message,
                    failedAt = DateTime.UtcNow
                }
            };
        }
    }
}

