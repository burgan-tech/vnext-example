using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Render Success Rule - Checks if document render was successful
/// </summary>
public class RenderSuccessRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        try
        {
            var renderStatus = context.Instance?.Data?.renderStatus?.ToString();
            return renderStatus == "success";
        }
        catch (Exception)
        {
            return false;
        }
    }
}

