using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using AzureLab.Assistant;
using AzureLab.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Assistant;

public static class FailureLab
{
    public static IEndpointRouteBuilder MapFailureLab(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/assistant/failure-lab", async (
            string owner, IServiceScopeFactory scopes, IOptions<AiOptions> ai, ToolLimits limits, CancellationToken ct) =>
        {
            var checks = new List<object>();
            var failed = 0;
            void Check(string scenario, string name, bool pass, string detail = "")
            {
                if (!pass) failed++;
                checks.Add(new { scenario, name, pass, detail });
            }

            // ---- in-process taps: spans and metrics emitted by the assistant
            var spans = new List<Activity>();
            using var activityListener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == AssistantTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { lock (spans) spans.Add(a); }
            };
            ActivitySource.AddActivityListener(activityListener);

            var measurements = new List<(string Name, double Value, string Tags)>();
            void Add(Instrument inst, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                var t = string.Join(",", tags.ToArray().Select(x => $"{x.Key}={x.Value}"));
                lock (measurements) measurements.Add((inst.Name, value, t));
            }
            using var meterListener = new MeterListener
            {
                InstrumentPublished = (inst, l) =>
                {
                    if (inst.Meter.Name == AssistantTelemetry.Name && inst.Name.StartsWith("assistant."))
                        l.EnableMeasurementEvents(inst);
                }
            };
            meterListener.SetMeasurementEventCallback<long>((inst, v, tags, _) => Add(inst, v, tags));
            meterListener.SetMeasurementEventCallback<int>((inst, v, tags, _) => Add(inst, v, tags));
            meterListener.SetMeasurementEventCallback<double>((inst, v, tags, _) => Add(inst, v, tags));
            meterListener.Start();

            // ---- setup
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var realReader = sp.GetRequiredService<IRedeemStatusReader>();
            var evA = Guid.NewGuid();
            var maxQ = ai.Value.Query.MaxQuestionChars;

            CallerContext Caller(string correlation = "failure-lab") =>
                new(owner, evA, new HashSet<Guid> { evA }, correlation);

            AssistantService Svc(IChatClient model, IRedeemStatusReader reader, IEventDocumentCatalog docs,
                                 IEventPassageSearch search, ToolLimits? lim = null) =>
                new(model, reader, docs, search, sp.GetRequiredService<IRedeemProposals>(), lim ?? limits, lf);

            async Task<(AssistantResult? R, string? Unavailable)> Run(AssistantService svc, string message, string correlation = "failure-lab")
            {
                try { return (await svc.AskAsync(Caller(correlation), message, ct), null); }
                catch (AssistantUnavailableException ex) { return (null, ex.Message); }
            }

            async Task<(IntentResult? R, string? Unavailable)> Extract(ScriptedChatClient model, string message)
            {
                try { return (await new IntentExtractor(model, lf).ExtractAsync(message, maxQ, "failure-lab", ct), null); }
                catch (AssistantUnavailableException ex) { return (null, ex.Message); }
            }

            string Json(string kind, string rid = "", string oid = "", string q = "") =>
                $"{{\"intent\":\"{kind}\",\"redeemId\":\"{rid}\",\"offerId\":\"{oid}\",\"question\":\"{q}\"}}";

            // ===== F1: model dies in the MIDDLE of the loop =====
            {
                var search = new SecurityLab.LabSearch("Registration opens at 9 AM.");
                var model = new ScriptedChatClient([
                    SecurityLab.Call("SearchEventDocuments", ("query", "registration")),
                    new ScriptedTurn(Throw: new HttpRequestException("connection refused"))]);
                var (r, un) = await Run(Svc(model, realReader, new SecurityLab.LabCatalog(), search), "When does registration open?");
                Check("F1 model down mid-loop", "503 path after one tool run: 'model_unavailable', no retry inside the request",
                    un == "model_unavailable" && search.Calls == 1 && model.Turns == 2,
                    $"{un}; toolRuns={search.Calls}; modelTurns={model.Turns}");
            }

            // ===== F2: 429-like overload from the model =====
            {
                var model = new ScriptedChatClient([new ScriptedTurn(
                    Throw: new HttpRequestException("too many requests", null, HttpStatusCode.TooManyRequests))]);
                var (_, un) = await Run(Svc(model, realReader, new SecurityLab.LabCatalog(), new SecurityLab.LabSearch()), "hello");
                Check("F2 model overloaded", "429 maps to 'model_rate_limited' (503 path) with exactly one model call (no hammering)",
                    un == "model_rate_limited" && model.Turns == 1, $"{un}; modelTurns={model.Turns}");
            }

            // ===== F3: SQL Server down inside a tool =====
            {
                var model = new ScriptedChatClient([
                    SecurityLab.Call("GetRedeemStatus", ("redeemId", Guid.NewGuid().ToString())), SecurityLab.Say("done")]);
                var (_, un) = await Run(Svc(model, new ThrowingReader(new FakeDbException("connection failed")),
                    new SecurityLab.LabCatalog(), new SecurityLab.LabSearch()), "status please");
                Check("F3 SQL down", "DbException in a tool -> 503 path, and NO second model turn is wasted",
                    un == "FakeDbException" && model.Turns == 1, $"{un}; modelTurns={model.Turns}");
            }

            // ===== F4: unexpected bug inside a tool =====
            {
                var model = new ScriptedChatClient([
                    SecurityLab.Call("GetRedeemStatus", ("redeemId", Guid.NewGuid().ToString())), SecurityLab.Say("done")]);
                var (r, _) = await Run(Svc(model, new ThrowingReader(new InvalidOperationException("bug")),
                    new SecurityLab.LabCatalog(), new SecurityLab.LabSearch()), "status please");
                Check("F4 tool bug", "unexpected exception -> 'InternalError' (the endpoint maps this to 500), loop stops, nothing leaked",
                    r is not null && r.Outcome == "InternalError" && r.ToolCalls.Single().Outcome == "internal_error"
                    && model.Turns == 1 && r.Answer == AssistantService.Fallback,
                    r is null ? "unavailable" : $"{r.Outcome}; modelTurns={model.Turns}");
            }

            // ===== F5: tool hangs =====
            {
                var model = new ScriptedChatClient([SecurityLab.Call("SearchEventDocuments", ("query", "slow")), SecurityLab.Say("done")]);
                var (_, un) = await Run(Svc(model, realReader, new SecurityLab.LabCatalog(),
                    new SecurityLab.LabSearch("x", TimeSpan.FromSeconds(30)), limits with { PerToolTimeoutSeconds = 1 }), "slow");
                Check("F5 tool timeout", "per-tool timeout -> 503 path and NO second model turn",
                    un == "tool_timeout" && model.Turns == 1, $"{un}; modelTurns={model.Turns}");
            }

            // ===== F6: model returns blank text =====
            {
                var model = new ScriptedChatClient([SecurityLab.Say("   ")]);
                var (r, _) = await Run(Svc(model, realReader, new SecurityLab.LabCatalog(), new SecurityLab.LabSearch()), "hello");
                Check("F6 blank model output", "whitespace-only output fails closed with the fixed fallback",
                    r is not null && r.Outcome == "EmptyAnswer" && r.Answer == AssistantService.Fallback, r?.Outcome ?? "unavailable");
            }

            // ===== F7: malformed output for the structured-output extractor =====
            {
                var rid = Guid.NewGuid();
                var msg = $"status of redeem {rid}";
                var good = Json("CheckRedeemStatus", rid: rid.ToString());

                var m1 = new ScriptedChatClient([SecurityLab.Say("not json"), SecurityLab.Say("still not json")]);
                var (r1, _) = await Extract(m1, msg);
                Check("F7 malformed JSON", "garbage twice -> Rejected after exactly 2 attempts (fail closed, bounded)",
                    r1 is { Outcome: IntentOutcome.Rejected, Attempts: 2 } && m1.Turns == 2, $"{r1?.Outcome}; turns={m1.Turns}");

                var m2 = new ScriptedChatClient([SecurityLab.Say("{\"intent\":"), SecurityLab.Say(good)]);
                var (r2, _) = await Extract(m2, msg);
                Check("F7 malformed JSON", "broken JSON then valid JSON -> repaired on attempt 2",
                    r2 is { Outcome: IntentOutcome.Ok, Attempts: 2 }, $"{r2?.Outcome}; attempts={r2?.Attempts}");

                var m3 = new ScriptedChatClient([SecurityLab.Say(Json("DeleteEverything")), SecurityLab.Say(good)]);
                var (r3, _) = await Extract(m3, msg);
                Check("F7 malformed JSON", "an intent value outside the enum is rejected, then repaired",
                    r3 is { Outcome: IntentOutcome.Ok, Attempts: 2 }, $"{r3?.Outcome}; attempts={r3?.Attempts}");

                var m4 = new ScriptedChatClient([new ScriptedTurn(Text: "{\"intent\":\"Check", Truncated: true), SecurityLab.Say(good)]);
                var (r4, _) = await Extract(m4, msg);
                Check("F7 malformed JSON", "truncated output (finish=Length) is rejected, then repaired",
                    r4 is { Outcome: IntentOutcome.Ok, Attempts: 2 }, $"{r4?.Outcome}; attempts={r4?.Attempts}");

                var m5 = new ScriptedChatClient([
                    SecurityLab.Say(Json("CheckRedeemStatus", rid: Guid.NewGuid().ToString())),   // fabricated id
                    SecurityLab.Say(Json("Unknown"))]);
                var (r5, _) = await Extract(m5, msg);
                Check("F7 malformed JSON", "a fabricated id is rejected, and the repaired answer 'Unknown' is accepted",
                    r5 is { Outcome: IntentOutcome.Unknown, Attempts: 2 }, $"{r5?.Outcome}; attempts={r5?.Attempts}");

                var m6 = new ScriptedChatClient([new ScriptedTurn(Throw: new HttpRequestException("refused"))]);
                var (_, un6) = await Extract(m6, msg);
                Check("F7 malformed JSON", "model down during extraction -> 'model_unavailable' (503 path)", un6 == "model_unavailable", un6 ?? "");

                var m7 = new ScriptedChatClient([new ScriptedTurn(
                    Throw: new HttpRequestException("429", null, HttpStatusCode.TooManyRequests))]);
                var (_, un7) = await Extract(m7, msg);
                Check("F7 malformed JSON", "429 during extraction -> 'model_rate_limited'", un7 == "model_rate_limited", un7 ?? "");
            }

            // ===== F8: observability: normal request with a distinctive marker in the user text =====
            const string marker = "PII-MARKER-7f3a";
            var secretRedeem = Guid.NewGuid().ToString();
            {
                var model = new ScriptedChatClient([
                    SecurityLab.Call("SearchEventDocuments", ("query", "registration")), SecurityLab.Say("done")]);
                var (r, un) = await Run(Svc(model, realReader, new SecurityLab.LabCatalog(),
                    new SecurityLab.LabSearch("Registration opens at 9 AM.")),
                    $"{marker} status of redeem {secretRedeem}", "obs-run");
                Check("F8 observability", "the normal request completes", r?.Outcome == "Answered", r?.Outcome ?? un ?? "");
            }

            List<Activity> snapshot;
            lock (spans) snapshot = spans.ToList();
            List<(string Name, double Value, string Tags)> m;
            lock (measurements) m = measurements.ToList();
            bool Has(string name, string tags) => m.Any(x => x.Name == name && x.Tags.Contains(tags));

            var req = snapshot.FirstOrDefault(a => a.OperationName == "assistant.request" && a.GetTagItem("correlation.id")?.ToString() == "obs-run");
            var toolSpan = snapshot.FirstOrDefault(a => a.OperationName == "assistant.tool_call" && req is not null && a.TraceId == req.TraceId && a.ParentSpanId == req.SpanId);
            Check("F8 observability", "a tool_call span exists as a CHILD of the request span, with tool and outcome tags",
                req is not null && toolSpan is not null &&
                toolSpan.GetTagItem("tool.name")?.ToString() == "SearchEventDocuments" &&
                toolSpan.GetTagItem("tool.outcome")?.ToString() == "ok",
                $"requestSpan={(req is null ? "missing" : "ok")}, toolSpan={(toolSpan is null ? "missing" : "ok")}");
            Check("F8 observability", "request span carries outcome, tool_calls and model_turns",
                req is not null && req.GetTagItem("assistant.outcome")?.ToString() == "Answered"
                && req.GetTagItem("assistant.tool_calls") is not null && req.GetTagItem("assistant.model_turns") is not null);
            Check("F8 observability", "failed requests are marked as errors on their span",
                snapshot.Any(a => a.OperationName == "assistant.request" && a.Status == ActivityStatusCode.Error));
            Check("F8 observability", "tool-call counter has tool+outcome tags",
                Has("assistant.tool.calls", "tool=SearchEventDocuments,outcome=ok") && Has("assistant.tool.calls", "outcome=internal_error"));
            Check("F8 observability", "unavailable counter splits the reasons (model, rate limit, SQL, tool timeout)",
                Has("assistant.unavailable", "reason=model_unavailable") && Has("assistant.unavailable", "reason=model_rate_limited")
                && Has("assistant.unavailable", "reason=FakeDbException") && Has("assistant.unavailable", "reason=tool_timeout"));
            Check("F8 observability", "per-request histograms (model turns, tool calls, duration) and the request outcome counter are recorded",
                Has("assistant.request.model_turns", "") && Has("assistant.request.tool_calls", "")
                && Has("assistant.request.duration", "") && Has("assistant.requests", "outcome=EmptyAnswer")
                && Has("assistant.requests", "outcome=InternalError") && Has("assistant.requests", "outcome=Unavailable"));

            var leaked = snapshot.SelectMany(a => a.TagObjects).Any(t =>
                    (t.Value?.ToString() ?? "").Contains(marker) || (t.Value?.ToString() ?? "").Contains(secretRedeem))
                || m.Any(x => x.Tags.Contains(marker) || x.Tags.Contains(secretRedeem) || x.Tags.Contains(owner)
                              || x.Tags.Contains(evA.ToString()));
            Check("F8 observability", "NO user text, redeem id, user id or event id appears in any span tag or metric tag", !leaked);

            return Results.Ok(new
            {
                allPassed = failed == 0,
                failed,
                checks,
                spans = snapshot.GroupBy(a => a.OperationName).Select(g => new { name = g.Key, count = g.Count() }),
                spanDump = snapshot.Select(a => new
                {
                    name = a.OperationName,
                    span = a.SpanId.ToString(),
                    parent = a.ParentSpanId.ToString(),
                    trace = a.TraceId.ToString()[^6..],
                    corr = a.GetTagItem("correlation.id")?.ToString(),
                    tool = a.GetTagItem("tool.name")?.ToString(),
                    outcome = (a.GetTagItem("tool.outcome") ?? a.GetTagItem("assistant.outcome"))?.ToString()
                }),
                metrics = m.GroupBy(x => (x.Name, x.Tags))
                           .Select(g => new { name = g.Key.Name, tags = g.Key.Tags, n = g.Count(), sum = Math.Round(g.Sum(x => x.Value), 1) })
                           .OrderBy(x => x.name).ThenBy(x => x.tags)
            });
        });

        return app;
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class ThrowingReader(Exception ex) : IRedeemStatusReader
    {
        public Task<RedeemStatusView?> FindForUserAsync(Guid redeemId, string userId, CancellationToken ct) => throw ex;
    }
}
