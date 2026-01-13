using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// GetInstances Task Mapping - Retrieves a list of workflow instances with filtering and pagination
/// Test case: Verify GetInstancesTask functionality including filter, pagination, and sorting
/// 
/// Filter Format (GraphQL-like JSON structure):
/// ?filter={"attributes":{"fieldName":{"operator":"value"}}}
/// 
/// Supported operators: eq, neq, gt, gte, lt, lte, contains, startsWith, endsWith
/// 
/// Examples:
/// - Single filter: {"attributes":{"clientId":{"eq":122}}}
/// - Multiple filters: {"attributes":{"clientId":{"eq":122},"testValue":{"gt":2}}}
/// - String filter: {"attributes":{"status":{"eq":"active"}}}
/// </summary>
public class GetInstancesMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            LogInformation("GetInstances Test - Starting GetInstances task configuration");

            var getInstancesTask = task as GetInstancesTask;
            if (getInstancesTask == null)
            {
                throw new InvalidOperationException("Task must be a GetInstancesTask");
            }

            // Configure target workflow - query instances from task-test-subflow
            getInstancesTask.SetDomain("core");
            getInstancesTask.SetFlow("task-test-subflow");

            // Configure pagination
            getInstancesTask.SetPage(1);
            getInstancesTask.SetPageSize(10);

            // Configure sorting - newest first
            // getInstancesTask.SetSort("-CreatedAt");

            // Build filter in GraphQL-like JSON format
            // Format: {"attributes":{"fieldName":{"operator":"value"}}}
            // API call example: GET /api/v1.0/core/workflows/task-test-subflow/instances?filter={"attributes":{"parentInstanceId":{"eq":"abc-123"}}}
            
            // Get parent instance data to build meaningful filters
            var parentInstanceId = context.Instance.Id.ToString();
            var testId = context.Instance.Data?.testId.ToString();

            // Build attribute filters dictionary
            // Each key is a field name, each value is a dictionary of {operator: value}
            var attributeFilters = new Dictionary<string, Dictionary<string, object>>();

            // Filter 1: Find instances by parentInstanceId using "eq" operator
            if (!string.IsNullOrEmpty(parentInstanceId))
            {
                attributeFilters["parentInstanceId"] = new Dictionary<string, object>
                {
                    { "eq", parentInstanceId }
                };
            }

            // Filter 2: Find instances by testId using "eq" operator
            if (!string.IsNullOrEmpty(testId))
            {
                attributeFilters["testId"] = new Dictionary<string, object>
                {
                    { "eq", testId }
                };
            }

            // Filter 3: Find instances started by task-test-workflow using "eq" operator
            attributeFilters["startedBy"] = new Dictionary<string, object>
            {
                { "eq", "task-test-workflow" }
            };

            // Build the complete filter JSON object
            // Result format: {"attributes":{"parentInstanceId":{"eq":"..."},"testId":{"eq":"..."},"startedBy":{"eq":"task-test-workflow"}}}
            if (attributeFilters.Count > 0)
            {
                var filterObject = new Dictionary<string, object>
                {
                    { "attributes", attributeFilters }
                };

                // Serialize to JSON string
                var filterJson = JsonSerializer.Serialize(filterObject);
                
                // SetFilter expects a string array - pass the complete filter as single element
                getInstancesTask.SetFilter([filterJson]);
                
                LogInformation("GetInstances Test - Applied JSON filter: {0}", 
                    args: [filterJson]);
            }
            else
            {
                LogInformation("GetInstances Test - No filters applied, retrieving all instances");
            }

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            LogError("GetInstances Test - Failed to configure GetInstances task: {0}", 
                args: [ex.Message]);
            
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-instances-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("GetInstances Test - Processing GetInstances response");

            var response = context.Body;
            var isSuccess = response?.isSuccess == true;
            var statusCode = response?.statusCode ?? 500;

            if (isSuccess)
            {
                // Extract response data
                var instances = response?.data.items;

                LogInformation("GetInstances Test - Successfully retrieved {0} instance(s)", 
                    args: new object?[] { instances?.Count ?? 0 });

                // Build instance summary for verification
                var instanceSummary = new List<object>();
                if (instances != null)
                {
                    foreach (var instance in instances)
                    {
                        instanceSummary.Add(new
                        {
                            testId = instance?.data?.testId
                        });
                    }
                }

                return new ScriptResponse
                {
                    Key = "get-instances-success",
                    Data = new
                    {
                        getInstancesResult = new
                        {
                            success = true,
                            taskType = "GetInstances",
                            description = "Retrieved workflow instances with JSON filter, pagination, and sorting",
                            filter = new
                            {
                                applied = true,
                                format = "GraphQL-like JSON",
                                targetDomain = "core",
                                targetFlow = "task-test-subflow"
                            },
                            instances = instanceSummary,
                            instanceCount = instances?.Count ?? 0,
                            retrievedAt = DateTime.UtcNow
                        }
                    },
                    Tags = ["task-test", "get-instances", "filter-test", "json-filter", "success"]
                };
            }

            // Handle failure
            LogWarning("GetInstances Test - Failed to retrieve instances: {0}", 
                args: [response?.errorMessage ?? "Unknown error"]);

            return new ScriptResponse
            {
                Key = "get-instances-failed",
                Data = new
                {
                    getInstancesResult = new
                    {
                        success = false,
                        taskType = "GetInstances",
                        statusCode = statusCode,
                        error = response?.errorMessage ?? "Failed to retrieve instances",
                        failedAt = DateTime.UtcNow
                    }
                },
                Tags = ["task-test", "get-instances", "failed"]
            };
        }
        catch (Exception ex)
        {
            LogError("GetInstances Test - Output handler exception: {0}", 
                args: [ex.Message]);
            
            return new ScriptResponse
            {
                Key = "get-instances-exception",
                Data = new
                {
                    getInstancesResult = new
                    {
                        success = false,
                        taskType = "GetInstances",
                        error = ex.Message
                    }
                }
            };
        }
    }
}
