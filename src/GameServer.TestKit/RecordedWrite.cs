namespace GameServer.TestKit;

/// <summary>An aggregate set by the handler: id and its latest state (a copy taken through the aggregate's state type).</summary>
public sealed record RecordedWrite(string AggregateId, object State);
