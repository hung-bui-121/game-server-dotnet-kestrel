extern alias sdk;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T056 (FR-045, contracts/sdk-pending-store.md §2): the entry container v1 round-trips, and every refused shape gives its
/// reason without an exception.
/// </summary>
public sealed class PendingCommandFormatTests
{
    private const string Id = "3f2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d";

    private static readonly DateTimeOffset Recorded = new(2030, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

    private static PendingCommand Entry(byte[]? body = null, string playerId = "p_1", string route = "api/v1/shop/buy") =>
        new(Id, playerId, route, body ?? [1, 2, 3, 0, 255], Recorded);

    /// <summary>
    /// T082 M3: Record runs on the main thread, so serializing an entry copies its body once into the exact-size payload and once
    /// into the payload array — never through the cloning <see cref="PendingCommand.Body"/> (three more copies) or a growing stream.
    /// </summary>
    [Fact]
    public void Serializing_copies_the_body_only_into_the_payload()
    {
        var entry = Entry(new byte[PendingCommandFormat.MaxBodyBytes]);
        PendingCommandFormat.Serialize(entry, out _).ShouldNotBeNull();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var parts = PendingCommandFormat.Serialize(entry, out var problem);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        parts.ShouldNotBeNull(problem);
        allocated.ShouldBeLessThan((long)(2.5 * PendingCommandFormat.MaxBodyBytes));
    }

    private static byte[] Bytes(PendingCommand entry) =>
        PendingCommandFormat.Serialize(entry, out var problem).ShouldNotBeNull(problem).SelectMany(p => p).ToArray();

    private static string? Refusal(byte[] data, string expectedId = Id)
    {
        PendingCommandFormat.Parse(data, expectedId, out var problem).ShouldBeNull();
        return problem;
    }

    /// <summary>Rewrites the payload (and its hash), so only the targeted field is wrong.</summary>
    private static byte[] WithPayload(byte[] payload)
    {
        var header = new byte[PendingCommandFormat.HeaderLength];
        Encoding.ASCII.GetBytes("GSPC").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        return [.. header, .. payload, .. SHA256.HashData(payload)];
    }

    private static byte[] Payload(string requestId, string playerId, string route, long recordedMs, byte[] body, byte[]? playerBytes = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Text(byte[] bytes)
        {
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        Text(Encoding.UTF8.GetBytes(requestId));
        Text(playerBytes ?? Encoding.UTF8.GetBytes(playerId));
        Text(Encoding.UTF8.GetBytes(route));
        writer.Write(recordedMs);
        writer.Write((uint)body.Length);
        writer.Write(body);
        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void An_entry_round_trips()
    {
        var entry = Entry();

        var read = PendingCommandFormat.Parse(Bytes(entry), Id, out var problem).ShouldNotBeNull(problem);

        read.RequestId.ShouldBe(Id);
        read.PlayerId.ShouldBe("p_1");
        read.Route.ShouldBe("api/v1/shop/buy");
        read.Body.ShouldBe(new byte[] { 1, 2, 3, 0, 255 });
        read.RecordedAtServerUtc.ShouldBe(Recorded);
    }

    [Fact]
    public void A_body_of_one_mebibyte_is_the_bound()
    {
        var body = RandomNumberGenerator.GetBytes(PendingCommandFormat.MaxBodyBytes);

        PendingCommandFormat.Parse(Bytes(Entry(body)), Id, out _).ShouldNotBeNull().Body.ShouldBe(body);
        PendingCommandFormat.Serialize(Entry(new byte[PendingCommandFormat.MaxBodyBytes + 1]), out var problem).ShouldBeNull();
        problem.ShouldStartWith("not saved: ");
    }

    [Fact]
    public void An_empty_body_round_trips()
    {
        PendingCommandFormat.Parse(Bytes(Entry([])), Id, out _).ShouldNotBeNull().Body.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void A_truncated_header_is_refused(int length) => Refusal(Bytes(Entry())[..length]).ShouldBe("truncated");

    [Fact]
    public void A_truncated_payload_or_hash_is_refused()
    {
        var data = Bytes(Entry());

        Refusal(data[..20]).ShouldBe("truncated");
        Refusal(data[..^1]).ShouldBe("truncated");
    }

    [Fact]
    public void A_wrong_magic_is_refused()
    {
        var data = Bytes(Entry());
        data[0] = (byte)'X';

        Refusal(data).ShouldBe("not a pending command file");
    }

    [Fact]
    public void Format_version_2_is_refused()
    {
        var data = Bytes(Entry());
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);

        Refusal(data).ShouldBe("unknown format version 2");
    }

    [Fact]
    public void Trailing_bytes_are_refused() => Refusal([.. Bytes(Entry()), 0]).ShouldBe("trailing bytes");

    [Fact]
    public void A_flipped_payload_byte_fails_the_integrity_check()
    {
        var data = Bytes(Entry());
        data[PendingCommandFormat.HeaderLength + 10] ^= 0x01;

        Refusal(data).ShouldBe("integrity check failed");
    }

    [Fact]
    public void Invalid_utf8_is_refused()
    {
        Refusal(WithPayload(Payload(Id, "x", "r", 0, [], playerBytes: [0xC3, 0x28]))).ShouldBe("invalid UTF-8");
    }

    [Theory]
    [InlineData("3F2A9C1E-7B11-4D2A-9C0D-5E6F7A8B9C0D")]
    [InlineData("3f2a9c1e+7b11-4d2a-9c0d-5e6f7a8b9c0d")]
    public void A_request_id_that_is_not_a_lowercase_uuid_is_refused(string requestId)
    {
        Refusal(WithPayload(Payload(requestId, "p", "r", 0, [])), requestId).ShouldBe("the request id is not a lowercase UUID");
    }

    [Fact]
    public void A_request_id_other_than_the_file_name_is_refused()
    {
        Refusal(Bytes(Entry()), "00000000-0000-4000-8000-000000000001").ShouldBe("the request id does not match the file name");
    }

    [Theory]
    [InlineData(37, 1, 1)]
    [InlineData(36, 129, 1)]
    [InlineData(36, 1, 513)]
    public void Lengths_past_their_bounds_are_refused(int idLength, int playerLength, int routeLength)
    {
        var payload = Payload(Id.PadRight(idLength, 'a'), new string('p', playerLength), new string('r', routeLength), 0, []);

        Refusal(WithPayload(payload)).ShouldBe("invalid content");
    }

    [Fact]
    public void A_length_beyond_the_payload_is_refused()
    {
        var payload = Payload(Id, "p", "r", 0, [9]);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(payload.Length - 5), 1000);

        Refusal(WithPayload(payload)).ShouldBe("truncated");
    }

    [Fact]
    public void An_empty_player_or_route_is_refused()
    {
        Refusal(WithPayload(Payload(Id, string.Empty, "r", 0, []))).ShouldBe("invalid content");
        Refusal(WithPayload(Payload(Id, "p", string.Empty, 0, []))).ShouldBe("invalid content");
    }

    [Fact]
    public void An_entry_needs_a_lowercase_uuid_player_and_route()
    {
        Should.Throw<ArgumentException>(() => new PendingCommand(Id.ToUpperInvariant(), "p", "r", [], Recorded)).ParamName.ShouldBe("requestId");
        Should.Throw<ArgumentException>(() => new PendingCommand(Id, string.Empty, "r", [], Recorded)).ParamName.ShouldBe("playerId");
        Should.Throw<ArgumentException>(() => new PendingCommand(Id, "p", string.Empty, [], Recorded)).ParamName.ShouldBe("route");
        Should.Throw<ArgumentNullException>(() => new PendingCommand(Id, "p", "r", null!, Recorded));
    }

    [Fact]
    public void An_entry_keeps_its_own_copy_of_the_body()
    {
        var body = new byte[] { 1, 2 };
        var entry = new PendingCommand(Id, "p", "r", body, Recorded);

        body[0] = 9;
        entry.Body[1] = 9;

        entry.Body.ShouldBe(new byte[] { 1, 2 });
    }
}
