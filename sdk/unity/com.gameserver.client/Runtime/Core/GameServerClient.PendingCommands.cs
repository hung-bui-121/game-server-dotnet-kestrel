#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using GameServer.Protocol.Core.V1;

namespace GameServer.Client
{
    /// <summary>Caller-owned request ids and the opt-in pending-command store (FG-022, contracts/sdk-pending-store.md §4).</summary>
    public sealed partial class GameServerClient
    {
        private static readonly IReadOnlyList<PendingCommand> NoPendingCommands = Array.Empty<PendingCommand>();

        /// <summary>
        /// Entries of the pending-command store (<c>Options.PendingCommands</c>); empty without a store. A snapshot: resending or
        /// discarding inside a <c>foreach</c> over it is safe; read it again for the current entries.
        /// </summary>
        public IReadOnlyList<PendingCommand> PendingCommands => _options.PendingCommands?.Entries ?? NoPendingCommands;

        /// <summary>A fresh request id (lowercase UUID, format D), e.g. to record in the game's own state before sending.</summary>
        public static string NewRequestId() => Guid.NewGuid().ToString("D");

        /// <summary>
        /// SendCommandAsync with a caller-owned request id: every attempt sends it with the same bytes; the same id and bytes in a
        /// later call (also after an app restart) replay the committed command (CMD-02, CMD-07). Null = the SDK generates one.
        /// Throws ArgumentException for an id that is not a UUID in format D, before any network call or disk write. With
        /// <c>Options.PendingCommands</c>, the command is recorded before the first attempt and removed once the server answered
        /// it (a command response with any code, or an error that is not retryable); it stays when the outcome is unknown.
        /// </summary>
        public async Task<CommandResult<TResult>> SendCommandAsync<TResult>(
            string route,
            IMessage request,
            MessageParser<TResult> resultParser,
            string? requestId,
            CancellationToken cancellationToken = default)
            where TResult : class, IMessage<TResult>
        {
            var id = requestId == null ? NewRequestId() : NormalizeRequestId(requestId);
            var body = request.ToByteArray();
            var token = RequireToken();
            var store = _options.PendingCommands;
            var playerId = _session.PlayerId;
            if (store != null && !string.IsNullOrEmpty(playerId))
            {
                // Before the first attempt: from here on the command may reach the server.
                store.Record(new PendingCommand(id, playerId!, route, body, ServerNow));
            }

            return await SendCommandCoreAsync(route, body, id, token, resultParser, cancellationToken);
        }

        /// <summary>
        /// <see cref="PendingCommandStatus.OtherPlayer"/> for an entry of another player or without a session;
        /// <see cref="PendingCommandStatus.Expired"/> when it is at least <c>PendingCommandMaxAge</c> old on the server clock
        /// (<see cref="ServerNow"/>, so a device clock change does not move it); otherwise <see cref="PendingCommandStatus.Resendable"/>.
        /// </summary>
        public PendingCommandStatus StatusOf(PendingCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (!HasSession || !string.Equals(_session.PlayerId, command.PlayerId, StringComparison.Ordinal))
            {
                return PendingCommandStatus.OtherPlayer;
            }

            return ServerNow - command.RecordedAtServerUtc >= _options.PendingCommandMaxAge
                ? PendingCommandStatus.Expired
                : PendingCommandStatus.Resendable;
        }

        /// <summary>
        /// Resends the stored bytes with the stored request id to the stored route, and returns the result like SendCommandAsync (a
        /// committed command answers <c>Replayed</c> with its receipt). The same remove/keep rules apply. Never resends by itself: the
        /// game calls it after login. Throws InvalidOperationException when the entry is not Resendable (another player, expired,
        /// or unknown to the store).
        /// </summary>
        public Task<CommandResult<TResult>> ResendPendingAsync<TResult>(
            PendingCommand command,
            MessageParser<TResult> resultParser,
            CancellationToken cancellationToken = default)
            where TResult : class, IMessage<TResult>
        {
            var status = StatusOf(command);
            if (status != PendingCommandStatus.Resendable)
            {
                throw new InvalidOperationException($"Pending command {command.RequestId} is {status}: only a Resendable entry is resent.");
            }

            if (!PendingCommands.Any(c => string.Equals(c.RequestId, command.RequestId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Pending command {command.RequestId} is not in the store: it was answered or discarded.");
            }

            return SendCommandCoreAsync(command.Route, command.BodyBytes, command.RequestId, RequireToken(), resultParser, cancellationToken);
        }

        /// <summary>Removes an entry (the game decided from state, e.g. an Expired one). Unknown ids are ignored.</summary>
        public void DiscardPendingCommand(string requestId)
        {
            var store = _options.PendingCommands;
            if (store != null && store.Entries.Any(c => string.Equals(c.RequestId, requestId, StringComparison.Ordinal)))
            {
                store.Remove(requestId);
            }
        }

        /// <summary>A UUID in format D (any case) as lowercase; anything else is the caller's bug.</summary>
        private static string NormalizeRequestId(string requestId)
        {
            if (requestId.Length != PendingCommandFormat.RequestIdLength || !Guid.TryParseExact(requestId, "D", out var parsed))
            {
                throw new ArgumentException("The request id must be a UUID in format D (8-4-4-4-12 hex digits), e.g. from GameServerClient.NewRequestId().", nameof(requestId));
            }

            return parsed.ToString("D");
        }

        /// <summary>
        /// A final answer removes the pending entry: an error envelope (the server answered; its codes have numbers) that is not
        /// retryable. SDK-made errors (no response, unreadable response) and retryable answers leave the outcome unknown. An
        /// authentication answer is not final either: the request was refused before the command ran, so the entry stays for a
        /// resend after the next login (T082 m6). <c>ACCOUNT_DELETED</c> is final: no later login of that account can run the
        /// resend (T082 round 2, m3).
        /// </summary>
        private static bool IsFinalAnswer(GameServerError error) => error.CodeNumber >= 0 && !error.Retryable && !IsAuthenticationAnswer(error.Code);

        /// <summary><c>UNAUTHENTICATED</c>, <c>SESSION_REPLACED</c> and every <c>ACCOUNT_*</c> code except <c>ACCOUNT_DELETED</c> (e.g. suspended).</summary>
        private static bool IsAuthenticationAnswer(string code) =>
            code == ErrorCodes.Unauthenticated
            || code == ErrorCodes.SessionReplaced
            || (code.StartsWith("ACCOUNT_", StringComparison.Ordinal) && code != ErrorCodes.AccountDeleted);

        private async Task<CommandResult<TResult>> SendCommandCoreAsync<TResult>(
            string route,
            byte[] body,
            string requestId,
            string token,
            MessageParser<TResult> resultParser,
            CancellationToken cancellationToken)
            where TResult : class, IMessage<TResult>
        {
            var store = _options.PendingCommands;
            CommandResponse response;
            try
            {
                response = await SendAsync("POST", route, body, token, RetryMode.Idempotent, CommandResponse.Parser, r => r.ServerTimeUtcMs, requestId, cancellationToken);
            }
            catch (GameServerException ex) when (store != null && IsFinalAnswer(ex.Error))
            {
                store.Remove(requestId);
                throw;
            }

            if (string.IsNullOrEmpty(response.Code))
            {
                throw new GameServerException(GameServerError.Client(ErrorCodes.InvalidResponse, "Command response without a code.", false, requestId));
            }

            // The server answered with its final word for this request id (any code): nothing is left to resend.
            store?.Remove(requestId);

            // From here on the command is known to the server: report problems, never throw (a caller retrying with a new
            // request id would apply it twice).
            TrackConfig(response.ConfigVersion, response.ClientConfigVersion);
            var stale = response.Updates.Where(u => !Sections.Apply(u)).Select(u => u.Id).ToList();
            Sections.MarkUnavailable(response.UnavailableSections);
            var sectionsStale = false;
            if (stale.Count > 0)
            {
                try
                {
                    await RefreshSectionsAsync(stale, cancellationToken);
                }
                catch (Exception ex) when (ex is GameServerException || ex is OperationCanceledException)
                {
                    sectionsStale = true;
                }
            }

            TResult? result = null;
            var unreadable = false;
            if (response.Code == ErrorCodes.Ok)
            {
                try
                {
                    result = resultParser.ParseFrom(response.Result);
                }
                catch (InvalidProtocolBufferException)
                {
                    unreadable = true;
                }
            }

            return new CommandResult<TResult>(response, result, sectionsStale, unreadable, requestId);
        }
    }
}
