using System.Net;
using System.Net.Http;
using System.Text.Json;
using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

/// <summary>
/// Tests for the PR 2 reliability surfacing: PredictionFailure classification +
/// ReportFailure event propagation. These are exercised against a test subclass of
/// <see cref="PredictionEngineBase"/> so no network, no real engines involved.
/// </summary>
public class PredictionFailureClassificationTests
{
    // ── HTTP classification ───────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void ClassifyHttpResponse_401And403_AreAuthFailureAndNotRetryable(HttpStatusCode status)
    {
        var engine = new TestPredictionEngine();
        using var response = new HttpResponseMessage(status);

        var failure = engine.InvokeClassifyHttpResponse(response, "unauthorized");

        Assert.Equal(PredictionFailureKind.AuthFailure, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Equal((int)status, failure.HttpStatusCode);
        Assert.Equal("test-engine", failure.ProviderName);
    }

    [Fact]
    public void ClassifyHttpResponse_429_IsRateLimitAndRetryable()
    {
        var engine = new TestPredictionEngine();
        using var response = new HttpResponseMessage((HttpStatusCode)429);

        var failure = engine.InvokeClassifyHttpResponse(response, "too many requests");

        Assert.Equal(PredictionFailureKind.RateLimit, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void ClassifyHttpResponse_BodyMentionsRateLimit_IsRateLimit()
    {
        // Some providers return 400 with a body carrying rate_limit_exceeded
        var engine = new TestPredictionEngine();
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest);

        var failure = engine.InvokeClassifyHttpResponse(
            response, "{\"error\":{\"type\":\"rate_limit_exceeded\"}}");

        Assert.Equal(PredictionFailureKind.RateLimit, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void ClassifyHttpResponse_5xx_IsTransientAndRetryable(HttpStatusCode status)
    {
        var engine = new TestPredictionEngine();
        using var response = new HttpResponseMessage(status);

        var failure = engine.InvokeClassifyHttpResponse(response, "server error");

        Assert.Equal(PredictionFailureKind.Transient, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void ClassifyHttpResponse_TruncatesLongBodyInMessage()
    {
        var engine = new TestPredictionEngine();
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var hugeBody = new string('x', 10_000);

        var failure = engine.InvokeClassifyHttpResponse(response, hugeBody);

        // Body is truncated so trace logs stay bounded — 400 chars + some framing,
        // so comfortably under 1 KB.
        Assert.True(failure.Message.Length < 1000,
            $"Message should be truncated, was {failure.Message.Length} chars");
    }

    // ── Exception classification ──────────────────────────────────────────────

    [Fact]
    public void ClassifyException_HttpRequestException_IsTransient()
    {
        var engine = new TestPredictionEngine();
        var ex = new HttpRequestException("connection reset");

        var failure = engine.InvokeClassifyException(ex);

        Assert.Equal(PredictionFailureKind.Transient, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void ClassifyException_TimeoutException_IsTransient()
    {
        var engine = new TestPredictionEngine();
        var ex = new TimeoutException("timed out");

        var failure = engine.InvokeClassifyException(ex);

        Assert.Equal(PredictionFailureKind.Transient, failure.Kind);
    }

    [Fact]
    public void ClassifyException_JsonException_IsMalformedResponse()
    {
        var engine = new TestPredictionEngine();
        var ex = new JsonException("bad json");

        var failure = engine.InvokeClassifyException(ex);

        Assert.Equal(PredictionFailureKind.MalformedResponse, failure.Kind);
    }

    [Fact]
    public void ClassifyException_UnknownException_IsUnknown()
    {
        var engine = new TestPredictionEngine();
        var ex = new InvalidOperationException("something weird");

        var failure = engine.InvokeClassifyException(ex);

        Assert.Equal(PredictionFailureKind.Unknown, failure.Kind);
    }

    // ── ReportFailure event wiring ────────────────────────────────────────────

    [Fact]
    public void ReportFailure_RaisesFailureOccurredEvent()
    {
        var engine = new TestPredictionEngine();
        PredictionFailure? captured = null;
        engine.FailureOccurred += f => captured = f;

        var failure = new PredictionFailure(
            PredictionFailureKind.AuthFailure, "test", "401", Retryable: false, 401);
        engine.InvokeReportFailure(failure);

        Assert.NotNull(captured);
        Assert.Equal(PredictionFailureKind.AuthFailure, captured!.Kind);
        Assert.Equal("test", captured.ProviderName);
    }

    [Fact]
    public void ReportFailure_SwallowsThrowingHandler()
    {
        // A handler that throws must NOT propagate back up the engine hot path.
        // Without this guarantee, a bad subscriber could break prediction entirely.
        var engine = new TestPredictionEngine();
        engine.FailureOccurred += _ => throw new InvalidOperationException("boom");

        var failure = new PredictionFailure(
            PredictionFailureKind.Transient, "test", "fail", Retryable: true);

        var ex = Record.Exception(() => engine.InvokeReportFailure(failure));
        Assert.Null(ex);
    }

    // ── Test harness ──────────────────────────────────────────────────────────

    private sealed class TestPredictionEngine : PredictionEngineBase
    {
        public TestPredictionEngine() : base("prediction-failure-tests.log", "test-engine") { }

        public PredictionFailure InvokeClassifyHttpResponse(HttpResponseMessage response, string body)
            => ClassifyHttpResponse(response, body);

        public PredictionFailure InvokeClassifyException(Exception ex)
            => ClassifyException(ex);

        public void InvokeReportFailure(PredictionFailure failure)
            => ReportFailure(failure);
    }
}
