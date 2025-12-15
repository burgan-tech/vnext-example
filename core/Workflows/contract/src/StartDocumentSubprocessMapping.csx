using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start Document Subprocess Mapping - Starts subprocess for current document
/// </summary>
public class StartDocumentSubprocessMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var startTask = task as SubProcessTask;
            if (startTask == null)
            {
                throw new InvalidOperationException("Task must be a StartTask");
            }

            // Configure target workflow
            startTask.SetDomain("core");
            startTask.SetFlow("contract-document-subflow");
            startTask.SetVersion("1.0.0");
            
            // Get current document
            var currentIndex = (int)(context.Instance?.Data?.currentDocumentIndex ?? 0);
            var documents = context.Instance?.Data?.documents;
            
            object currentDocument = null;
            if (documents != null)
            {
                var docList = new List<object>();
                foreach (var doc in documents)
                {
                    docList.Add(doc);
                }
                if (currentIndex < docList.Count)
                {
                    currentDocument = docList[currentIndex];
                }
            }

            // Generate unique key for subprocess
            var subprocessKey = $"{context.Instance?.Key}-doc-{currentIndex}";
            startTask.SetKey(subprocessKey);
            startTask.SetTags(new[] { "contract", "document", "subprocess" });

            // Prepare initialization body
            var initBody = new
            {
                parentInstanceId = context.Instance?.Id,
                parentInstanceKey = context.Instance?.Key,
                parentWorkflowKey = context.Workflow?.Key,
                document = currentDocument,
                documentIndex = currentIndex,
                groupCode = context.Instance?.Data?.groupCode,
                startedAt = DateTime.UtcNow
            };
            startTask.SetBody(initBody);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "start-subprocess-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;
            var currentIndex = (int)(context.Instance?.Data?.currentDocumentIndex ?? 0);

            if (response?.isSuccess == true)
            {
                // Store subprocess instance info
                var existingInstances = context.Instance?.Data?.documentInstances ?? new object[] { };
                var instanceList = new List<object>();
                foreach (var inst in existingInstances)
                {
                    instanceList.Add(inst);
                }
                
                // Get current document for contractId reference
                var documents = context.Instance?.Data?.documents;
                object currentDoc = null;
                if (documents != null)
                {
                    var docList = new List<object>();
                    foreach (var d in documents)
                    {
                        docList.Add(d);
                    }
                    if (currentIndex < docList.Count)
                    {
                        currentDoc = docList[currentIndex];
                    }
                }

                instanceList.Add(new
                {
                    instanceId = response?.data?.id,
                    contractId = ((dynamic)currentDoc)?.contractId,
                    contractName = ((dynamic)currentDoc)?.contractName,
                    documentIndex = currentIndex,
                    startedAt = DateTime.UtcNow,
                    status = "started"
                });

                return new ScriptResponse
                {
                    Key = "subprocess-started",
                    Data = new
                    {
                        documentInstances = instanceList.ToArray(),
                        currentDocumentIndex = currentIndex + 1,
                        lastSubprocessId = response?.data?.id
                    },
                    Tags = new[] { "contract", "subprocess-started" }
                };
            }

            return new ScriptResponse
            {
                Key = "subprocess-failed",
                Data = new
                {
                    error = response?.errorMessage ?? "Failed to start subprocess"
                }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "subprocess-exception",
                Data = new { error = ex.Message }
            };
        }
    }
}

