using GameServer.Errors;
using Shouldly;

namespace GameServer.Platform.Tests.Registry;

/// <summary>
/// Slice 016 T010 (FR-020, FR-030, BC-4, errors-and-report-lines §1): the three core codes of the slice, asserted literally, are
/// listed in <see cref="CoreErrorCodes.All"/>, and every core number is unique inside the core range 0-999.
/// </summary>
public sealed class CoreErrorCodeCatalogTests
{
    public static TheoryData<ErrorCode, int, string, int, bool, string> SliceCodes() => new()
    {
        { CoreErrorCodes.CommandOutputTooLarge, 23, "COMMAND_OUTPUT_TOO_LARGE", 200, false, "The command result is too large." },
        { CoreErrorCodes.AggregateVersionAhead, 24, "AGGREGATE_VERSION_AHEAD", 503, true, "Player data was written by a newer server version. Retry later." },
        { CoreErrorCodes.AggregateUnreadable, 25, "AGGREGATE_UNREADABLE", 500, false, "Player data could not be read." },
    };

    [Theory]
    [MemberData(nameof(SliceCodes))]
    public void The_slice_codes_have_their_documented_number_name_status_retry_flag_and_message(
        ErrorCode code, int number, string name, int httpStatus, bool retryable, string message)
    {
        code.Number.ShouldBe(number);
        code.Name.ShouldBe(name);
        code.HttpStatus.ShouldBe(httpStatus);
        code.Retryable.ShouldBe(retryable);
        code.SafeMessage.ShouldBe(message);
        CoreErrorCodes.All.Codes.ShouldContain(code);
    }

    [Fact]
    public void Core_numbers_and_names_are_unique_and_inside_the_core_range()
    {
        var codes = CoreErrorCodes.All.Codes;

        codes.Select(c => c.Number).ShouldBeUnique();
        codes.Select(c => c.Name).ShouldBeUnique();
        codes.ShouldAllBe(c => c.Number >= CoreErrorCodes.RangeStart && c.Number <= CoreErrorCodes.RangeEnd);
        (CoreErrorCodes.All.RangeStart, CoreErrorCodes.All.RangeEnd).ShouldBe((0, 999));
    }
}
