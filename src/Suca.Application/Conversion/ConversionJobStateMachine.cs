using System;
using System.Collections.Generic;
using System.Linq;
using SUCA_Compressor.Application.Common;

namespace SUCA_Compressor.Application.Conversion
{
    // Conversion job state machine. The same states and transitions are mirrored
    // by the web client scheduler and the console runner.
    public enum ConversionJobState
    {
        Pending,
        Uploading,
        Queued,
        Running,
        Paused,
        Cancelling,
        Completed,
        Failed,
        Cancelled,
        Interrupted
    }

    public sealed class StateHistoryEntry
    {
        public string State { get; set; } = "";
        public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
        public long ElapsedMs { get; set; }
    }

    public static class ConversionJobStateMachine
    {
        private static readonly Dictionary<ConversionJobState, ConversionJobState[]> Transitions =
            new()
            {
                [ConversionJobState.Pending] = new[]
                    { ConversionJobState.Queued, ConversionJobState.Uploading, ConversionJobState.Running, ConversionJobState.Paused, ConversionJobState.Cancelling, ConversionJobState.Cancelled, ConversionJobState.Interrupted },
                [ConversionJobState.Uploading] = new[]
                    { ConversionJobState.Queued, ConversionJobState.Cancelling, ConversionJobState.Failed, ConversionJobState.Interrupted },
                [ConversionJobState.Queued] = new[]
                    { ConversionJobState.Running, ConversionJobState.Paused, ConversionJobState.Cancelling, ConversionJobState.Cancelled, ConversionJobState.Interrupted },
                [ConversionJobState.Running] = new[]
                    { ConversionJobState.Completed, ConversionJobState.Failed, ConversionJobState.Paused, ConversionJobState.Cancelling, ConversionJobState.Interrupted },
                [ConversionJobState.Paused] = new[]
                    { ConversionJobState.Queued, ConversionJobState.Running, ConversionJobState.Cancelling, ConversionJobState.Cancelled },
                [ConversionJobState.Cancelling] = new[]
                    { ConversionJobState.Cancelled, ConversionJobState.Completed, ConversionJobState.Failed },
                [ConversionJobState.Completed] = Array.Empty<ConversionJobState>(),
                [ConversionJobState.Failed] = Array.Empty<ConversionJobState>(),
                [ConversionJobState.Cancelled] = Array.Empty<ConversionJobState>(),
                [ConversionJobState.Interrupted] = Array.Empty<ConversionJobState>()
            };

        public static bool IsTerminal(ConversionJobState state) =>
            state is ConversionJobState.Completed or ConversionJobState.Failed
                or ConversionJobState.Cancelled or ConversionJobState.Interrupted;

        public static bool CanTransition(ConversionJobState from, ConversionJobState to) =>
            from != to && Transitions[from].Contains(to);

        public static Result Transition(ConversionJobState from, ConversionJobState to)
        {
            return CanTransition(from, to)
                ? Result.Ok()
                : Result.Fail(
                    $"Transicion invalida: {from} -> {to}.",
                    ConversionErrorCodes.InvalidStateTransition);
        }
    }
}
