#nullable enable
using System;
using System.Collections.Generic;
using Google.Protobuf;
using GameServer.Protocol.Core.V1;

namespace GameServer.Client
{
    /// <summary>One client-visible aggregate as last received (API-10).</summary>
    public sealed class SectionState
    {
        public SectionState(string id, long rev, ByteString payload)
        {
            Id = id;
            Rev = rev;
            Payload = payload;
        }

        public string Id { get; }

        public long Rev { get; }

        public ByteString Payload { get; }
    }

    /// <summary>
    /// A section the server could not send in its last answer (FG-029): <see cref="Code"/> is <c>AGGREGATE_VERSION_AHEAD</c> (a rolling
    /// deploy; a later read usually works) or <c>AGGREGATE_UNREADABLE</c>. The store keeps the last good copy of the section.
    /// </summary>
    public sealed class UnavailableSectionState
    {
        public UnavailableSectionState(string id, string code, int codeNumber)
        {
            Id = id;
            Code = code;
            CodeNumber = codeNumber;
        }

        public string Id { get; }

        public string Code { get; }

        public int CodeNumber { get; }
    }

    /// <summary>
    /// Sections by aggregate id with their <c>rev</c> (SDK-03). Full updates replace a section unless they are older than what
    /// the client has; partial updates apply only on the exact next revision, otherwise the section is reported stale and the
    /// client refetches it. Sections are read through typed keys of registered tables (FG-028); ids no table knows are still
    /// stored raw and never parsed. A section the server lists as unavailable (FG-029) is marked in <see cref="Unavailable"/> and keeps
    /// its last good copy until a later answer carries it again.
    /// </summary>
    public sealed class SectionStore
    {
        private readonly Dictionary<string, SectionState> _sections = new Dictionary<string, SectionState>(StringComparer.Ordinal);
        private readonly Dictionary<string, UnavailableSectionState> _unavailable = new Dictionary<string, UnavailableSectionState>(StringComparer.Ordinal);
        private readonly Dictionary<string, SectionKey> _keys = new Dictionary<string, SectionKey>(StringComparer.Ordinal);

        /// <summary>Creates an empty store that knows the framework sections (<see cref="FrameworkSections.Table"/>).</summary>
        public SectionStore()
        {
            Register(FrameworkSections.Table);
        }

        /// <summary>Raised after a section changed (id).</summary>
        public event Action<string>? SectionChanged;

        /// <summary>Raised when a section becomes unavailable or available again (id); not raised by <see cref="Clear"/>.</summary>
        public event Action<string>? AvailabilityChanged;

        /// <summary>A <see cref="SectionChanged"/> or <see cref="AvailabilityChanged"/> handler threw; the store caught it.</summary>
        public event Action<Exception>? HandlerFailed;

        /// <summary>Sections the last answers listed as unavailable, by id; their last good copy (if any) is still in the store.</summary>
        public IReadOnlyDictionary<string, UnavailableSectionState> Unavailable => _unavailable;

        public bool IsUnavailable(string id) => _unavailable.ContainsKey(id);

        public IReadOnlyCollection<SectionState> All => _sections.Values;

        public bool TryGet(string id, out SectionState section) => _sections.TryGetValue(id, out section!);

        public long RevOf(string id) => _sections.TryGetValue(id, out var section) ? section.Rev : 0;

        /// <summary>Parses the payload of a section; null when the client does not have it.</summary>
        public T? Get<T>(string id, MessageParser<T> parser)
            where T : class, IMessage<T> =>
            _sections.TryGetValue(id, out var section) ? parser.ParseFrom(section.Payload) : null;

        /// <summary>
        /// Adds a generated table (a game's <c>GameSections.Table</c>; the framework table is registered by the constructor). Registering
        /// the same id with the same message type again does nothing; the same id with another message type throws
        /// <see cref="ArgumentException"/> and registers none of the table's keys.
        /// </summary>
        public void Register(SectionTable table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            foreach (var key in table.Keys)
            {
                // A generated key is a SectionKey<TMessage>: its runtime type names the message type.
                if (_keys.TryGetValue(key.Id, out var known) && known.GetType() != key.GetType())
                {
                    throw new ArgumentException(
                        $"Section '{key.Id}' is already registered as {known.GetType()} and cannot be registered as {key.GetType()}. " +
                        "Use one generated table per section id.",
                        nameof(table));
                }
            }

            foreach (var key in table.Keys)
            {
                _keys.TryAdd(key.Id, key);
            }
        }

        /// <summary>True when a registered table knows the section id.</summary>
        public bool IsKnown(string id) => _keys.ContainsKey(id);

        /// <summary>The section parsed with its key's parser; null when the client does not have it.</summary>
        public T? Get<T>(SectionKey<T> key)
            where T : class, IMessage<T>
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _sections.TryGetValue(key.Id, out var section) ? key.TypedParser.ParseFrom(section.Payload) : null;
        }

        /// <summary>
        /// Reads a section without knowing its type: <see cref="SectionRead.Absent"/> (not received), <see cref="SectionRead.Unknown"/>
        /// (no registered table knows the id: kept raw, not parsed) or <see cref="SectionRead.Parsed"/> with the parsed message.
        /// </summary>
        public SectionRead TryRead(string id, out IMessage? message)
        {
            message = null;
            if (!_sections.TryGetValue(id, out var section))
            {
                return SectionRead.Absent;
            }

            if (!_keys.TryGetValue(id, out var key))
            {
                return SectionRead.Unknown;
            }

            message = key.Parser.ParseFrom(section.Payload);
            return SectionRead.Parsed;
        }

        /// <summary>
        /// Stores a snapshot/login section unless the client already has a newer revision (a snapshot read before a command's
        /// update can arrive after it). Login clears the store first. The section is available again.
        /// </summary>
        public void Set(Section section)
        {
            MarkAvailable(section.Id);
            if (_sections.TryGetValue(section.Id, out var current) && section.Rev < current.Rev)
            {
                return;
            }

            Store(section.Id, section.Rev, section.Payload);
        }

        /// <summary>
        /// Applies a <c>SectionUpdate</c>; false when the section is now stale and must be refetched. An applied update makes the
        /// section available again.
        /// </summary>
        public bool Apply(SectionUpdate update)
        {
            _sections.TryGetValue(update.Id, out var current);
            if (update.Full || (current != null && current.Rev == update.BaseRev && update.NewRev == update.BaseRev + 1))
            {
                MarkAvailable(update.Id);
            }

            if (update.Full)
            {
                // Retries/replays return the current aggregate (rev >= newRev); never go back in time.
                if (current == null || update.NewRev >= current.Rev)
                {
                    Store(update.Id, update.NewRev, update.Payload);
                }

                return true;
            }

            if (current != null && current.Rev == update.BaseRev && update.NewRev == update.BaseRev + 1)
            {
                Store(update.Id, update.NewRev, update.Payload);
                return true;
            }

            return false;
        }

        /// <summary>Removes every section and every unavailable mark (no events).</summary>
        public void Clear()
        {
            _sections.Clear();
            _unavailable.Clear();
        }

        /// <summary>
        /// A same-player login (FG-029, research R12): removes every section except <paramref name="keep"/>, whose last good copies
        /// stay because the login lists them as unavailable; marks are cleared (the login marks again).
        /// </summary>
        internal void ClearExcept(ICollection<string> keep)
        {
            foreach (var id in new List<string>(_sections.Keys))
            {
                if (!keep.Contains(id))
                {
                    _sections.Remove(id);
                }
            }

            _unavailable.Clear();
        }

        /// <summary>Marks the sections an answer lists in <c>unavailable_sections</c>; the last good copy of each stays.</summary>
        internal void MarkUnavailable(IEnumerable<UnavailableSection> sections)
        {
            foreach (var section in sections)
            {
                var known = _unavailable.TryGetValue(section.Id, out var current);
                _unavailable[section.Id] = new UnavailableSectionState(section.Id, section.Code, section.CodeNumber);
                if (!known || current!.Code != section.Code)
                {
                    Raise(AvailabilityChanged, section.Id);
                }
            }
        }

        private void MarkAvailable(string id)
        {
            if (_unavailable.Remove(id))
            {
                Raise(AvailabilityChanged, id);
            }
        }

        private void Store(string id, long rev, ByteString payload)
        {
            _sections[id] = new SectionState(id, rev, payload);
            Raise(SectionChanged, id);
        }

        private void Raise(Action<string>? handler, string id)
        {
            if (handler == null)
            {
                return;
            }

            foreach (var single in handler.GetInvocationList())
            {
                try
                {
                    ((Action<string>)single)(id);
                }
#pragma warning disable CA1031 // Game handlers must not break applying server state.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    HandlerFailed?.Invoke(ex);
                }
            }
        }
    }
}
