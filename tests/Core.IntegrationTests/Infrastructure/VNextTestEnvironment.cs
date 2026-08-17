using VNext.Testing.Sdk.Infrastructure;

namespace Core.IntegrationTests.Infrastructure;

/// <summary>
/// Domain-specific test environment for Core.
/// Override any virtual property or method from <see cref="VNext.Testing.Sdk.Infrastructure.VNextTestEnvironment"/>
/// to customise the Docker stack for this domain.
/// </summary>
public class VNextTestEnvironment : VNext.Testing.Sdk.Infrastructure.VNextTestEnvironment
{
    // -------------------------------------------------------------------------
    // Required — set these to match your domain
    // -------------------------------------------------------------------------

    /// <summary>APP_DOMAIN value passed to vNext containers.</summary>
    protected override string Domain => "core";

    /// <summary>
    /// Publish this domain's components from local files with the SDK's own publisher.
    /// <para>
    /// <b>Caveat for the containerised path.</b> The SDK publisher uploads component JSON verbatim,
    /// and every HTTP task in this domain points at <c>http://localhost:3001</c> — MockLab's
    /// published port on a developer machine. That resolves correctly when the runtime also runs on
    /// the host (<c>VNEXT_BASE_URL</c>), but inside the container stack <c>localhost</c> is the
    /// runtime container itself, so those tasks fail with a connection error. Flows built only from
    /// script tasks (chain-busy) are unaffected. Where a mapping overrides the URL it reads
    /// <c>Example:ApiBaseUrl</c> from configuration instead of hard-coding the host.
    /// </para>
    /// </summary>
    protected override bool EnableDomainPublish => true;

    /// <summary>
    /// MockLab request collections for this domain. The SDK defaults to a directory inside the
    /// test project; the real collections live with the docker stack and are shared with local
    /// development, so point at those rather than keeping a second copy in sync.
    /// </summary>
    protected override string MocklabSeedDirectory =>
        Path.Combine(RepoRoot, "etc", "docker", "config", "seed");

    // NOTE — there is deliberately no OnAfterEnvironmentReadyAsync override and no
    // VNEXT_IT_SKIP_PUBLISH handling. Publishing is the SDK's job via EnableDomainPublish above, and
    // it happens inside InitializeAsync BEFORE this hook would run (in the external-stack path the
    // hook is never called at all). An override that logged "skipping publish" therefore suppressed
    // nothing while reading as though it did — remove nothing here without checking the SDK's
    // InitializeAsync first.

    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "vnext.config.json"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new InvalidOperationException("vnext.config.json not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>PostgreSQL database name created during test setup.</summary>
    protected override string DatabaseName => "vNext_Core_Test";

    /// <summary>
    /// Base image for vNext orchestrator/execution (without the role suffix).
    /// Override if your team publishes to a different registry.
    /// </summary>
    protected override string VNextImage => "ghcr.io/burgan-tech/vnext";

    /// <summary>
    /// Image tag for vNext containers. Defaults to "latest".
    /// Override to pin a specific release, e.g. "1.4.2" or "sha-abc1234".
    /// </summary>
    // protected override string VNextImageVersion => "latest";

    /// <summary>
    /// Full image for the db-migrator worker. Defaults to {VNextImage}/db-migrator:{VNextImageVersion}.
    /// Override if your migrator image lives at a different path or tag.
    /// </summary>
    // protected override string DbMigratorImage => $"{VNextImage}/db-migrator:{VNextImageVersion}";

    /// <summary>
    /// Mocklab container image. Defaults to "ghcr.io/burgan-tech/mocklab:latest".
    /// Override to pin a specific version.
    /// </summary>
    // protected override string MocklabImage => "ghcr.io/burgan-tech/mocklab:latest";

    /// <summary>
    /// Host-side directory bind-mounted to /app/seed in the Mocklab container.
    /// Defaults to Infrastructure/MocklabSeed/ relative to the test output directory.
    /// Override to point to a different seed data location.
    /// </summary>
    // protected override string MocklabSeedDirectory =>
    //     Path.Combine(Directory.GetCurrentDirectory(), "Infrastructure", "MocklabSeed");

    // -------------------------------------------------------------------------
    // Optional — uncomment and override as needed
    // -------------------------------------------------------------------------

    // /// <summary>Disable Mocklab if your domain doesn't need mock HTTP services.</summary>
    // protected override bool EnableMocklab => false;

    // /// <summary>Disable domain publish if definitions are managed externally.</summary>
    // protected override bool EnableDomainPublish => false;

    // /// <summary>
    // /// Called after the full stack is ready. Start additional services here
    // /// (e.g. a custom microservice, a second mock server, a message broker).
    // /// Containers started here must be disposed in a DisposeAsync override.
    // /// </summary>
    // protected override async Task OnAfterEnvironmentReadyAsync()
    // {
    //     // _myService = new ContainerBuilder()
    //     //     .WithImage("my-registry/my-service:latest")
    //     //     .WithNetwork(_network)
    //     //     .WithNetworkAliases("test-my-service")
    //     //     .Build();
    //     // await _myService.StartAsync();
    // }

    // /// <summary>Adds domain-specific Vault secrets on top of the SDK defaults.</summary>
    // protected override Dictionary<string, Dictionary<string, string>> GetVaultSecrets()
    // {
    //     var secrets = base.GetVaultSecrets();
    //     secrets["core-secret"] = new() { ["ApiKey"] = "my-test-api-key" };
    //     return secrets;
    // }

    // /// <summary>Extends the orchestrator environment variables.</summary>
    // protected override Dictionary<string, string> GetOrchestratorEnvironment()
    // {
    //     var env = base.GetOrchestratorEnvironment();
    //     env["MY_CUSTOM_VAR"] = "value";
    //     return env;
    // }
}
