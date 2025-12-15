using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Get Contract Documents Mapping - Retrieves document list from API
/// </summary>
public class GetContractDocumentsMapping : IMapping
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

            var groupCode = context.Instance?.Data?.groupCode?.ToString() ?? "";
            
            // Mockoon API expects "groupCode" field
            var requestBody = new
            {
                groupCode = groupCode,
                requestId = Guid.NewGuid().ToString(),
                timestamp = DateTime.UtcNow
            };

            httpTask.SetBody(requestBody);

            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Request-Id"] = Guid.NewGuid().ToString(),
                ["X-Correlation-Id"] = context.Instance.Id.ToString()
            };
            httpTask.SetHeaders(headers);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-documents-input-error",
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
                // Mockoon API returns "contracts" array, not "documents"
                var contracts = response?.data?.data?.contracts ?? new object[] { };
                var contractList = new List<object>();
                
                if (contracts != null)
                {
                    foreach (var contract in contracts)
                    {
                        contractList.Add(contract);
                    }
                }

                // totalContracts from API response or calculated from list
                var totalContracts = response?.data?.data?.totalContracts ?? contractList.Count;

                return new ScriptResponse
                {
                    Key = "get-documents-success",
                    Data = new
                    {
                        documents = contractList.ToArray(),
                        totalDocuments = (int)totalContracts,
                        groupCode = response?.data?.data?.groupCode,
                        retrievedAt = response?.data?.data?.retrievedAt,
                        currentDocumentIndex = 0,
                        readyCount = 0,
                        approvedCount = 0,
                        documentInstances = new object[] { },
                        documentsLoadedAt = DateTime.UtcNow
                    },
                    Tags = new[] { "contract", "documents-loaded" }
                };
            }

            return new ScriptResponse
            {
                Key = "get-documents-failed",
                Data = new
                {
                    error = response?.data?.error?.message ?? response?.error ?? "Failed to get documents",
                    errorCode = response?.data?.error?.code,
                    statusCode = statusCode,
                    documents = new object[] { },
                    totalDocuments = 0
                }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "get-documents-exception",
                Data = new
                {
                    error = ex.Message,
                    documents = new object[] { },
                    totalDocuments = 0
                }
            };
        }
    }
}

