// SysManager · ServiceRegistrationGraphTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Extensions.DependencyInjection;

namespace SysManager.Tests;

/// <summary>
/// Proves the whole DI graph is resolvable, in CI, on every pull request.
/// <para>The gap these tests close: <c>App.OnStartup</c> builds the container with default options, so a
/// registration that cannot be satisfied — someone adds a constructor parameter and forgets to register
/// its type — is not detected when the container is built. It is detected when that one type is first
/// resolved. Since tab construction became lazy, "first resolved" means "the user clicked that tab", so
/// a wiring mistake ships silently and reaches the user as an error dialog on a tab that never opens.
/// Nothing in either test project built the container before this file existed.</para>
/// <para><see cref="ServiceProviderOptions.ValidateOnBuild"/> walks every registration's constructor
/// call sites and reports all unsatisfiable ones at once, WITHOUT invoking any constructor — so this is
/// safe for services that touch WMI, the registry or the filesystem in their constructors. It ran in
/// 16 ms against the real 131-registration graph.</para>
/// <para>Deliberately a test rather than a production setting. <c>ConfigureServices</c> is static and
/// unconditional, so the graph built here is the graph the app builds — CI catches the break before it
/// merges. Turning the flag on in <c>App.OnStartup</c> instead would convert "one tab is broken" into
/// "the app does not start at all" for the least technical user, which is a strictly worse failure for
/// a bug CI has already caught.</para>
/// </summary>
public class ServiceRegistrationGraphTests
{
    private static ServiceProviderOptions StrictOptions => new() { ValidateOnBuild = true, ValidateScopes = true };

    private static IServiceCollection RealGraph()
    {
        var services = new ServiceCollection();
        services.ConfigureServices();
        return services;
    }

    /// <summary>
    /// Every registration in <see cref="ServiceRegistration.ConfigureServices"/> must be resolvable.
    /// </summary>
    [Fact]
    public void TheWholeGraph_IsResolvable()
    {
        var services = RealGraph();

        // Vacuity floor. An empty or partially-built collection would validate trivially and this test
        // would report success while proving nothing. 131 registrations at the time of writing; the
        // floor is deliberately loose so adding a service does not fail an unrelated test.
        Assert.True(services.Count >= 120,
            $"expected the real graph to hold 120+ registrations; got {services.Count}. Either "
            + "ConfigureServices stopped registering most of the app, or this test is no longer "
            + "building the real graph — in both cases the validation below proves nothing.");

        // BuildServiceProvider throws AggregateException listing EVERY unresolvable descriptor, so a
        // failure names all the broken wiring at once rather than the first one.
        using var provider = services.BuildServiceProvider(StrictOptions);
        Assert.NotNull(provider);
    }

    /// <summary>
    /// The validation above must actually be able to fail — a missing dependency has to be caught.
    /// <para>Without this, <see cref="TheWholeGraph_IsResolvable"/> could pass because validation is
    /// silently disabled (a wrong options object, a future default change) rather than because the graph
    /// is sound, and it would keep passing while broken wiring merged.</para>
    /// </summary>
    [Fact]
    public void TheValidation_CatchesAnUnregisteredDependency()
    {
        var services = RealGraph();
        services.AddSingleton<ProbeWithMissingDependency>();

        var ex = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(StrictOptions));
        Assert.Contains(nameof(ProbeWithMissingDependency), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="ServiceProviderOptions.ValidateScopes"/> must be on: a singleton may not capture a
    /// scoped service. The app registers no scoped services today, so this asserts the guard is armed
    /// for the first one that is added rather than describing a current defect.
    /// </summary>
    [Fact]
    public void TheValidation_CatchesASingletonCapturingAScopedService()
    {
        var services = RealGraph();
        services.AddScoped<ScopedProbe>();
        services.AddSingleton<SingletonCapturingScopedProbe>();

        var ex = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(StrictOptions));
        Assert.Contains(nameof(SingletonCapturingScopedProbe), ex.Message, StringComparison.Ordinal);
    }

    // Probes for the two negative tests. Intentionally never registered as a dependency of anything in
    // the real graph — each exists only to give validation something it must reject.
    private sealed class NeverRegistered;

    private sealed class ProbeWithMissingDependency(NeverRegistered missing)
    {
        private readonly NeverRegistered _missing = missing;
    }

    private sealed class ScopedProbe;

    private sealed class SingletonCapturingScopedProbe(ScopedProbe scoped)
    {
        private readonly ScopedProbe _scoped = scoped;
    }
}
