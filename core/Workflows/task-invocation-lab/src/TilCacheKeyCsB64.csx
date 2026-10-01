using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// CacheAside key script, C# (Roslyn) form, embedded with the default B64 encoding in
/// <c>til-cacheaside-key-cs-b64</c>. Per-instance key, so a hit can only come from this instance.
/// </summary>
public class TilCacheKeyCsB64 : ICacheKeyMapping
{
    public Task<string?> Handler(ScriptContext context)
        => Task.FromResult<string?>($"til:ca:{context.Instance?.Key}:cs-b64");
}
