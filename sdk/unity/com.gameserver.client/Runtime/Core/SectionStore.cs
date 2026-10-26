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
    /// Sections by aggregate id with their <c>rev</c> (SDK-03). Full updates replace a section unless they are older than what
    /// the client has; partial updates apply only on the exact next revision, otherwise the section is reported stale and the
    /// client refetches it.
    /// </summary>
    public sealed class SectionStore
    {
        private readonly Dictionary<string, SectionState> _sections = new Dictionary<string, SectionState>(StringComparer.Ordinal);

        /// <summary>Raised after a section changed (id).</summary>
        public event Action<string>? SectionChanged;

        public IReadOnlyCollection<SectionState> All => _sections.Values;

        public bool TryGet(string id, out SectionState section) => _sections.TryGetValue(id, out section!);

        public long RevOf(string id) => _sections.TryGetValue(id, out var section) ? section.Rev : 0;

        /// <summary>Parses the payload of a section; null when the client does not have it.</summary>
        public T? Get<T>(string id, MessageParser<T> parser)
            where T : class, IMessage<T> =>
            _sections.TryGetValue(id, out var section) ? parser.ParseFrom(section.Payload) : null;

        /// <summary>Snapshot/login sections are authoritative at their own revision.</summary>
        public void Set(Section section) => Store(section.Id, section.Rev, section.Payload);

        /// <summary>Applies a <c>SectionUpdate</c>; false when the section is now stale and must be refetched.</summary>
        public bool Apply(SectionUpdate update)
        {
            _sections.TryGetValue(update.Id, out var current);
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

        public void Clear() => _sections.Clear();

        private void Store(string id, long rev, ByteString payload)
        {
            _sections[id] = new SectionState(id, rev, payload);
            SectionChanged?.Invoke(id);
        }
    }
}
