using System;
using System.Dynamic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

public class DaprPubSubMapping : ScriptBase, IMapping
{
    /// <summary>
    /// TaskExecutionEngine, task sonucunu JsonSerializer ile yazarken ExpandoObject içindeki
    /// JsonElement referanslarını serileştiremiyor ("Operation is not valid due to the current state of the object").
    /// context.Body.isSuccess / statusCode runtime'da JsonElement olarak gelebilir — CLR tipine çeviriyoruz.
    /// </summary>
    private static bool CoerceBool(object o)
    {
        if (o == null)
            return false;
        if (o is bool b)
            return b;
        if (o is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(je.GetString(), out var p) && p,
                JsonValueKind.Number => je.TryGetInt32(out var n) ? n != 0 : false,
                _ => false,
            };
        }
        if (o is string s)
            return bool.TryParse(s, out var x) && x;
        try
        {
            return Convert.ToBoolean(o);
        }
        catch
        {
            return false;
        }
    }

    private static int? CoerceInt32(object o)
    {
        if (o == null)
            return null;
        if (o is int i)
            return i;
        if (o is long l)
            return checked((int)l);
        if (o is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n))
            return n;
        if (o is string s && int.TryParse(s, out var p))
            return p;
        try
        {
            return Convert.ToInt32(o);
        }
        catch
        {
            return null;
        }
    }

    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var pubsubTask = (task as DaprPubSubTask)!;
        if (pubsubTask == null)
            throw new InvalidOperationException("Task must be a DaprPubSubTask");

        // Vault'ta DaprPubSubName varsa onu kullan, yoksa task JSON'daki default (test-pubsub) gecerli kalir.
        // Boylece development'te Vault config'i opsiyonel; farkli ortamda (ornegin vnext-pubsub)
        // sadece Vault'a key eklemek yeterli.
        string vaultValue = GetConfigValue("DaprPubSubName");
        if (!string.IsNullOrWhiteSpace(vaultValue))
        {
            pubsubTask.SetPubSubName(vaultValue);
            LogInformation($"DaprPubSubMapping: Vault override pubSubName = {vaultValue}");
        }
        else
        {
            LogInformation(
                $"DaprPubSubMapping: Vault'ta DaprPubSubName yok, task JSON default kullaniliyor"
            );
        }

        // Publish icin `data` null olamaz (ArgumentNullException). Task JSON'da data olsa bile
        // eski publish / sirasi yüzünden güven vermez; mapping ile garanti et.
        var instanceData = context.Instance.Data;
        dynamic messageData = new ExpandoObject();
        messageData.eventType = "IntegrationTest";
        messageData.source = "extended-tasks-test-workflow";
        messageData.timestamp = DateTime.UtcNow.ToString("o");
        if (HasProperty(instanceData, "testId"))
            messageData.testId = instanceData.testId;
        pubsubTask.SetData(messageData);
        LogInformation("DaprPubSubMapping: SetData tamamlandi");

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId"))
            result.testId = data.testId;
        if (HasProperty(data, "initCompleted"))
            result.initCompleted = data.initCompleted;

        result.taskResults = new ExpandoObject();
        if (HasProperty(data, "taskResults"))
        {
            if (HasProperty(data.taskResults, "daprHttp"))
                result.taskResults.daprHttp = data.taskResults.daprHttp;
            if (HasProperty(data.taskResults, "daprService"))
                result.taskResults.daprService = data.taskResults.daprService;
            if (HasProperty(data.taskResults, "daprBinding"))
                result.taskResults.daprBinding = data.taskResults.daprBinding;
            if (HasProperty(data.taskResults, "notification"))
                result.taskResults.notification = data.taskResults.notification;
            if (HasProperty(data.taskResults, "triggerTransition"))
                result.taskResults.triggerTransition = data.taskResults.triggerTransition;
            if (HasProperty(data.taskResults, "getInstances"))
                result.taskResults.getInstances = data.taskResults.getInstances;
            if (HasProperty(data.taskResults, "subprocess"))
                result.taskResults.subprocess = data.taskResults.subprocess;
        }

        result.taskResults.daprPubSub = new ExpandoObject();
        result.taskResults.daprPubSub.completed = true;
        result.taskResults.daprPubSub.executedAt = DateTime.UtcNow.ToString("o");

        // Skill vnext-workflow-creation §6.4: "completed = true" literal mapping çağrıldığını
        // gösterir, task'in başarılı olduğunu kanıtlamaz. Dapr PubSub task'inde fire-and-forget
        // olduğundan dönen veri yok; ama runtime context.Body zarfında task sonucu için
        // isSuccess (bool) ve statusCode (int) alanlarını döner. Bunları parent attributes'a
        // yazıp testte assert ederek "task gerçekten publish edildi" iddiasını kanıtlıyoruz.
        var taskResponse = context.Body;
        if (taskResponse != null)
        {
            if (HasProperty(taskResponse, "isSuccess"))
            {
                dynamic tr = taskResponse;
                result.taskResults.daprPubSub.published = CoerceBool(tr.isSuccess);
            }
            if (HasProperty(taskResponse, "statusCode"))
            {
                dynamic tr = taskResponse;
                var code = CoerceInt32(tr.statusCode);
                if (code.HasValue)
                    result.taskResults.daprPubSub.statusCode = code.Value;
            }
        }

        LogInformation("DaprPubSubMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
