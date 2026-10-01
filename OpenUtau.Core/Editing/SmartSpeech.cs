using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// Creates a simple speech-like score from plain text. This is deliberately offline:
    /// Chinese/Japanese characters become individual notes, Latin words stay together,
    /// and punctuation becomes a short rest. The resulting notes can then be processed by
    /// the normal phonemizer and automatic vocal tuning tools.
    /// </summary>
    public static class SmartSpeechGenerator {
        public sealed class Options {
            public int BaseTone { get; set; } = 45;
            public double BeatsPerToken { get; set; } = 0.3;
            public bool ReplaceSelection { get; set; } = true;
            public bool FitSelection { get; set; } = true;
        }

        enum BoundaryKind {
            None,
            Soft,
            Falling,
            Rising,
        }

        readonly struct SpeechToken {
            public readonly string Lyric;
            public readonly int Weight;
            public readonly bool IsRest;
            public readonly BoundaryKind BoundaryAfter;
            public readonly int MandarinTone;
            public SpeechToken(string lyric, int weight, bool isRest = false,
                    BoundaryKind boundaryAfter = BoundaryKind.None, int mandarinTone = 0) {
                Lyric = lyric;
                Weight = weight;
                IsRest = isRest;
                BoundaryAfter = boundaryAfter;
                MandarinTone = mandarinTone;
            }
            public SpeechToken WithBoundary(BoundaryKind boundary) {
                return new SpeechToken(Lyric, Weight, IsRest,
                    BoundaryAfter == BoundaryKind.Rising || boundary == BoundaryKind.Rising
                        ? BoundaryKind.Rising
                        : BoundaryAfter == BoundaryKind.Falling || boundary == BoundaryKind.Falling
                            ? BoundaryKind.Falling
                            : boundary == BoundaryKind.Soft || BoundaryAfter == BoundaryKind.Soft
                                ? BoundaryKind.Soft
                                : BoundaryKind.None, MandarinTone);
            }
            public SpeechToken WithWeight(int weight) {
                return new SpeechToken(Lyric, weight, IsRest, BoundaryAfter, MandarinTone);
            }
        }

        /// <summary>Apply generated notes as one undoable operation.</summary>
        public static IReadOnlyList<UNote> Apply(
                UProject project, UVoicePart part, IReadOnlyCollection<UNote> selectedNotes,
                string text, Options? options, DocManager docManager) {
            options ??= new Options();
            var tokens = Tokenize(text);
            if (tokens.Count == 0) {
                return Array.Empty<UNote>();
            }

            var selected = selectedNotes
                .Where(part.notes.Contains)
                .OrderBy(note => note.position)
                .ToList();
            var start = selected.Count > 0 && options.ReplaceSelection
                ? selected[0].position
                : selected.Count > 0
                    ? selected[^1].End
                : Math.Max(0, docManager.playPosTick - part.position);
            var selectedEnd = selected.Count > 0 ? selected[^1].End : start;
            var ticksPerBeat = Math.Max(1, project.resolution);
            var baseTicks = Math.Max(30, (int)Math.Round(
                ticksPerBeat * Math.Clamp(options.BeatsPerToken, 0.125, 8),
                MidpointRounding.AwayFromZero));
            var totalWeight = tokens.Sum(token => token.Weight);
            var available = selected.Count > 0 && options.FitSelection
                ? selectedEnd - start
                : 0;
            if (available > 0) {
                baseTicks = Math.Max(30, available / Math.Max(1, totalWeight));
            }

            // Keep generated notes out of a neighbouring note when inserting at the playhead.
            if (selected.Count == 0 || !options.ReplaceSelection) {
                var conflict = part.notes
                    .Where(note => note.position < start + baseTicks * totalWeight && note.End > start)
                    .OrderBy(note => note.End)
                    .LastOrDefault();
                if (conflict != null) {
                    start = conflict.End;
                }
            }

            var generated = new List<UNote>(tokens.Count);
            var cursor = start;
            var toneIndex = 0;
            var phrasePosition = 0;
            var phraseLength = tokens.Count(token => !token.IsRest);
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++) {
                var token = tokens[tokenIndex];
                var duration = Math.Max(30, baseTicks * token.Weight);
                if (available > 0) {
                    duration = Math.Max(30, (int)Math.Round(
                        (double)available * token.Weight / totalWeight,
                        MidpointRounding.AwayFromZero));
                }
                var toneOffset = token.IsRest
                    ? 0
                    : SpeechContour(toneIndex, phrasePosition, phraseLength,
                        token.BoundaryAfter, token.MandarinTone);
                var note = project.CreateNote(
                    Math.Clamp(options.BaseTone + toneOffset, 1, 127),
                    cursor, duration);
                note.lyric = token.Lyric;
                AddMandarinContour(project, part, note, token.MandarinTone);
                generated.Add(note);
                cursor += duration;
                if (!token.IsRest) {
                    toneIndex++;
                    phrasePosition++;
                }
                if (token.BoundaryAfter != BoundaryKind.None) {
                    phrasePosition = 0;
                    phraseLength = tokens
                        .Skip(tokenIndex + 1)
                        .TakeWhile(next => !next.IsRest)
                        .TakeWhile(next => next.BoundaryAfter == BoundaryKind.None)
                        .Count();
                }
            }

            docManager.StartUndoGroup("command.smart.speech", true);
            try {
                if (options.ReplaceSelection && selected.Count > 0) {
                    docManager.ExecuteCmd(new RemoveNoteCommand(part, selected));
                }
                docManager.ExecuteCmd(new AddNoteCommand(part, generated));
            } finally {
                docManager.EndUndoGroup();
            }
            return generated;
        }

        static int SpeechContour(int index, int phrasePosition, int phraseLength,
                BoundaryKind boundary, int mandarinTone) {
            // Keep phrases centered around the requested base note, with a gentle rise in
            // the middle and a clear fall at a sentence boundary. The variation is
            // deterministic so reopening a project produces the same score.
            var arc = phraseLength > 1
                ? Math.Sin(Math.PI * phrasePosition / (phraseLength - 1))
                : 0;
            var offset = (int)Math.Round(arc * 1.2, MidpointRounding.AwayFromZero);
            offset += (index % 4) switch {
                1 => 1,
                2 => 0,
                3 => -1,
                _ => 0,
            };
            // Preserve the main Mandarin tone contrast in the generated note sequence.
            // The singer's renderer supplies the continuous movement inside each syllable.
            offset += mandarinTone switch {
                1 => 2,
                2 => 1,
                3 => -1,
                4 => -2,
                _ => 0,
            };
            return boundary switch {
                BoundaryKind.Falling => -2,
                BoundaryKind.Rising => 2,
                BoundaryKind.Soft => -1,
                _ => offset,
            };
        }

        static void AddMandarinContour(UProject project, UVoicePart part, UNote note, int tone) {
            if (tone is < 1 or > 4) {
                return;
            }
            var startMs = project.timeAxis.TickPosToMsPos(part.position + note.position);
            var endMs = project.timeAxis.TickPosToMsPos(part.position + note.End);
            var durationMs = endMs - startMs;
            if (durationMs <= 1) {
                return;
            }

            // PitchPoint.Y is measured in tenths of a semitone. Keep the movement subtle:
            // the singer and automatic tuning still control the final voice character.
            var points = tone switch {
                1 => new (double Position, float Pitch)[] {
                    (0.00, 6), (0.55, 6), (1.00, 5),
                },
                2 => new (double Position, float Pitch)[] {
                    (0.00, -5), (0.55, 1), (1.00, 7),
                },
                3 => new (double Position, float Pitch)[] {
                    (0.00, 3), (0.45, -8), (0.72, -4), (1.00, 3),
                },
                _ => new (double Position, float Pitch)[] {
                    (0.00, 7), (0.45, 1), (1.00, -7),
                },
            };
            note.pitch.data.Clear();
            note.pitch.snapFirst = true;
            foreach (var point in points) {
                note.pitch.AddPoint(new PitchPoint(
                    (float)(durationMs * point.Position), point.Pitch, PitchPointShape.io));
            }
        }

        static List<SpeechToken> Tokenize(string text) {
            var result = new List<SpeechToken>();
            var word = new StringBuilder();
            var cjkIndex = 0;
            void FlushWord() {
                if (word.Length > 0) {
                    var value = word.ToString();
                    var weight = EstimateWordWeight(value);
                    if (result.Count > 0 && result[^1].IsRest) {
                        // Give the first word after a pause a little room to land.
                        weight = Math.Max(weight, 2);
                    }
                    result.Add(new SpeechToken(value, weight));
                    word.Clear();
                }
            }
            var normalized = text.Normalize(NormalizationForm.FormC);
            for (var textIndex = 0; textIndex < normalized.Length; textIndex++) {
                var c = normalized[textIndex];
                if (c is '\r' or '\n') {
                    FlushWord();
                    MarkLastPhrase(BoundaryKind.Falling);
                    AddPause(result, 2, BoundaryKind.Falling);
                    continue;
                }
                if (char.IsWhiteSpace(c)) {
                    FlushWord();
                    var spaces = 1;
                    while (textIndex + 1 < normalized.Length
                            && char.IsWhiteSpace(normalized[textIndex + 1])
                            && normalized[textIndex + 1] is not '\r' and not '\n') {
                        spaces++;
                        textIndex++;
                    }
                    StretchLastToken(spaces);
                    continue;
                }
                if (IsCjkOrKana(c)) {
                    FlushWord();
                    var mandarinTone = GetMandarinTone(c);
                    var weight = EstimateCjkWeight(c, cjkIndex++, mandarinTone);
                    if (result.Count > 0 && result[^1].IsRest) {
                        weight = Math.Max(weight, 2);
                    }
                    result.Add(new SpeechToken(c.ToString(), weight,
                        mandarinTone: mandarinTone));
                    continue;
                }
                if (char.IsPunctuation(c) || char.IsSymbol(c)) {
                    // Apostrophes and hyphens are part of a Latin word; sentence marks pause longer.
                    if ((c == '\'' || c == '-' || c == '_') && word.Length > 0) {
                        word.Append(c);
                        continue;
                    }
                    FlushWord();
                    if (IsPause(c)) {
                        var boundary = GetBoundary(c);
                        MarkLastPhrase(boundary);
                        AddPause(result, IsLongPause(c) ? 2 : 1, boundary);
                    }
                    continue;
                }
                word.Append(c);
            }
            FlushWord();
            MarkLastPhrase(BoundaryKind.Falling);
            return result;

            void MarkLastPhrase(BoundaryKind boundary) {
                for (var i = result.Count - 1; i >= 0; i--) {
                    if (!result[i].IsRest) {
                        var token = result[i].WithBoundary(boundary);
                        if (boundary == BoundaryKind.Falling) {
                            token = token.WithWeight(Math.Max(token.Weight, 2));
                        }
                        result[i] = token;
                        return;
                    }
                }
            }

            void StretchLastToken(int spaces) {
                if (spaces <= 0) {
                    return;
                }
                // A space is an explicit sustain marker. Four additional spaces add
                // another base slot, with a cap to keep accidental pasted whitespace usable.
                var extraWeight = Math.Clamp((spaces + 3) / 4, 1, 8);
                for (var i = result.Count - 1; i >= 0; i--) {
                    if (!result[i].IsRest) {
                        result[i] = result[i].WithWeight(result[i].Weight + extraWeight);
                        return;
                    }
                }
            }
        }

        static void AddPause(List<SpeechToken> tokens, int weight, BoundaryKind boundary) {
            if (tokens.Count > 0 && tokens[^1].IsRest) {
                tokens[^1] = tokens[^1].WithWeight(Math.Max(tokens[^1].Weight, weight)).WithBoundary(boundary);
            } else {
                tokens.Add(new SpeechToken("R", weight, true, boundary));
            }
        }

        static int EstimateWordWeight(string word) {
            var syllables = 0;
            var previousVowel = false;
            foreach (var c in word) {
                var lower = char.ToLowerInvariant(c);
                var vowel = lower is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
                if (vowel && !previousVowel) {
                    syllables++;
                }
                previousVowel = vowel;
            }
            if (syllables > 1 && word.EndsWith('e')) {
                syllables--;
            }
            return Math.Clamp(syllables, 1, 3);
        }

        static int EstimateCjkWeight(char c, int index, int mandarinTone) {
            // Function words are naturally shorter; every fourth content syllable gets a
            // slightly longer slot to avoid an artificial metronomic stream.
            const string light = "的了着过吗呢啊呀吧啦和与在是我你他她它这那不也都就还而";
            if (light.Contains(c)) {
                return 1;
            }
            var weight = index % 4 == 2 ? 2 : 1;
            return mandarinTone == 3 ? Math.Min(3, weight + 1) : weight;
        }

        static int GetMandarinTone(char c) {
            var lyric = c.ToString();
            if (!Pinyin.Pinyin.Instance.IsHanzi(lyric)) {
                return 0;
            }
            var pinyin = Pinyin.Pinyin.Instance
                .GetDefaultPinyin(lyric, Pinyin.ManTone.Style.TONE3, false, false)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(pinyin) || !char.IsDigit(pinyin[^1])) {
                return 0;
            }
            var tone = pinyin[^1] - '0';
            return tone is >= 1 and <= 4 ? tone : 0;
        }

        static bool IsPause(char c) => c is ',' or '，' or '、' or ';' or '；' or ':' or '：'
            or '.' or '。' or '!' or '！' or '?' or '？' or '…' or '\n';

        static bool IsLongPause(char c) => c is '.' or '。' or '!' or '！' or '?' or '？' or '…';

        static BoundaryKind GetBoundary(char c) => c is '?' or '？'
            ? BoundaryKind.Rising
            : IsLongPause(c) ? BoundaryKind.Falling : BoundaryKind.Soft;

        static bool IsCjkOrKana(char c) {
            return c is >= '\u3040' and <= '\u30ff' // Hiragana/Katakana
                or >= '\u3400' and <= '\u4dbf'     // CJK extension A
                or >= '\u4e00' and <= '\u9fff'     // CJK unified
                or >= '\uf900' and <= '\ufaff';    // CJK compatibility
        }
    }
}
