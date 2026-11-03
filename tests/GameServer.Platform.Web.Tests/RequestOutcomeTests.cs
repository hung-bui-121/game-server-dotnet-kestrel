using GameServer.Platform.Web.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>Slice 013 T037 (FR-083, FR-084, SD5): finalization rules of the request outcome.</summary>
public sealed class RequestOutcomeTests
{
    [Fact]
    public void RequestOutcome_on_a_bare_context_creates_one_and_returns_it_again()
    {
        var context = new DefaultHttpContext();

        var outcome = context.RequestOutcome();

        outcome.ShouldNotBeNull();
        outcome.Completed.ShouldBeFalse();
        outcome.ResultCode.ShouldBeNull();
        context.RequestOutcome().ShouldBeSameAs(outcome);
        context.Features.Get<IRequestOutcomeFeature>().ShouldBeSameAs(outcome);
    }

    [Theory]
    [InlineData(200, "OK")]
    [InlineData(204, "OK")]
    [InlineData(304, "OK")]
    [InlineData(400, "HTTP_400")]
    [InlineData(418, "HTTP_418")]
    [InlineData(503, "HTTP_503")]
    public void Without_a_code_the_status_decides(int status, string expected)
    {
        var (context, outcome) = Context(status);

        outcome.Complete(context, failure: null);

        outcome.StatusCode.ShouldBe(status);
        outcome.ResultCode.ShouldBe(expected);
        outcome.Aborted.ShouldBeFalse();
        outcome.Completed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(200, "COUNTER_LIMIT_EXCEEDED")]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(418, "TEAPOT")]
    public void A_code_set_by_a_writer_or_consumer_endpoint_is_kept(int status, string code)
    {
        var (context, outcome) = Context(status);
        outcome.ResultCode = code;
        outcome.Operation = "counter.increment";
        outcome.Replayed = true;
        outcome.NotModified = true;

        outcome.Complete(context, failure: null);

        outcome.ResultCode.ShouldBe(code);
        outcome.StatusCode.ShouldBe(status);
        outcome.Operation.ShouldBe("counter.increment");
        outcome.Replayed.ShouldBeTrue();
        outcome.NotModified.ShouldBeTrue();
    }

    [Fact]
    public void A_client_gone_before_the_response_started_is_aborted_without_status_and_overrides_a_set_code()
    {
        var (context, outcome) = Context(200);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        context.RequestAborted = cancelled.Token;
        outcome.ResultCode = "OK";

        outcome.Complete(context, new OperationCanceledException());

        outcome.Aborted.ShouldBeTrue();
        outcome.StatusCode.ShouldBeNull();
        outcome.ResultCode.ShouldBe(RequestResultCodes.Aborted);
        outcome.Completed.ShouldBeTrue();
    }

    [Fact]
    public void A_client_gone_after_the_response_started_keeps_the_sent_status()
    {
        var (context, outcome) = Context(200, started: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        context.RequestAborted = cancelled.Token;
        outcome.ResultCode = "OK";

        outcome.Complete(context, failure: null);

        outcome.Aborted.ShouldBeFalse();
        outcome.StatusCode.ShouldBe(200);
        outcome.ResultCode.ShouldBe("OK");
    }

    [Fact]
    public void An_escaping_exception_is_internal_500_and_overrides_a_set_code()
    {
        var (context, outcome) = Context(200);
        outcome.ResultCode = "OK";

        outcome.Complete(context, new InvalidOperationException("boom"));

        outcome.StatusCode.ShouldBe(500);
        outcome.ResultCode.ShouldBe("INTERNAL");
        outcome.Aborted.ShouldBeFalse();
        outcome.Completed.ShouldBeTrue();
    }

    [Fact]
    public void An_escaping_exception_after_the_response_started_keeps_the_sent_status()
    {
        var (context, outcome) = Context(200, started: true);

        outcome.Complete(context, new InvalidOperationException("boom"));

        outcome.StatusCode.ShouldBe(200);
        outcome.ResultCode.ShouldBe("INTERNAL");
    }

    [Theory]
    [InlineData(418, "HTTP_418")]
    [InlineData(500, "HTTP_500")]
    public void ForStatus_names_the_status(int status, string expected) => RequestResultCodes.ForStatus(status).ShouldBe(expected);

    [Fact]
    public void Category_and_event_id_are_the_documented_ones()
    {
        RequestLog.Category.ShouldBe("GameServer.Requests");
        RequestLog.EventId.Id.ShouldBe(1);
        RequestLog.EventId.Name.ShouldBe("Request");
    }

    private static (DefaultHttpContext Context, RequestOutcome Outcome) Context(int status, bool started = false)
    {
        var context = new DefaultHttpContext();
        if (started)
        {
            context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        }

        context.Response.StatusCode = status;
        var outcome = new RequestOutcome();
        context.Features.Set<IRequestOutcomeFeature>(outcome);
        return (context, outcome);
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
