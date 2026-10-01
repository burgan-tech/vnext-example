using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// CacheAside key script, C# (Roslyn) form, served from <c>sys-mappings</c> and referenced with
/// <c>"encoding": "REF"</c> by <c>til-cacheaside-key-cs-ref</c>. Per-instance key.
/// </summary>
public class TilCacheKeyCsRef : ICacheKeyMapping
{
    public Task<string?> Handler(ScriptContext context)
        => Task.FromResult<string?>($"til:ca:{context.Instance?.Key}:cs-ref");
}
