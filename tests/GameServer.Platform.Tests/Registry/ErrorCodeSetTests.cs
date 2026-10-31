using GameServer.Errors;
using Shouldly;

namespace GameServer.Platform.Tests.Registry;

/// <summary>
/// A null entry in an <see cref="ErrorCodeSet"/> (an ErrorCode field declared below the set that lists it) fails where it is
/// declared, naming the cause, instead of a NullReferenceException in the startup validation (API-11).
/// </summary>
public sealed class ErrorCodeSetTests
{
    private const string Fix =
        "Fix: declare ErrorCode fields above the ErrorCodeSet that lists them (static fields initialize in declaration order).";

    [Fact]
    public void A_null_entry_is_rejected_with_its_position_and_the_fix()
    {
        var listed = new ErrorCode(30001, "ORDER_LISTED", 200, false, "Listed.");

        var ex = Should.Throw<ArgumentException>(() => new ErrorCodeSet(30000, 30099, listed, null!));

        ex.ParamName.ShouldBe("codes");
        ex.Message.ShouldStartWith("ErrorCodeSet 30000-30099: entry 1 (0-based) is null. " + Fix);
    }

    [Fact]
    public void An_error_code_declared_below_its_set_fails_type_initialization_with_the_fix()
    {
        var ex = Should.Throw<TypeInitializationException>(() => MisorderedErrors.All);

        ex.InnerException.ShouldBeOfType<ArgumentException>().Message.ShouldStartWith("ErrorCodeSet 30100-30199: entry 1 (0-based) is null. " + Fix);
    }

    [Fact]
    public void Codes_declared_above_their_set_are_kept_in_order()
    {
        OrderedErrors.All.Codes.ShouldBe([OrderedErrors.First, OrderedErrors.Second]);
    }

    private static class MisorderedErrors
    {
        public static readonly ErrorCode Listed = new(30101, "MISORDERED_LISTED", 200, false, "Listed.");

        // The compiler warns (CS8604) where nullable analysis is on; the guard covers projects without it or with the warning ignored.
        public static ErrorCodeSet All { get; } = new(30100, 30199, Listed, Late!);

        // Declared below the set: still null when All is built.
        public static readonly ErrorCode Late = new(30102, "MISORDERED_LATE", 200, false, "Late.");
    }

    private static class OrderedErrors
    {
        public static readonly ErrorCode First = new(30201, "ORDERED_FIRST", 200, false, "First.");

        public static readonly ErrorCode Second = new(30202, "ORDERED_SECOND", 200, false, "Second.");

        public static ErrorCodeSet All { get; } = new(30200, 30299, First, Second);
    }
}
