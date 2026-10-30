using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Tests.Persistence;

/// <summary>Slice 011 R7: key ↔ file name (FR-021, D6), safe on case-folding file systems and Windows device names.</summary>
public sealed class FileKeyCodecTests
{
    [Theory]
    [InlineData("p_7f3k2", "p_7f3k2")]
    [InlineData("rcpt::9a1c", "rcpt%3A%3A9a1c")]
    [InlineData("extref::iap_google::GPA.1234-5678", "extref%3A%3Aiap_google%3A%3A%47%50%41.1234-5678")]
    [InlineData("Keyring", "%4Beyring")]
    [InlineData("keyring", "keyring")]
    [InlineData("con", "%63on")]
    [InlineData("con.txt", "%63on.txt")]
    [InlineData("lpt1", "%6Cpt1")]
    [InlineData("CON", "%43%4F%4E")]
    [InlineData("console", "console")]
    public void Data_model_examples(string key, string stem) => FileKeyCodec.Encode(key).ShouldBe(stem);

    [Theory]
    [InlineData("a::b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("100%")]
    [InlineData("UPPER lower")]
    [InlineData("người chơi ✓ 🎮")]
    [InlineData("nul")]
    [InlineData(".")]
    [InlineData("~tilde")]
    public void Keys_round_trip(string key)
    {
        var stem = FileKeyCodec.Encode(key);

        FileKeyCodec.TryDecode(stem, out var decoded).ShouldBeTrue(stem);
        decoded.ShouldBe(key);
        FileKeyCodec.Matches(stem, key).ShouldBeTrue();
    }

    [Fact]
    public void Long_keys_are_hashed_and_match_through_the_stored_key()
    {
        var key = new string('k', 500);
        var stem = FileKeyCodec.Encode(key);

        stem.Length.ShouldBe(FileKeyCodec.HashedStemLength);
        stem.ShouldMatch("^~[a-z2-7]{52}$");
        FileKeyCodec.TryDecode(stem, out _).ShouldBeFalse();
        FileKeyCodec.Matches(stem, key).ShouldBeTrue();
        FileKeyCodec.Matches(stem, key + "x").ShouldBeFalse();

        // 120 encoded characters stay readable; one more is hashed.
        FileKeyCodec.Encode(new string('a', FileKeyCodec.MaxStemLength)).ShouldBe(new string('a', FileKeyCodec.MaxStemLength));
        FileKeyCodec.Encode(new string('a', FileKeyCodec.MaxStemLength + 1)).ShouldStartWith("~");
        FileKeyCodec.Encode(new string(':', 41)).ShouldStartWith("~"); // 41 x %3A = 123 characters
    }

    [Fact]
    public void Distinct_keys_never_give_names_equal_under_case_folding()
    {
        var random = new Random(11);
        const string alphabet = "aAbBkK:%_.-/ éÉ0~";
        var keys = new HashSet<string>(StringComparer.Ordinal) { "Keyring", "keyring", "KEYRING", "con", "Con", "%4Beyring", "%4beyring" };
        while (keys.Count < 5000)
        {
            keys.Add(new string(Enumerable.Range(0, random.Next(1, 12)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            names.TryAdd(FileKeyCodec.Encode(key), key).ShouldBeTrue($"'{key}' collides with '{names.GetValueOrDefault(FileKeyCodec.Encode(key))}'");
        }
    }

    [Theory]
    [InlineData("%4beyring")] // lowercase hex
    [InlineData("%61bc")] // encoded literal 'a'
    [InlineData("%63at")] // encoded literal outside a device name
    [InlineData("con")] // device name left literal
    [InlineData("Keyring")] // uppercase literal
    [InlineData("a%")] // truncated escape
    [InlineData("a%4")]
    [InlineData("a b")] // space is never literal
    [InlineData("%FF")] // not UTF-8
    [InlineData("~abc")] // hashed names cannot be decoded
    [InlineData("")]
    public void Non_canonical_and_foreign_names_are_refused(string stem) => FileKeyCodec.TryDecode(stem, out _).ShouldBeFalse();

    [Fact]
    public void The_empty_key_is_refused() => Should.Throw<ArgumentException>(() => FileKeyCodec.Encode(string.Empty));
}
