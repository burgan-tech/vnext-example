using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// CacheAside key script, C# (Roslyn) form, embedded with <c>"encoding": "NAT"</c> in
/// <c>til-cacheaside-key-cs-nat</c>. Per-instance key, so a hit can only come from this instance.
/// </summary>
public class TilCacheKeyCsNat : ICacheKeyMapping
{
    public Task<string?> Handler(ScriptContext context)
        => Task.FromResult<string?>($"til:ca:{context.Instance?.Key}:cs-nat");
}
