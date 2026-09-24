// SysManager · NetworkUseGuard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

[assembly: AssemblyFixture(typeof(SysManager.Tests.NetworkUseGuard))]

namespace SysManager.Tests;

/// <summary>
/// Fails the unit run if any test in it reaches the network.
/// </summary>
/// <remarks>
/// SysManager.Tests is the blocking suite and takes no system dependencies; a test that needs a real network
/// belongs in SysManager.IntegrationTests. That rule was prose until #2412, when 22 About tests turned out to
/// send the startup update check to api.github.com on every run. Nothing could see it, because a call that
/// succeeds and a call that fails both leave the test green — the convenience constructor swallows the failure.
/// <para>It listens in-process to the runtime's own networking EventSources. A DNS resolution or a socket
/// connect is the evidence, because only real I/O raises them. An HTTP request start is NOT evidence: it is
/// raised at the HttpClient layer even when the handler is a stub, and <c>AppIconServiceTests</c> drives two
/// such requests on purpose. Requests are therefore reported only as context for whichever test connected.</para>
/// <para>Attribution comes from the test context that flows with the async call, so a fire-and-forget request
/// started by a constructor is charged to the test that built the object even when it lands after that test
/// has returned. That is also why the verdict waits for the end of the assembly: it is the only point at which
/// late requests are guaranteed to have been recorded. A connection reused from the pool raises neither event,
/// so only the first test to reach a given host is named; fixing it exposes the next.</para>
/// <para>Only network use inside a test's context counts. The host process's own plumbing — result reporting,
/// coverage, whatever the runner needs — runs outside any test and is not what this rule is about; it is listed
/// as context when the guard fails, never as the reason. A thread that suppresses the flow of the execution
/// context escapes attribution too, which is the price of never failing the run over infrastructure.</para>
/// </remarks>
public sealed class NetworkUseGuard : IDisposable
{
    private readonly Listener _listener = new();

    public void Dispose()
    {
        // Stop listening first, so nothing is still being recorded while the verdict is written.
        _listener.Dispose();

        var reached = _listener.Reached.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (reached.Count == 0) return;

        var requests = _listener.Requests.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var outside = _listener.Outside.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        throw new InvalidOperationException(
            $"{reached.Count} network use(s) in the blocking unit suite. Unit tests take no system dependencies: "
            + "put the call behind a substitute (IUpdateService is the model), or move the test to "
            + "SysManager.IntegrationTests (#2412).\n  " + string.Join("\n  ", reached)
            + Context("HTTP requests started (a stubbed handler raises these too)", requests)
            + Context("Network use outside any test (host plumbing; not counted)", outside));
    }

    private static string Context(string heading, List<string> lines) =>
        lines.Count == 0 ? string.Empty : $"\n{heading}, for context:\n  " + string.Join("\n  ", lines);

    /// <summary>The listener itself. Composed rather than inherited so the fixture's disposal is its own.</summary>
    private sealed class Listener : EventListener
    {
        // Field initialisers run before the base constructor, which is where OnEventSourceCreated is first
        // called for every source that already exists — so these are ready by then.
        public ConcurrentQueue<string> Reached { get; } = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> Outside { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name is "System.Net.NameResolution" or "System.Net.Sockets" or "System.Net.Http")
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var what = eventData.EventName switch
            {
                "ResolutionStart" => $"resolved {Payload(eventData, "hostNameOrAddress")}",
                "ConnectStart" => "opened a socket connection",
                "RequestStart" when eventData.EventSource.Name == "System.Net.Http" =>
                    $"{Payload(eventData, "scheme")}://{Payload(eventData, "host")}:{Payload(eventData, "port")}"
                    + Payload(eventData, "pathAndQuery"),
                _ => null,
            };
            if (what is null) return;

            var test = CurrentTest();
            if (eventData.EventName == "RequestStart")
                Requests.Enqueue($"{test ?? "(outside any test)"}: {what}");
            else if (test is null)
                Outside.Enqueue(what);
            else
                Reached.Enqueue($"{test}: {what}");
        }

        /// <summary>
        /// Class and method of the running test, or null outside one. Class and method only: a theory's display
        /// name carries its argument values, and a value built at run time can hold a local path, which has no
        /// place in a message that can end up in a CI log.
        /// </summary>
        private static string? CurrentTest()
        {
            var context = TestContext.Current;
            return context.TestMethod is { } method
                ? $"{context.TestClass?.TestClassName}.{method.MethodName}"
                : null;
        }

        private static string Payload(EventWrittenEventArgs eventData, string name)
        {
            var index = eventData.PayloadNames?.IndexOf(name) ?? -1;
            return index >= 0 && eventData.Payload is { } payload && index < payload.Count
                ? payload[index]?.ToString() ?? string.Empty
                : string.Empty;
        }
    }
}
