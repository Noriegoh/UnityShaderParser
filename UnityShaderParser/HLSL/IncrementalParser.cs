using System;
using System.Collections.Generic;
using System.Linq;
using UnityShaderParser.Common;
using UnityShaderParser.HLSL.PreProcessor;

namespace UnityShaderParser.HLSL
{
    using HLSLToken = Token<TokenKind>;

    /// <summary>
    /// Performs declaration-level incremental re-parsing of HLSL source code.
    /// 
    /// Each top-level declaration (function, struct, cbuffer, global variable, etc.)
    /// is tracked by the token range it covers. When the token stream changes,
    /// only the declarations whose token ranges overlap the dirty region are
    /// re-parsed.
    /// 
    /// The algorithm:
    /// 1. After incremental lexing, determine which old declaration indices overlap
    ///    the changed token range ("dirty" declarations).
    /// 2. Expand the dirty range to include adjacent declarations when the edit may
    ///    have merged/split them (e.g., inserting/deleting `}` or `;`).
    /// 3. Collect all tokens in the dirty range and run the standard HLSLParser
    ///    on that token sub-slice.
    /// 4. Splice the new declarations into the cached AST, replacing the dirty ones.
    /// 5. Fire diff notifications via IIncrementalParseCallback.
    /// </summary>
    public class IncrementalParser
    {
        /// <summary>
        /// Tracks the token range for a single top-level declaration.
        /// </summary>
        private struct DeclRange
        {
            public int FirstTokenIndex;
            public int LastTokenIndex; // Inclusive
            public HLSLSyntaxNode Node;

            public DeclRange(int first, int last, HLSLSyntaxNode node)
            {
                FirstTokenIndex = first;
                LastTokenIndex = last;
                Node = node;
            }
        }

        private List<DeclRange> _declRanges;
        private List<HLSLSyntaxNode> _declarations;
        private HLSLParserConfig _config;

        public IncrementalParser(HLSLParserConfig config)
        {
            _config = config ?? new HLSLParserConfig();
            _declRanges = new List<DeclRange>();
            _declarations = new List<HLSLSyntaxNode>();
        }

        /// <summary>
        /// The current list of top-level declarations.
        /// </summary>
        public IReadOnlyList<HLSLSyntaxNode> Declarations => _declarations;

        /// <summary>
        /// Perform a full initial parse (cold start) from the given token list.
        /// Returns diagnostics from parsing.
        /// </summary>
        public List<Diagnostic> Initialize(List<HLSLToken> tokens)
        {
            var decls = HLSLParser.ParseTopLevelDeclarations(
                tokens, _config, out var diagnostics, out _);

            _declarations = decls;
            RebuildDeclRanges();

            return diagnostics;
        }

        /// <summary>
        /// Apply an incremental token change to the AST.
        /// 
        /// <paramref name="tokens"/> is the FULL updated token list (after incremental lexing).
        /// <paramref name="firstDirtyToken"/> and <paramref name="newTokenCount"/> describe
        /// the range of tokens that were inserted/changed by the incremental lexer.
        /// <paramref name="oldTokenCount"/> is the number of tokens that were removed from
        /// the old token list at <paramref name="firstDirtyToken"/>.
        /// <paramref name="callback"/> receives notifications about AST changes.
        /// 
        /// Returns diagnostics from the re-parse.
        /// </summary>
        public List<Diagnostic> ApplyChange(
            List<HLSLToken> tokens,
            int firstDirtyToken,
            int newTokenCount,
            int oldTokenCount,
            IIncrementalParseCallback callback)
        {
            if (_declRanges.Count == 0)
            {
                // No existing declarations — do a full parse
                var diags = Initialize(tokens);
                if (callback != null)
                {
                    callback.OnDeclarationsInserted(0, _declarations);
                    callback.OnDiagnosticsUpdated(diags);
                }
                return diags;
            }

            // ─── Step 1: Find dirty declaration range ───
            // The old token range that was replaced is [firstDirtyToken, firstDirtyToken + oldTokenCount).
            // Find which declarations overlap this range.
            int oldDirtyEnd = firstDirtyToken + oldTokenCount; // Exclusive, in old token indices

            int firstDirtyDecl = -1;
            int lastDirtyDecl = -1;

            for (int i = 0; i < _declRanges.Count; i++)
            {
                var range = _declRanges[i];
                // Check if this declaration's token range overlaps the dirty region
                if (range.LastTokenIndex >= firstDirtyToken && range.FirstTokenIndex < oldDirtyEnd)
                {
                    if (firstDirtyDecl == -1)
                        firstDirtyDecl = i;
                    lastDirtyDecl = i;
                }
            }

            // If no declarations overlap, the edit was in whitespace/trivia between declarations.
            // We still need to update token ranges but don't need to re-parse.
            if (firstDirtyDecl == -1)
            {
                // Update token indices on declarations after the edit point
                int tokenDelta = newTokenCount - oldTokenCount;
                ShiftDeclRanges(firstDirtyToken, tokenDelta);
                return new List<Diagnostic>();
            }

            // ─── Step 2: Expand dirty range for safety ───
            // Include one declaration before and after the dirty range to handle
            // edits that merge/split declarations at boundaries.
            if (firstDirtyDecl > 0)
                firstDirtyDecl--;
            if (lastDirtyDecl < _declRanges.Count - 1)
                lastDirtyDecl++;

            // ─── Step 3: Determine the token sub-slice to re-parse ───
            int tokenDeltaForShift = newTokenCount - oldTokenCount;

            // First token index = first token of first dirty declaration (in NEW indices)
            int reparseStartToken = _declRanges[firstDirtyDecl].FirstTokenIndex;

            // Last token index = last token of last dirty declaration, adjusted for delta
            int reparseEndToken;
            if (lastDirtyDecl < _declRanges.Count - 1)
            {
                // The declaration AFTER the dirty range — its tokens are in old indices
                // so we apply the delta to get new indices.
                reparseEndToken = _declRanges[lastDirtyDecl].LastTokenIndex;
                if (_declRanges[lastDirtyDecl].LastTokenIndex >= firstDirtyToken)
                {
                    reparseEndToken += tokenDeltaForShift;
                }
            }
            else
            {
                // Last dirty decl is the final declaration — go up to EOF
                reparseEndToken = tokens.Count - 1; // EOF token
            }

            // Clamp to valid range
            reparseStartToken = Math.Max(0, reparseStartToken);
            reparseEndToken = Math.Min(reparseEndToken, tokens.Count - 1);

            // ─── Step 4: Build the token sub-slice with an EOF sentinel ───
            int sliceCount = reparseEndToken - reparseStartToken + 1;
            var subTokens = new List<HLSLToken>(sliceCount + 1);

            for (int i = reparseStartToken; i <= reparseEndToken; i++)
            {
                subTokens.Add(tokens[i]);
            }

            // Ensure the sub-slice ends with an EOF token for the parser
            if (subTokens.Count == 0 ||
                !EqualityComparer<TokenKind>.Default.Equals(subTokens[subTokens.Count - 1].Kind, TokenKind.EndOfFileToken))
            {
                var lastSpan = subTokens.Count > 0
                    ? subTokens[subTokens.Count - 1].Span
                    : new SourceSpan("", "", new SourceLocation(1, 1, 0), new SourceLocation(1, 1, 0));
                subTokens.Add(new HLSLToken(TokenKind.EndOfFileToken, null, lastSpan, subTokens.Count));
            }

            // ─── Step 5: Re-parse the sub-slice ───
            var newDecls = HLSLParser.ParseTopLevelDeclarations(
                subTokens, _config, out var diagnostics, out _);

            // ComputeParents is already called inside ParseTopLevelDeclarations

            // ─── Step 6: Splice new declarations into the cached AST ───
            int removeDeclCount = lastDirtyDecl - firstDirtyDecl + 1;

            // Notify callback about removals
            callback?.OnDeclarationsRemoved(firstDirtyDecl, removeDeclCount);

            // Remove old declarations and their ranges
            _declarations.RemoveRange(firstDirtyDecl, removeDeclCount);
            _declRanges.RemoveRange(firstDirtyDecl, removeDeclCount);

            // Insert new declarations and compute their ranges
            var newRanges = new List<DeclRange>(newDecls.Count);
            foreach (var decl in newDecls)
            {
                var declTokens = decl.Tokens;
                if (declTokens != null && declTokens.Count > 0)
                {
                    // Find actual token indices in the full token list
                    int firstIdx = FindTokenIndex(tokens, declTokens[0]);
                    int lastIdx = FindTokenIndex(tokens, declTokens[declTokens.Count - 1]);
                    newRanges.Add(new DeclRange(firstIdx, lastIdx, decl));
                }
                else
                {
                    // Fallback: use the reparse range
                    newRanges.Add(new DeclRange(reparseStartToken, reparseEndToken, decl));
                }
            }

            _declarations.InsertRange(firstDirtyDecl, newDecls);
            _declRanges.InsertRange(firstDirtyDecl, newRanges);

            // ─── Step 7: Update token ranges on subsequent declarations ───
            ShiftDeclRanges(firstDirtyDecl + newDecls.Count, tokenDeltaForShift, onlyBeyondToken: firstDirtyToken);

            // Notify callback about insertions
            callback?.OnDeclarationsInserted(firstDirtyDecl, newDecls);
            callback?.OnDiagnosticsUpdated(diagnostics);

            return diagnostics;
        }

        /// <summary>
        /// Rebuilds the declaration ranges from the current declaration list.
        /// Called after a full parse.
        /// </summary>
        private void RebuildDeclRanges()
        {
            _declRanges.Clear();

            foreach (var decl in _declarations)
            {
                var declTokens = decl.Tokens;
                if (declTokens != null && declTokens.Count > 0)
                {
                    // Token.Position gives us the index in the token stream
                    int first = declTokens[0].Position;
                    int last = declTokens[declTokens.Count - 1].Position;
                    _declRanges.Add(new DeclRange(first, last, decl));
                }
            }
        }

        /// <summary>
        /// Shifts declaration token ranges after a given declaration index
        /// by a token count delta.
        /// </summary>
        private void ShiftDeclRanges(int startDeclIndex, int tokenDelta, int onlyBeyondToken = -1)
        {
            if (tokenDelta == 0) return;

            for (int i = startDeclIndex; i < _declRanges.Count; i++)
            {
                var range = _declRanges[i];
                if (onlyBeyondToken >= 0 && range.FirstTokenIndex < onlyBeyondToken)
                    continue;

                _declRanges[i] = new DeclRange(
                    range.FirstTokenIndex + tokenDelta,
                    range.LastTokenIndex + tokenDelta,
                    range.Node);
            }
        }

        /// <summary>
        /// Shifts declaration token ranges starting from a token index
        /// (overload without declaration index restriction).
        /// </summary>
        private void ShiftDeclRanges(int beyondToken, int tokenDelta)
        {
            ShiftDeclRanges(0, tokenDelta, beyondToken);
        }

        /// <summary>
        /// Finds the index of a specific token object in the full token list.
        /// Uses reference equality for O(N) worst case but usually the token
        /// is near the start/end of a known range.
        /// </summary>
        private static int FindTokenIndex(List<HLSLToken> fullList, HLSLToken target)
        {
            // Fast path: use the Position field if it's valid
            int pos = target.Position;
            if (pos >= 0 && pos < fullList.Count && ReferenceEquals(fullList[pos], target))
                return pos;

            // Fallback: linear search from the Position hint
            int start = Math.Max(0, pos - 5);
            int end = Math.Min(fullList.Count, pos + 50);
            for (int i = start; i < end; i++)
            {
                if (ReferenceEquals(fullList[i], target))
                    return i;
            }

            // Full linear search as last resort
            for (int i = 0; i < fullList.Count; i++)
            {
                if (ReferenceEquals(fullList[i], target))
                    return i;
            }

            return -1;
        }
    }
}
