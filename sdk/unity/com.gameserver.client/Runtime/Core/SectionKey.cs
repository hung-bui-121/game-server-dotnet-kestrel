#nullable enable
using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace GameServer.Client
{
    /// <summary>A section id bound to the parser of its message (generated code creates these; FG-028).</summary>
    public abstract class SectionKey
    {
        protected SectionKey(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("A section key needs a section id.", nameof(id));
            }

            Id = id;
        }

        /// <summary>Section (aggregate) id, as sent by the server.</summary>
        public string Id { get; }

        /// <summary>Parser of the section's message.</summary>
        public abstract MessageParser Parser { get; }
    }

    /// <summary>A section id bound to the parser of message <typeparamref name="T"/>.</summary>
    public sealed class SectionKey<T> : SectionKey
        where T : class, IMessage<T>
    {
        public SectionKey(string id, MessageParser<T> parser)
            : base(id)
        {
            TypedParser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        /// <summary>The typed parser (<see cref="SectionStore"/> parses with it).</summary>
        public MessageParser<T> TypedParser { get; }

        public override MessageParser Parser => TypedParser;
    }

    /// <summary>Section id → key; generated per origin (<c>FrameworkSections.Table</c>, a game's <c>GameSections.Table</c>).</summary>
    public sealed class SectionTable
    {
        private readonly List<SectionKey> _keys = new List<SectionKey>();
        private readonly Dictionary<string, SectionKey> _byId = new Dictionary<string, SectionKey>(StringComparer.Ordinal);

        /// <summary>Throws <see cref="ArgumentException"/> when two keys share an id.</summary>
        public SectionTable(params SectionKey[] keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            foreach (var key in keys)
            {
                if (key == null)
                {
                    throw new ArgumentException("A section table cannot hold a null key.", nameof(keys));
                }

                if (_byId.ContainsKey(key.Id))
                {
                    throw new ArgumentException($"Section '{key.Id}' is listed twice in one section table.", nameof(keys));
                }

                _byId.Add(key.Id, key);
                _keys.Add(key);
            }
        }

        /// <summary>The keys in the order they were given.</summary>
        public IReadOnlyCollection<SectionKey> Keys => _keys;

        public bool TryGet(string id, out SectionKey key) => _byId.TryGetValue(id, out key!);
    }

    /// <summary>Outcome of reading a section by id without knowing its message type.</summary>
    public enum SectionRead
    {
        /// <summary>The client has not received the section.</summary>
        Absent,

        /// <summary>The client has the section but no registered table knows its id: it is kept raw, not parsed.</summary>
        Unknown,

        /// <summary>The section was parsed with its key's parser.</summary>
        Parsed,
    }
}
