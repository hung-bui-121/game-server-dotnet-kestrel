using System.Net;
using Couchbase;
using Couchbase.Core.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// T055 M1 without a cluster: the provisioner's cluster-manager calls (scope listing included) retry a 503 with capped exponential
/// backoff and nothing else.
/// </summary>
public sealed class CouchbaseManagementRetryTests
{
    private static readonly TimeSpan FastBackoff = TimeSpan.FromMilliseconds(1);

    private static CouchbaseException Management(HttpStatusCode status) => new(new ManagementErrorContext { HttpStatus = status });

    [Fact]
    public async Task A_503_is_retried_until_the_call_succeeds()
    {
        var calls = 0;

        var result = await CouchbaseManagementRetry.RunAsync(
            () => ++calls < 3 ? throw Management(HttpStatusCode.ServiceUnavailable) : Task.FromResult("scopes"),
            NullLogger.Instance,
            CancellationToken.None,
            FastBackoff);

        result.ShouldBe("scopes");
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_lasting_503_fails_after_the_last_attempt_with_the_original_exception()
    {
        var calls = 0;

        var ex = await Should.ThrowAsync<CouchbaseException>(() => CouchbaseManagementRetry.RunAsync<string>(
            () =>
            {
                calls++;
                throw Management(HttpStatusCode.ServiceUnavailable);
            },
            NullLogger.Instance,
            CancellationToken.None,
            FastBackoff));

        calls.ShouldBe(CouchbaseManagementRetry.Attempts);
        CouchbaseManagementRetry.IsServiceUnavailable(ex).ShouldBeTrue();
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        var forbidden = 0;
        var other = 0;

        await Should.ThrowAsync<CouchbaseException>(() => CouchbaseManagementRetry.RunAsync<string>(
            () =>
            {
                forbidden++;
                throw Management(HttpStatusCode.Forbidden);
            },
            NullLogger.Instance,
            CancellationToken.None,
            FastBackoff));
        await Should.ThrowAsync<InvalidOperationException>(() => CouchbaseManagementRetry.RunAsync<string>(
            () =>
            {
                other++;
                throw new InvalidOperationException("not a management error");
            },
            NullLogger.Instance,
            CancellationToken.None,
            FastBackoff));

        forbidden.ShouldBe(1);
        other.ShouldBe(1);
    }

    [Fact]
    public void The_backoff_doubles_from_500_ms_and_is_capped_at_5_s()
    {
        var initial = CouchbaseManagementRetry.InitialBackoff;

        Enumerable.Range(1, CouchbaseManagementRetry.Attempts - 1)
            .Select(attempt => CouchbaseManagementRetry.Backoff(attempt, initial).TotalMilliseconds)
            .ShouldBe([500, 1000, 2000, 4000, 5000, 5000, 5000]);
    }
}
