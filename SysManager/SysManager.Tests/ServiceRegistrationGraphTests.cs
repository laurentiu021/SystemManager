// SysManager · ServiceRegistrationGraphTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SysManager.Services;

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

    /// <summary>
    /// Every optional service a registered type's constructor takes is registered too, so the container hands it over
    /// rather than the null the constructor falls back on.
    /// </summary>
    /// <remarks>
    /// Those parameters are optional so that the construction sites written before them kept compiling. A parameter with
    /// a default is one the container fills with that default when its type is not registered, and it does so silently:
    /// the validation above passes either way. Unregistered, the Dashboard would lose its blocked-apps alert and its "Why
    /// is it slow?" button would never run (#1529), and a tab whose change Undo Changes put back would not hear of it.
    /// <para>The constructor read is the one the container calls (<see cref="ContainerConstructor"/>). This used to read
    /// the widest public one, which is not always it: Deep Cleanup's widest takes the cleanup roots, which nothing
    /// registers, so the container builds it through the three-parameter one, and the scan read optional services of a
    /// constructor that never runs (#2742).</para>
    /// <para>A registration the app builds in a factory has no constructor the container chooses, and was not read at
    /// all. Its factory is run over stand-ins (<see cref="StandInProvider"/>) and the object it builds is read instead:
    /// each optional service its type takes must be in the field that holds it, and each service the factory asks for
    /// must be registered, which the validation above cannot see either, since a factory is code it never runs.</para>
    /// </remarks>
    [Fact]
    public void EveryOptionalServiceAConstructorTakes_IsRegistered()
    {
        var scan = ScanOptionalServices(RealGraph());

        // Floors, so a reader that stopped finding them cannot pass by checking nothing: 35 optional services in the
        // constructors the container calls when this was written, 11 factories, and the 2 services two of them hand over.
        Assert.True(scan.Optional.Count >= 30,
            $"only {scan.Optional.Count} optional services were found in the constructors the real graph calls.");
        Assert.Contains(scan.Optional, o => o.Owner == "DashboardViewModel" && o.Parameter.ParameterType == typeof(ISlowdownService));
        Assert.True(scan.Factories >= 11, $"only {scan.Factories} factory registrations were found in the real graph.");
        Assert.Contains($"{nameof(TemperatureService)}({nameof(INvidiaGpuReader)})", scan.FactoryFields);
        Assert.Contains($"{nameof(ResourceHistoryService)}({nameof(IGpuTelemetry)})", scan.FactoryFields);

        Assert.True(scan.Missing.Count == 0,
            "these registrations leave out an optional service, or ask for one nothing registers, so the app gets the "
            + "default or an error instead:\n  " + string.Join("\n  ", scan.Missing));
    }

    /// <summary>
    /// The scan reads the constructor the container calls, and asks the container which that is: a type whose widest
    /// constructor takes something nothing registers is built through the narrower one, and an optional service that one
    /// leaves out is reported, though the widest one's is registered (#2742).
    /// </summary>
    [Fact]
    public void TheOptionalServiceScan_ReadsTheConstructorTheContainerCalls()
    {
        var services = RealGraph();
        services.AddSingleton<ProbeWithAnUnfillableWidestConstructor>();

        using (var provider = services.BuildServiceProvider(StrictOptions))
        {
            Assert.Equal("narrower", provider.GetRequiredService<ProbeWithAnUnfillableWidestConstructor>().Built);
            Assert.Equal([typeof(IPowerShellRunner), typeof(IRecycleBin), typeof(IPreviousInstallationCleanup)],
                ContainerConstructor(typeof(DeepCleanupService), provider.GetRequiredService<IServiceProviderIsService>())!
                    .GetParameters().Select(p => p.ParameterType));
        }

        var scan = ScanOptionalServices(services);
        Assert.Contains($"{nameof(ProbeWithAnUnfillableWidestConstructor)}({nameof(IProbeNeverRegistered)} optional)", scan.Missing);
        Assert.DoesNotContain(scan.Optional, o => o.Owner == nameof(DeepCleanupService));
    }

    /// <summary>
    /// The scan reads what a factory builds: one that leaves an optional service out, and one that asks for a service
    /// nothing registers, are both reported, while the validation above passes both (#2742). One that hands over another
    /// registration's object is not read as if it had built a stand-in.
    /// </summary>
    [Fact]
    public void TheOptionalServiceScan_ReadsWhatAFactoryBuilds()
    {
        var services = RealGraph();
        services.AddSingleton(sp => new ProbeBuiltByAFactory(sp.GetRequiredService<SystemInfoService>()));
        services.AddSingleton(sp => new ProbeAskingForTheUnregistered(sp.GetRequiredService<NeverRegistered>()));
        services.AddSingleton<ProbeForwarded>();
        services.AddSingleton<IProbeForward>(sp => sp.GetRequiredService<ProbeForwarded>());

        Assert.Null(Record.Exception(() => services.BuildServiceProvider(StrictOptions).Dispose()));
        var scan = ScanOptionalServices(services);

        Assert.Contains($"{nameof(ProbeBuiltByAFactory)} is built by a factory that leaves out its {nameof(IPutBackSignal)} signal",
            scan.Missing);
        Assert.Contains($"{nameof(ProbeAskingForTheUnregistered)}'s factory asks for {nameof(NeverRegistered)}, which nothing registers",
            scan.Missing);
        Assert.DoesNotContain(scan.Missing, m => m.Contains(nameof(ProbeForwarded), StringComparison.Ordinal));
    }

    /// <summary>What <see cref="ScanOptionalServices"/> read, and what it found missing.</summary>
    private sealed record OptionalServiceScan(
        List<(string Owner, ParameterInfo Parameter)> Optional, int Factories, List<string> FactoryFields, List<string> Missing);

    /// <summary>
    /// Reads every optional service <paramref name="services"/> hands over: from the constructor the container calls for
    /// each registered type, and from what each factory builds.
    /// </summary>
    private static OptionalServiceScan ScanOptionalServices(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider(StrictOptions);
        var isService = provider.GetRequiredService<IServiceProviderIsService>();

        var optional = services
            .Select(d => d.ImplementationType)
            .OfType<Type>()
            .Distinct()
            .SelectMany(type => (ContainerConstructor(type, isService)?.GetParameters() ?? [])
                .Where(IsOptionalService)
                .Select(p => (Owner: type.Name, Parameter: p)))
            .ToList();
        var missing = optional
            .Where(o => !isService.IsService(o.Parameter.ParameterType))
            .Select(o => $"{o.Owner}({o.Parameter.ParameterType.Name} {o.Parameter.Name})")
            .ToList();

        var factories = 0;
        var fields = new List<string>();
        foreach (var descriptor in services.Where(d => d.ImplementationFactory is not null))
        {
            factories++;
            var standIns = new StandInProvider(isService);
            var built = descriptor.ImplementationFactory!(standIns);
            missing.AddRange(standIns.Unregistered.Select(t =>
                $"{built.GetType().Name}'s factory asks for {t.Name}, which nothing registers"));

            // One that hands over another registration's object is that registration's, and was read above.
            if (standIns.HandedOut(built)) continue;

            var type = built.GetType();
            var parameters = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Where(IsOptionalService)
                .DistinctBy(p => p.ParameterType);
            foreach (var parameter in parameters)
            {
                var holders = InstanceFields(type).Where(f => f.FieldType == parameter.ParameterType).ToList();
                if (holders.Count == 0)
                    missing.Add($"{type.Name} is built by a factory, and no field holds its {parameter.ParameterType.Name}, "
                        + "so this scan cannot tell whether the factory passes one");
                else if (holders.All(f => f.GetValue(built) is null))
                    missing.Add($"{type.Name} is built by a factory that leaves out its {parameter.ParameterType.Name} {parameter.Name}");
                else
                    fields.Add($"{type.Name}({parameter.ParameterType.Name})");
            }
        }

        return new(optional, factories, fields, missing);
    }

    /// <summary>A parameter with a default whose type is a service: an interface, or a type of the app's own.</summary>
    private static bool IsOptionalService(ParameterInfo p) =>
        p.HasDefaultValue && (p.ParameterType.IsInterface || p.ParameterType.Assembly == typeof(ServiceRegistration).Assembly);

    /// <summary>
    /// The public constructor the container calls for <paramref name="type"/>: of those whose every parameter it can fill,
    /// from a registration or from the parameter's default, the one with the most parameters. That is how
    /// Microsoft.Extensions.DependencyInjection chooses, and the container itself says what it can fill.
    /// </summary>
    private static ConstructorInfo? ContainerConstructor(Type type, IServiceProviderIsService isService) =>
        type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue || isService.IsService(p.ParameterType)));

    /// <summary>Every instance field of <paramref name="type"/> and of the types it derives from.</summary>
    private static IEnumerable<FieldInfo> InstanceFields(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
        }
    }

    /// <summary>
    /// What a factory is handed in <see cref="ScanOptionalServices"/>: for each service it asks for, an object of that type
    /// whose constructor never ran, so the factory runs without building anything under it, and a record of what it asked
    /// for that the container could not supply.
    /// </summary>
    private sealed class StandInProvider(IServiceProviderIsService isService) : IServiceProvider
    {
        private readonly List<object> _handedOut = [];

        public List<Type> Unregistered { get; } = [];

        public bool HandedOut(object built) => _handedOut.Any(o => ReferenceEquals(o, built));

        public object GetService(Type serviceType)
        {
            if (!isService.IsService(serviceType)) Unregistered.Add(serviceType);
            var standIn = serviceType.IsInterface || serviceType.IsAbstract
                ? Substitute.For([serviceType], [])
                : RuntimeHelpers.GetUninitializedObject(serviceType);
            // An object whose constructor never ran must not reach its finalizer either.
            GC.SuppressFinalize(standIn);
            _handedOut.Add(standIn);
            return standIn;
        }
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

    // Probes for the optional-service scan. The widest constructor takes something nothing registers, beside an optional
    // service that is registered; the one the container calls takes an optional service nothing registers.
    public interface IProbeNeverRegistered;

    private sealed class ProbeWithAnUnfillableWidestConstructor
    {
        public ProbeWithAnUnfillableWidestConstructor(NeverRegistered missing, IPutBackSignal? signal = null) =>
            (Built, _missing, _signal) = ("widest", missing, signal);

        public ProbeWithAnUnfillableWidestConstructor(IProbeNeverRegistered? optional = null) =>
            (Built, _optional) = ("narrower", optional);

        public string Built { get; }

        private readonly NeverRegistered? _missing;
        private readonly IPutBackSignal? _signal;
        private readonly IProbeNeverRegistered? _optional;
    }

    private sealed class ProbeBuiltByAFactory(SystemInfoService sys, IPutBackSignal? signal = null)
    {
        private readonly SystemInfoService _sys = sys;
        private readonly IPutBackSignal? _signal = signal;
    }

    private sealed class ProbeAskingForTheUnregistered(NeverRegistered missing)
    {
        private readonly NeverRegistered _missing = missing;
    }

    public interface IProbeForward;

    private sealed class ProbeForwarded(IPutBackSignal? signal = null) : IProbeForward
    {
        private readonly IPutBackSignal? _signal = signal;
    }
}
