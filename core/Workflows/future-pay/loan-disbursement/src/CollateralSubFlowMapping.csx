using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// State 5 (collateral-establishment, stateType 4) SubFlow mapping.
/// Seeds the collateral subflow from the application/assessment context and, on completion,
/// merges the collateral detail (type, value, status) into the master `collateral` section.
/// OutputHandler reads the child instance data straight off context.Body — there is no
/// StandardTaskResponse envelope to unwrap for a SubFlow.
/// </summary>
public class CollateralSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var application = Get(data, "application") as IDictionary<string, object>;
        var assessment = Get(data, "assessment") as IDictionary<string, object>;

        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object>
            {
                { "customerId", ToStr(Get(application, "customerId")) ?? ToStr(Get(data, "customerId")) ?? string.Empty },
                { "approvedLimit", Get(assessment, "approvedLimit") ?? Get(data, "approvedLimit") ?? (object)0 },
                { "productType", ToStr(Get(application, "productType")) ?? ToStr(Get(data, "productType")) ?? string.Empty }
            }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // context.Body IS the completed child instance data — no StandardTaskResponse envelope.
        // The child only carries the keys its sign-contract payload supplied, so every read is
        // guarded: a missing member on the underlying dynamic object throws rather than yielding null.
        dynamic result = context.Body;

        var collateralType = ToStr(TryRead(() => result?.collateralType));
        var collateralValue = TryRead(() => result?.collateralValue);
        var establishmentStatus = ToStr(TryRead(() => result?.establishmentStatus));

        var collateral = CreateObject();
        SetProperty(collateral, "collateralType", collateralType);
        SetProperty(collateral, "collateralValue", collateralValue);
        SetProperty(collateral, "establishmentStatus", establishmentStatus);

        var data = CreateObject();
        SetProperty(data, "collateral", collateral);

        LogInformation($"CollateralSubFlowMapping: type={collateralType}, value={collateralValue}, status={establishmentStatus}");

        return Task.FromResult(new ScriptResponse { Data = data, Tags = new[] { "subflow", "collateral" } });
    }

    private static object TryRead(Func<object> read)
    {
        try { return read(); } catch { return null; }
    }

    private static object Get(IDictionary<string, object> source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value : null;

    private static string ToStr(object value)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
