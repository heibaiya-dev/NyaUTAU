using System;
using System.Collections.Generic;
using System.Linq;
using FormatUstx = OpenUtau.Core.Format.Ustx;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Editing {
    public enum VocalTuningPreset { Natural, Gentle, Powerful }

    public enum VocalTuningChannel { Pitch, Vibrato, Dynamics, Breathiness, Articulation }

    public enum VocalTuningSkipReason { UnsupportedExpression, NoSustainedNotes }

    public sealed class AutoVocalTuningOptions {
        public VocalTuningPreset Preset { get; set; } = VocalTuningPreset.Natural;
        public double Strength { get; set; } = 0.65;
        public bool Pitch { get; set; } = true;
        public bool Vibrato { get; set; } = true;
        public bool Dynamics { get; set; } = true;
        public bool Breathiness { get; set; } = true;
        public bool Articulation { get; set; } = true;
    }

    public sealed class AutoVocalTuningSkippedChannel {
        public VocalTuningChannel Channel { get; }
        public VocalTuningSkipReason Reason { get; }
        public AutoVocalTuningSkippedChannel(VocalTuningChannel channel, VocalTuningSkipReason reason) {
            Channel = channel;
            Reason = reason;
        }
    }

    public sealed class AutoVocalTuningResult {
        public int EligibleNoteCount { get; internal set; }
        public int AppliedNoteCount { get; internal set; }
        public int PitchNoteCount { get; internal set; }
        public int VibratoNoteCount { get; internal set; }
        public int DynamicsNoteCount { get; internal set; }
        public int BreathinessNoteCount { get; internal set; }
        public int ArticulationNoteCount { get; internal set; }
        public IReadOnlyList<AutoVocalTuningSkippedChannel> SkippedChannels { get; internal set; }
            = Array.Empty<AutoVocalTuningSkippedChannel>();
    }

    public sealed class AutoVocalTuningCapabilities {
        public bool Dynamics { get; internal set; }
        public bool Breathiness { get; internal set; }
        public bool Articulation { get; internal set; }
        public string? DynamicsExpression { get; internal set; }
        public string? BreathinessExpression { get; internal set; }
        public string? ArticulationExpression { get; internal set; }
        public string? DynamicsReason { get; internal set; }
        public string? BreathinessReason { get; internal set; }
        public string? ArticulationReason { get; internal set; }
    }

    /// <summary>Applies deterministic, renderer-aware tuning gestures to a voice part.</summary>
    public sealed class AutoVocalTuning : BatchEdit {
        public const string MenuName = "pianoroll.menu.notes.autovocaltuning";
        public string Name => MenuName;
        public AutoVocalTuningResult LastResult { get; private set; } = new AutoVocalTuningResult();
        readonly AutoVocalTuningOptions options;

        public AutoVocalTuning(AutoVocalTuningOptions? options = null) {
            this.options = options ?? new AutoVocalTuningOptions();
        }

        public static AutoVocalTuningCapabilities GetCapabilities(UProject project, UVoicePart part) {
            var result = new AutoVocalTuningCapabilities();
            if (part.trackNo < 0 || part.trackNo >= project.tracks.Count) {
                result.DynamicsReason = result.BreathinessReason = result.ArticulationReason = "Invalid track";
                return result;
            }
            var track = project.tracks[part.trackNo];
            var renderer = track.RendererSettings?.Renderer;
            if (renderer == null) {
                result.DynamicsReason = result.BreathinessReason = result.ArticulationReason = "No active renderer";
                return result;
            }
            if (track.Singer is DiffSinger.DiffSingerSinger dsSinger) {
                // DiffSinger advertises dyn/brec for compatibility, but these only reach
                // the model through the variance predictor's explicit inputs.
                if (dsSinger.HasVariancePredictor && dsSinger.dsConfig.predict_energy
                        && dsSinger.dsConfig.useEnergyEmbed
                        && TrySupported(project, track, renderer, DiffSinger.DiffSingerUtils.ENE, out _)) {
                    result.Dynamics = true;
                    result.DynamicsExpression = DiffSinger.DiffSingerUtils.ENE;
                } else {
                    result.DynamicsReason = "DiffSinger energy variance input is unavailable";
                }
            } else if (TrySupported(project, track, renderer, FormatUstx.DYN, out _)) {
                result.Dynamics = true;
                result.DynamicsExpression = FormatUstx.DYN;
            } else if (TrySupported(project, track, renderer, FormatUstx.VOL, out _)) {
                result.Dynamics = true;
                result.DynamicsExpression = FormatUstx.VOL;
            } else {
                result.DynamicsReason = "Renderer does not support dynamics or volume";
            }
            if (track.Singer is DiffSinger.DiffSingerSinger dsBreathiness) {
                // DiffSinger consumes breathiness through its variance predictor and
                // explicit BREC input. A generic renderer descriptor for BRE must not
                // make this channel appear available when the model cannot consume it.
                if (BreathinessModelAvailable(track)
                        && dsBreathiness.dsConfig.useBreathinessEmbed
                        && TrySupported(project, track, renderer, FormatUstx.BREC, out _)) {
                    result.Breathiness = true;
                    result.BreathinessExpression = FormatUstx.BREC;
                } else {
                    result.BreathinessReason = "DiffSinger breathiness variance input is unavailable";
                }
            } else if (TrySupported(project, track, renderer, FormatUstx.BREC, out _)) {
                result.Breathiness = true;
                result.BreathinessExpression = FormatUstx.BREC;
            } else if (TrySupported(project, track, renderer, FormatUstx.BRE, out _)) {
                result.Breathiness = true;
                result.BreathinessExpression = FormatUstx.BRE;
            } else {
                result.BreathinessReason = "Renderer does not support breathiness";
            }
            if (track.Singer is DiffSinger.DiffSingerSinger dsArticulation) {
                // The DiffSinger acoustic model only receives articulation through
                // its velocity curve when speed embedding is enabled.
                if (dsArticulation.dsConfig.useSpeedEmbed
                        && TrySupported(project, track, renderer, DiffSinger.DiffSingerUtils.VELC, out _)) {
                    result.Articulation = true;
                    result.ArticulationExpression = DiffSinger.DiffSingerUtils.VELC;
                } else {
                    result.ArticulationReason = "DiffSinger speed embedding is unavailable";
                }
            } else if (TrySupported(project, track, renderer, FormatUstx.VEL, out _)) {
                result.Articulation = true;
                result.ArticulationExpression = FormatUstx.VEL;
            } else {
                result.ArticulationReason = "Renderer does not support velocity";
            }
            return result;
        }

        public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes,
                DocManager docManager) {
            var strength = (float)Math.Clamp(options.Strength, 0, 1);
            var preset = PresetValuesFor(options.Preset);
            var selected = selectedNotes.Count == 0 ? null : new HashSet<UNote>(selectedNotes);
            var notes = part.notes.Where(note => selected == null || selected.Contains(note))
                .Where(SmartPitch.IsPitched).OrderBy(note => note.position).ToList();
            var result = new AutoVocalTuningResult { EligibleNoteCount = notes.Count };
            var originalFirstPoints = part.notes
                .Where(note => note.pitch.data.Count > 0)
                .ToDictionary(note => note, note => (pitch: note.pitch, y: note.pitch.data[0].Y));
            var skipped = new List<AutoVocalTuningSkippedChannel>();
            var commands = new List<UCommand>();
            var track = part.trackNo >= 0 && part.trackNo < project.tracks.Count
                ? project.tracks[part.trackNo] : null;
            var capabilities = GetCapabilities(project, part);
            var phraseInfo = BuildPhraseInfo(part);

            if (options.Pitch && strength > 0) {
                var pitchCommands = SmartPitch.BuildHumanizedCommands(project, part,
                    notes, strength * preset.Pitch);
                commands.AddRange(pitchCommands);
                result.PitchNoteCount = pitchCommands.Count;
            }
            if (options.Vibrato && strength > 0) {
                foreach (var note in notes) {
                    var durationMs = NoteDurationMs(project, part, note);
                    if (durationMs < 180) {
                        continue;
                    }
                    commands.Add(new SetVibratoCommand(part, note,
                        MakeVibrato(durationMs, strength, preset.Vibrato, phraseInfo.TryGetValue(note, out var info) ? info.Progress : .5f)));
                    result.VibratoNoteCount++;
                }
                if (result.VibratoNoteCount == 0 && notes.Count > 0) {
                    skipped.Add(new AutoVocalTuningSkippedChannel(
                        VocalTuningChannel.Vibrato, VocalTuningSkipReason.NoSustainedNotes));
                }
            }
            if (track != null && options.Dynamics && strength > 0) {
                if (capabilities.DynamicsExpression == FormatUstx.DYN
                        || capabilities.DynamicsExpression == DiffSinger.DiffSingerUtils.ENE) {
                    var command = BuildCurveCommand(project, part, track, capabilities.DynamicsExpression!, notes,
                        strength, preset.Dynamics, false);
                    if (command != null) commands.Add(command);
                    result.DynamicsNoteCount = notes.Count;
                } else if (capabilities.DynamicsExpression == FormatUstx.VOL) {
                    foreach (var note in notes) {
                        commands.Add(new SetNoteExpressionSnapshotCommand(project, track, part, note,
                            FormatUstx.VOL, new float?[] { 100 + 15 * strength * preset.Dynamics }));
                    }
                    result.DynamicsNoteCount = notes.Count;
                } else if (notes.Count > 0) {
                    skipped.Add(new AutoVocalTuningSkippedChannel(
                        VocalTuningChannel.Dynamics, VocalTuningSkipReason.UnsupportedExpression));
                }
            } else if (options.Dynamics && notes.Count > 0) {
                skipped.Add(new AutoVocalTuningSkippedChannel(
                    VocalTuningChannel.Dynamics, VocalTuningSkipReason.UnsupportedExpression));
            }
            if (track != null && options.Breathiness && strength > 0) {
                if (capabilities.BreathinessExpression == FormatUstx.BREC) {
                    var command = BuildCurveCommand(project, part, track, FormatUstx.BREC, notes,
                        strength, preset.Breathiness, true);
                    if (command != null) commands.Add(command);
                    result.BreathinessNoteCount = notes.Count;
                } else if (capabilities.BreathinessExpression == FormatUstx.BRE) {
                    foreach (var note in notes) {
                        commands.Add(new SetNoteExpressionSnapshotCommand(project, track, part, note,
                            FormatUstx.BRE, new float?[] { 10 + 14 * strength * preset.Breathiness }));
                    }
                    result.BreathinessNoteCount = notes.Count;
                } else if (notes.Count > 0) {
                    skipped.Add(new AutoVocalTuningSkippedChannel(
                        VocalTuningChannel.Breathiness, VocalTuningSkipReason.UnsupportedExpression));
                }
            } else if (options.Breathiness && notes.Count > 0) {
                skipped.Add(new AutoVocalTuningSkippedChannel(
                    VocalTuningChannel.Breathiness, VocalTuningSkipReason.UnsupportedExpression));
            }
            if (track != null && options.Articulation && strength > 0
                    && capabilities.ArticulationExpression == DiffSinger.DiffSingerUtils.VELC) {
                var command = BuildCurveCommand(project, part, track, DiffSinger.DiffSingerUtils.VELC,
                    notes, strength, preset.Articulation, false, true);
                if (command != null) commands.Add(command);
                result.ArticulationNoteCount = notes.Count;
            } else if (track != null && options.Articulation && strength > 0 && capabilities.Articulation) {
                foreach (var note in notes) {
                    var amount = note.duration <= 240 ? 16 : 8;
                    commands.Add(new SetNoteExpressionSnapshotCommand(project, track, part, note,
                        FormatUstx.VEL, new float?[] { 100 - amount * strength * preset.Articulation }));
                }
                result.ArticulationNoteCount = notes.Count;
            } else if (options.Articulation && notes.Count > 0 && strength > 0) {
                skipped.Add(new AutoVocalTuningSkippedChannel(
                    VocalTuningChannel.Articulation, VocalTuningSkipReason.UnsupportedExpression));
            }

            result.AppliedNoteCount = new[] {
                result.PitchNoteCount, result.VibratoNoteCount, result.DynamicsNoteCount,
                result.BreathinessNoteCount, result.ArticulationNoteCount
            }.Max();
            result.SkippedChannels = skipped;
            LastResult = result;
            if (commands.Count == 0 || docManager == null) {
                return;
            }
            docManager.StartUndoGroup(Name, true);
            try {
                foreach (var command in commands) {
                    docManager.ExecuteCmd(command);
                }
                // Validation derives snapFirst's first point. Preserve the exact
                // pitch snapshots of unselected notes while this group commits.
                foreach (var partNote in part.notes) {
                    partNote.SkipSnapFirstValidationCount = 2;
                }
            } finally {
                docManager.EndUndoGroup();
                // Validation may derive snapFirst's first point for notes whose
                // pitch object was untouched. Restore that user-authored point.
                foreach (var pair in originalFirstPoints) {
                    if (ReferenceEquals(pair.Key.pitch, pair.Value.pitch)
                            && pair.Key.pitch.data.Count > 0) {
                        pair.Key.pitch.data[0].Y = pair.Value.y;
                    }
                }
            }
        }

        readonly struct PresetValues {
            public readonly float Pitch, Vibrato, Dynamics, Breathiness, Articulation;
            public PresetValues(float pitch, float vibrato, float dynamics, float breathiness, float articulation) {
                Pitch = pitch; Vibrato = vibrato; Dynamics = dynamics;
                Breathiness = breathiness; Articulation = articulation;
            }
        }

        static PresetValues PresetValuesFor(VocalTuningPreset preset) => preset switch {
            VocalTuningPreset.Gentle => new PresetValues(.65f, .7f, .65f, .6f, .65f),
            VocalTuningPreset.Powerful => new PresetValues(1.25f, 1.3f, 1.35f, 1.3f, 1.3f),
            _ => new PresetValues(1f, 1f, 1f, 1f, 1f),
        };

        static float NoteDurationMs(UProject project, UVoicePart part, UNote note) {
            var start = project.timeAxis.TickPosToMsPos((double)part.position + note.position);
            var end = project.timeAxis.TickPosToMsPos((double)part.position + note.End);
            return (float)Math.Max(0, end - start);
        }

        static UVibrato MakeVibrato(float durationMs, float strength, float amount, float phraseProgress) {
            // Vibrato arrives later on a sustained vowel and opens up towards the
            // end of a phrase. A little phrase drift keeps every note from sounding
            // like the same pasted preset.
            phraseProgress = Math.Clamp(phraseProgress, 0, 1);
            var length = Math.Clamp(34 + durationMs / 44 + phraseProgress * 12, 34, 72);
            var period = Math.Clamp(durationMs / 4.0f, 125, 240);
            var depth = (6 + 19 * strength * amount)
                * (0.78f + 0.38f * phraseProgress);
            return new UVibrato {
                length = length,
                period = period,
                depth = Math.Clamp(depth, 5, 70),
                @in = Math.Clamp(24 - phraseProgress * 7, 12, 30),
                @out = Math.Clamp(18 + phraseProgress * 8, 16, 30),
                shift = 0,
                drift = (phraseProgress - .5f) * 2.2f * strength,
                volLink = Math.Clamp(8 + 18 * strength * amount, 0, 100),
            };
        }

        readonly struct PhraseNoteInfo {
            public readonly int Index;
            public readonly int Count;
            public readonly float Progress;
            public PhraseNoteInfo(int index, int count) {
                Index = index;
                Count = count;
                Progress = count <= 1 ? .5f : (float)index / (count - 1);
            }
        }

        static Dictionary<UNote, PhraseNoteInfo> BuildPhraseInfo(UVoicePart part) {
            var result = new Dictionary<UNote, PhraseNoteInfo>();
            var phrase = new List<UNote>();
            UNote? previous = null;
            void Flush() {
                for (int i = 0; i < phrase.Count; i++) {
                    result[phrase[i]] = new PhraseNoteInfo(i, phrase.Count);
                }
                phrase.Clear();
            }
            foreach (var note in part.notes.OrderBy(note => note.position)) {
                if (!SmartPitch.IsPitched(note)) {
                    Flush();
                    previous = null;
                    continue;
                }
                if (previous != null && previous.End != note.position) {
                    Flush();
                }
                phrase.Add(note);
                previous = note;
            }
            Flush();
            return result;
        }

        static bool BreathinessModelAvailable(UTrack track) {
            if (track.Singer is DiffSinger.DiffSingerSinger singer) {
                return singer.HasVariancePredictor && singer.dsConfig.predict_breathiness;
            }
            return true;
        }

        static bool TrySupported(UProject project, UTrack track, IRenderer renderer,
                string abbr, out UExpressionDescriptor descriptor) {
            if (TryGetDescriptor(project, track, renderer, abbr, out descriptor)) {
                return renderer.SupportsExpression(descriptor);
            }
            descriptor = null!;
            return false;
        }

        static bool TryGetDescriptor(UProject project, UTrack track, IRenderer renderer,
                string abbr, out UExpressionDescriptor descriptor) {
            if (track.TryGetExpDescriptor(project, abbr, out descriptor)) {
                return true;
            }
            if (track.Singer == null) {
                descriptor = null!;
                return false;
            }
            descriptor = renderer.GetSuggestedExpressions(track.Singer, track.RendererSettings)
                .FirstOrDefault(exp => exp.abbr == abbr)!;
            return descriptor != null;
        }

        static SetCurveSnapshotCommand? BuildCurveCommand(UProject project, UVoicePart part,
                UTrack track, string abbr, IReadOnlyList<UNote> notes, float strength,
                float amount, bool breathiness, bool velocity = false) {
            var renderer = track.RendererSettings?.Renderer;
            if (renderer == null || !TryGetDescriptor(project, track, renderer, abbr, out var descriptor)) {
                return null;
            }
            var curve = part.curves.FirstOrDefault(c => c.abbr == abbr);
            var xs = curve?.xs.ToArray() ?? Array.Empty<int>();
            var ys = curve?.ys.ToArray() ?? Array.Empty<int>();
            var phraseInfo = BuildPhraseInfo(part);
            foreach (var note in notes) {
                int start = note.position;
                int end = note.End;
                int span = Math.Max(1, end - start);
                int centre = start + span / 2;
                var progress = phraseInfo.TryGetValue(note, out var info) ? info.Progress : .5f;
                var phraseArc = (float)Math.Sin(Math.PI * progress);
                int peak;
                (int x, int y)[] points;
                if (velocity) {
                    // VELC is logarithmic in DiffSinger: values above 100 make the
                    // consonant move faster. Put the emphasis on each phoneme onset,
                    // then settle to the neutral value before the vowel body.
                    var attack = (int)Math.Round(100 + (span <= 240 ? 24 : 12)
                        * strength * amount * (.85f + .3f * (1 - progress)));
                    peak = (int)Math.Round(100 + 4 * strength * amount * phraseArc);
                    points = new[] {
                        (start, attack), (start + Math.Max(UCurve.interval, span / 8), peak),
                        (centre, peak), (Math.Max(start, end - UCurve.interval), 100)
                    };
                } else {
                    var durationWeight = Math.Clamp(span / 720f, .55f, 1.35f);
                    if (breathiness) {
                        // Breath accumulates on sustained vowels and at phrase ends.
                        peak = (int)Math.Round((5 + 18 * phraseArc)
                            * strength * amount * durationWeight
                            * (.8f + .35f * progress));
                    } else {
                        // Dynamics follow a phrase envelope instead of resetting to
                        // zero at every note; this gives DiffSinger a connected line.
                        peak = (int)Math.Round((4 + 20 * (.35f + .65f * phraseArc))
                            * strength * amount * durationWeight);
                    }
                    var onset = breathiness ? 0 : (int)Math.Round(-4 * strength * amount);
                    var release = (int)Math.Round(peak * (breathiness
                        ? .22f + .28f * progress : .42f));
                    points = new[] {
                        (start, onset),
                        (start + Math.Max(UCurve.interval, span / 7), (int)Math.Round(peak * .68f)),
                        (centre, peak),
                        (Math.Max(start, end - UCurve.interval), release)
                    };
                }
                (xs, ys) = UCurve.ReplaceRange(xs, ys, start, end, points, descriptor);
                if (velocity) {
                    AddConsonantPoints(part, note, points[0].y, points[2].y, span,
                        ref xs, ref ys, descriptor);
                }
            }
            return new SetCurveSnapshotCommand(project, part, abbr, descriptor, xs, ys);
        }

        static void AddConsonantPoints(UVoicePart part, UNote note, int attack, int body,
                int span, ref int[] xs, ref int[] ys, UExpressionDescriptor descriptor) {
            var phonemes = part.phonemes.Where(phoneme => phoneme.Parent == note)
                .OrderBy(phoneme => phoneme.position).ToList();
            if (phonemes.Count == 0) {
                return;
            }
            foreach (var phoneme in phonemes) {
                if (IsVowelLike(phoneme.phoneme)) {
                    continue;
                }
                int x = Math.Clamp(phoneme.position, note.position, note.End);
                int fall = Math.Min(note.End, x + Math.Max(UCurve.interval, span / 14));
                var points = new[] { (x, attack), (fall, body) };
                (xs, ys) = UCurve.ReplaceRange(xs, ys, x, fall, points, descriptor);
            }
        }

        static bool IsVowelLike(string? phoneme) {
            if (string.IsNullOrWhiteSpace(phoneme)) {
                return true;
            }
            var value = phoneme.Trim().ToLowerInvariant();
            // Most UTAU/DiffSinger phonemizers use latin vowel symbols. Treat kana
            // and syllabic markers as vowel-bearing so they do not get a consonant
            // burst when the phoneme inventory is not latin.
            if (value.Any(ch => "aeiouəɪʊɑɔɛ".Contains(ch))) {
                return true;
            }
            return value.Any(ch => ch >= '\u3040' && ch <= '\u30ff');
        }
    }

    sealed class SetNoteExpressionSnapshotCommand : ExpCommand {
        readonly UProject project;
        readonly UTrack track;
        readonly string abbr;
        readonly float?[] values;
        readonly List<UExpression> oldExpressions;
        public SetNoteExpressionSnapshotCommand(UProject project, UTrack track, UVoicePart part,
                UNote note, string abbr, float?[] values) : base(part) {
            this.project = project; this.track = track; this.abbr = abbr; this.values = values;
            Note = note; Key = abbr; oldExpressions = note.phonemeExpressions;
        }
        public override string ToString() => $"Set note expression {abbr}";
        public override ValidateOptions ValidateOptions => new ValidateOptions {
            SkipTiming = true, Part = Part, SkipPhonemizer = abbr != FormatUstx.VEL,
        };
        public override void Execute() {
            Note.phonemeExpressions = oldExpressions.Select(exp => exp.Clone()).ToList();
            Note.SetExpression(project, track, abbr, values);
        }
        public override void Unexecute() => Note.phonemeExpressions = oldExpressions;
    }

    sealed class SetCurveSnapshotCommand : ExpCommand {
        readonly UProject project;
        readonly string abbr;
        readonly int[] newXs, newYs;
        readonly UCurve? oldCurve;
        readonly List<int>? oldXsReference, oldYsReference, oldRealXsReference, oldRealYsReference;
        readonly int[]? oldXs, oldYs, oldRealXs, oldRealYs;
        readonly UExpressionDescriptor descriptor;
        public SetCurveSnapshotCommand(UProject project, UVoicePart part, string abbr,
                UExpressionDescriptor descriptor, int[] newXs, int[] newYs) : base(part) {
            this.project = project; this.abbr = abbr; Key = abbr;
            this.newXs = newXs; this.newYs = newYs;
            oldCurve = part.curves.FirstOrDefault(c => c.abbr == abbr);
            oldXsReference = oldCurve?.xs; oldYsReference = oldCurve?.ys;
            oldRealXsReference = oldCurve?.realXs; oldRealYsReference = oldCurve?.realYs;
            oldXs = oldCurve?.xs.ToArray(); oldYs = oldCurve?.ys.ToArray();
            oldRealXs = oldCurve?.realXs.ToArray(); oldRealYs = oldCurve?.realYs.ToArray();
            this.descriptor = descriptor ?? oldCurve?.descriptor!;
        }
        public override string ToString() => $"Set curve {abbr}";
        public override ValidateOptions ValidateOptions => new ValidateOptions {
            SkipTiming = true, Part = Part, SkipPhonemizer = true, SkipPhoneme = true,
        };
        public override void Execute() {
            var curve = Part.curves.FirstOrDefault(c => c.abbr == abbr);
            if (curve == null) {
                curve = new UCurve(descriptor) { abbr = abbr };
                Part.curves.Add(curve);
            }
            curve.xs = new List<int>(newXs);
            curve.ys = new List<int>(newYs);
        }
        public override void Unexecute() {
            var curve = Part.curves.FirstOrDefault(c => c.abbr == abbr);
            if (oldCurve == null) {
                if (curve != null) Part.curves.Remove(curve);
                return;
            }
            if (curve == null) { Part.curves.Add(oldCurve); curve = oldCurve; }
            curve.xs = oldXsReference!; curve.ys = oldYsReference!;
            curve.realXs = oldRealXsReference!; curve.realYs = oldRealYsReference!;
        }
    }
}


