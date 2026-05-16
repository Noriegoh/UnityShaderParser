using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityShaderParser.HLSL;

namespace UnityShaderParser.Common
{
    /// <summary>
    /// Stateful session that maintains cached lexer/parser results and
    /// applies incremental updates when the source text changes.
    /// 
    /// Uses a queue-based async model: edits are enqueued from any thread
    /// (typically the UI thread) and processed sequentially on a dedicated
    /// background thread. This ensures the parser is never blocked by rapid
    /// keystrokes and always processes the latest state.
    /// 
    /// Usage:
    ///   1. Call <see cref="Initialize"/> with the initial text and config.
    ///   2. On each text edit, call <see cref="EnqueueChange"/>.
    ///   3. Read <see cref="Declarations"/>, <see cref="Tokens"/>, <see cref="Diagnostics"/>
    ///      at any time (thread-safe reads against a snapshot).
    ///   4. Call <see cref="Dispose"/> when done.
    /// </summary>
    public sealed class IncrementalParseSession : IDisposable
    {
        // ── Internal state ──────────────────────────────────────────
        private IncrementalLexer _lexer;
        private IncrementalParser _parser;
        private HLSLParserConfig _config;
        private IIncrementalParseCallback _callback;

        // Snapshot state (volatile reads/writes for cross-thread visibility)
        private volatile IReadOnlyList<Token<HLSL.TokenKind>> _tokensSnapshot;
        private volatile IReadOnlyList<HLSLSyntaxNode> _declsSnapshot;
        private volatile IReadOnlyList<Diagnostic> _diagsSnapshot;
        private volatile int _version;
        private volatile string _currentFullText;

        // ── Queue-based async processing ────────────────────────────
        private readonly BlockingCollection<ChangeRequest> _changeQueue;
        private readonly Thread _workerThread;
        private readonly CancellationTokenSource _cts;
        private bool _disposed;

        /// <summary>
        /// Represents a pending change request in the queue.
        /// </summary>
        private readonly struct ChangeRequest
        {
            public TextChange Change { get; }
            public ITextSnapshot Snapshot { get; }

            public ChangeRequest(TextChange change, ITextSnapshot snapshot)
            {
                Change = change;
                Snapshot = snapshot;
            }
        }

        /// <summary>
        /// Creates a new incremental parse session.
        /// </summary>
        /// <param name="callback">
        /// Optional callback to receive AST change notifications. May be null.
        /// </param>
        public IncrementalParseSession(IIncrementalParseCallback callback = null)
        {
            _callback = callback;
            _changeQueue = new BlockingCollection<ChangeRequest>(new ConcurrentQueue<ChangeRequest>());
            _cts = new CancellationTokenSource();

            _tokensSnapshot = Array.Empty<Token<HLSL.TokenKind>>();
            _declsSnapshot = Array.Empty<HLSLSyntaxNode>();
            _diagsSnapshot = Array.Empty<Diagnostic>();

            // Start the background worker thread
            _workerThread = new Thread(ProcessChangeLoop)
            {
                IsBackground = true,
                Name = "IncrementalParseSession.Worker"
            };
            _workerThread.Start();
        }

        // ── Public API: Thread-safe reads ───────────────────────────

        /// <summary>
        /// The current top-level declarations. Thread-safe snapshot.
        /// </summary>
        public IReadOnlyList<HLSLSyntaxNode> Declarations => _declsSnapshot;

        /// <summary>
        /// The current token list. Thread-safe snapshot.
        /// </summary>
        public IReadOnlyList<Token<HLSL.TokenKind>> Tokens => _tokensSnapshot;

        /// <summary>
        /// The current diagnostics. Thread-safe snapshot.
        /// </summary>
        public IReadOnlyList<Diagnostic> Diagnostics => _diagsSnapshot;

        /// <summary>
        /// The version number of the latest processed state.
        /// </summary>
        public int Version => _version;

        /// <summary>
        /// Whether the session has been initialized.
        /// </summary>
        public bool IsInitialized => _lexer != null;

        // ── Public API: Initialization ──────────────────────────────

        /// <summary>
        /// Perform a full initial parse (cold start).
        /// This is synchronous — call once before enqueuing changes.
        /// </summary>
        public void Initialize(ITextSnapshot snapshot, HLSLParserConfig config)
        {
            _config = config ?? new HLSLParserConfig();
            _currentFullText = snapshot.GetFullText();

            string basePath = _config.BasePath ?? string.Empty;
            string fileName = _config.FileName ?? string.Empty;

            _lexer = new IncrementalLexer(basePath, fileName);
            _parser = new IncrementalParser(_config);

            // Full lex
            var lexDiags = _lexer.Initialize(_currentFullText);

            // Full parse
            var parseDiags = _parser.Initialize(new List<Token<HLSL.TokenKind>>(_lexer.MutableTokens));

            // Build diagnostics snapshot
            var allDiags = new List<Diagnostic>(lexDiags.Count + parseDiags.Count);
            allDiags.AddRange(lexDiags);
            allDiags.AddRange(parseDiags);

            // Publish snapshots
            _tokensSnapshot = _lexer.Tokens;
            _declsSnapshot = _parser.Declarations;
            _diagsSnapshot = allDiags;
            _version = snapshot.Version;
        }

        /// <summary>
        /// Convenience overload that initializes from a plain string.
        /// Creates a <see cref="StringTextSnapshot"/> internally.
        /// </summary>
        public void Initialize(string source, HLSLParserConfig config)
        {
            Initialize(new StringTextSnapshot(source, 0), config);
        }

        // ── Public API: Enqueue changes ─────────────────────────────

        /// <summary>
        /// Enqueue a text change for async incremental processing.
        /// Thread-safe — can be called from the UI thread.
        /// 
        /// The snapshot must contain the text AFTER the change has been applied.
        /// </summary>
        public void EnqueueChange(TextChange change, ITextSnapshot snapshot)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(IncrementalParseSession));
            if (!IsInitialized)
                throw new InvalidOperationException("Session must be initialized before enqueuing changes.");

            _changeQueue.Add(new ChangeRequest(change, snapshot));
        }

        /// <summary>
        /// Convenience overload for when the snapshot is a plain string.
        /// </summary>
        public void EnqueueChange(TextChange change, string newFullText, int version = 0)
        {
            EnqueueChange(change, new StringTextSnapshot(newFullText, version));
        }

        /// <summary>
        /// Synchronously applies a single change (for testing / non-async use).
        /// Not thread-safe — must not be called concurrently with EnqueueChange.
        /// </summary>
        public void ApplyChangeSynchronous(TextChange change, ITextSnapshot snapshot)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Session must be initialized before applying changes.");

            ProcessSingleChange(change, snapshot);
        }

        /// <summary>
        /// Convenience overload for synchronous apply with a plain string.
        /// </summary>
        public void ApplyChangeSynchronous(TextChange change, string newFullText, int version = 0)
        {
            ApplyChangeSynchronous(change, new StringTextSnapshot(newFullText, version));
        }

        // ── Background worker ───────────────────────────────────────

        /// <summary>
        /// Background thread loop that processes enqueued changes.
        /// Drains the queue so that if multiple edits arrive, we skip
        /// intermediate states and only process the latest.
        /// </summary>
        private void ProcessChangeLoop()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    // Block until a change is available
                    ChangeRequest request;
                    try
                    {
                        request = _changeQueue.Take(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Drain the queue — if more changes arrived, process them all
                    // but we can apply them sequentially since each produces the
                    // state needed by the next.
                    ProcessSingleChange(request.Change, request.Snapshot);

                    // Process any additional queued changes immediately
                    while (_changeQueue.TryTake(out var next))
                    {
                        ProcessSingleChange(next.Change, next.Snapshot);
                    }
                }
            }
            catch (Exception)
            {
                // Swallow exceptions in the background thread.
                // In production, you'd log this.
            }
        }

        /// <summary>
        /// Processes a single text change: incremental lex → re-parse → publish snapshots.
        /// </summary>
        private void ProcessSingleChange(TextChange change, ITextSnapshot snapshot)
        {
            string newFullText = snapshot.GetFullText();
            int oldTokenCount = _lexer.MutableTokens.Count;

            // ─── Incremental Lex ───
            var (firstDirty, newCount, lexDiags) = _lexer.ApplyChange(change, newFullText);

            // Calculate how many old tokens were replaced
            int oldReplacedCount = oldTokenCount - _lexer.MutableTokens.Count + newCount;
            if (oldReplacedCount < 0) oldReplacedCount = 0;

            // ─── Incremental Parse ───
            var parseDiags = _parser.ApplyChange(
                _lexer.MutableTokens,
                firstDirty,
                newCount,
                oldReplacedCount,
                _callback);

            // ─── Build diagnostics ───
            var allDiags = new List<Diagnostic>(lexDiags.Count + parseDiags.Count);
            allDiags.AddRange(lexDiags);
            allDiags.AddRange(parseDiags);

            // ─── Publish snapshots atomically ───
            _currentFullText = newFullText;
            _tokensSnapshot = _lexer.Tokens;
            _declsSnapshot = _parser.Declarations;
            _diagsSnapshot = allDiags;
            _version = snapshot.Version;
        }

        // ── IDisposable ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _changeQueue.CompleteAdding();

            // Give the worker thread a moment to exit gracefully
            if (_workerThread.IsAlive)
            {
                _workerThread.Join(TimeSpan.FromSeconds(2));
            }

            _cts.Dispose();
            _changeQueue.Dispose();
        }
    }
}
