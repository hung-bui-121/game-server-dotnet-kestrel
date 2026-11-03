using GameServer.Platform.Web.Aggregates;
using Google.Protobuf;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>FG-028: a typed wire mapper carries its message type, parser and hidden members (research R15).</summary>
public sealed class WireMapperRegistrationTests
{
    private static readonly CounterState State = new() { Value = 7, Increments = 3 };

    private static Wire.CounterState ToWire(CounterState state) => new() { Value = state.Value, Increments = state.Increments };

    [Fact]
    public void Typed_mapper_payload_equals_the_untyped_payload_of_the_same_function()
    {
        var typed = new AggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, ToWire);
        var untyped = new AggregateWireMapper<CounterState>(CounterAggregates.Counter, ToWire);

        typed.ToPayload(State).ShouldBe(untyped.ToPayload(State));
        typed.Map(State).ShouldBe(ToWire(State));
    }

    [Fact]
    public void Contract_exposes_message_parser_and_declared_lists_sorted_and_distinct()
    {
        var options = new WireMapperOptions().Hidden("Value", "Increments", "Value").MayStayDefault("value");
        var mapper = new AggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, ToWire, options);
        var registry = new AggregateWireMapperRegistry([mapper]);

        registry.TryGetContract(CounterAggregates.Counter, out var contract).ShouldBeTrue();
        contract!.AggregateId.ShouldBe(CounterAggregates.Counter);
        contract.StateType.ShouldBe(typeof(CounterState));
        contract.Message.FullName.ShouldBe("sample.counter.v1.CounterState");
        contract.Parser.ParseFrom(mapper.ToPayload(State)).ShouldBe(ToWire(State));
        contract.HiddenMembers.ShouldBe(["Increments", "Value"]);
        contract.MayStayDefaultFields.ShouldBe(["value"]);
    }

    [Fact]
    public void An_untyped_mapper_has_no_contract()
    {
        var registry = new AggregateWireMapperRegistry([new AggregateWireMapper<CounterState>(CounterAggregates.Counter, ToWire)]);

        registry.Has(CounterAggregates.Counter).ShouldBeTrue();
        registry.TryGetContract(CounterAggregates.Counter, out var contract).ShouldBeFalse();
        contract.ShouldBeNull();
        registry.TryGetContract("unknown", out _).ShouldBeFalse();
    }

    [Fact]
    public void Duplicates_list_an_id_registered_twice_and_keep_today_s_winner()
    {
        IAggregateWireMapper first = new AggregateWireMapper<CounterState>(CounterAggregates.Counter, _ => new Wire.CounterState { Value = 1 });
        IAggregateWireMapper last = new AggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, _ => new Wire.CounterState { Value = 2 });
        IAggregateWireMapper other = new AggregateWireMapper<CounterState>("other", ToWire);

        var registry = new AggregateWireMapperRegistry([first, other, last]);

        registry.Duplicates.ShouldHaveSingleItem().ShouldBe(new DuplicateWireMapper(CounterAggregates.Counter, 2));
        Wire.CounterState.Parser.ParseFrom(registry.ToPayload(CounterAggregates.Counter, State)).Value.ShouldBe(2);
    }

    [Fact]
    public void Service_registration_passes_the_configured_options()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddAggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, ToWire, o => o.Hidden("Increments"));

        var mapper = services.ShouldHaveSingleItem().ImplementationInstance.ShouldBeOfType<AggregateWireMapper<CounterState, Wire.CounterState>>();
        mapper.HiddenMembers.ShouldBe(["Increments"]);
        mapper.ToPayload(State).ShouldBe(ToWire(State).ToByteString());
    }
}
