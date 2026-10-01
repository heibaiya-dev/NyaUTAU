using System;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;

namespace OpenUtau.App.ViewModels {
    public partial class AutoTuningViewModel : ViewModelBase {
        [Reactive] public partial int PresetIndex { get; set; }
        [Reactive] public partial double Strength { get; set; } = 65;
        [Reactive] public partial bool Pitch { get; set; } = true;
        [Reactive] public partial bool Vibrato { get; set; } = true;
        [Reactive] public partial bool Dynamics { get; set; } = true;
        [Reactive] public partial bool Breathiness { get; set; } = true;
        [Reactive] public partial bool Articulation { get; set; } = true;

        private bool useSelection;
        public bool UseSelection {
            get => useSelection;
            set {
                this.RaiseAndSetIfChanged(ref useSelection, value && HasSelection);
                this.RaisePropertyChanged(nameof(UseWholePart));
                this.RaisePropertyChanged(nameof(CanApply));
            }
        }
        public bool UseWholePart {
            get => !UseSelection;
            set { if (value) { UseSelection = false; } }
        }

        public bool HasSelection { get; }
        public string SelectionLabel { get; }
        public string PartLabel { get; }
        public string SingerLabel { get; }
        public bool SupportsDynamics { get; }
        public bool SupportsBreathiness { get; }
        public bool SupportsArticulation { get; }
        public string DynamicsStatus { get; }
        public string BreathinessStatus { get; }
        public string ArticulationStatus { get; }
        public bool HasUnsupportedChannels =>
            !SupportsDynamics || !SupportsBreathiness || !SupportsArticulation;
        public bool CanApply => (!UseSelection || HasSelection) &&
            (Pitch || Vibrato || Dynamics || Breathiness || Articulation);

        public AutoTuningViewModel(UProject project, UVoicePart part, int selectedCount) {
            HasSelection = selectedCount > 0;
            UseSelection = HasSelection;
            SelectionLabel = string.Format(
                ThemeManager.GetString("autotuning.scope.selection"), selectedCount);
            PartLabel = string.Format(
                ThemeManager.GetString("autotuning.scope.part"), part.notes.Count);
            var track = project.tracks[part.trackNo];
            SingerLabel = string.Format(ThemeManager.GetString("autotuning.singer"),
                track.Singer?.Name ?? ThemeManager.GetString("autotuning.singer.none")) + " · " +
                string.Format(ThemeManager.GetString("autotuning.renderer"),
                    track.RendererSettings?.renderer ??
                    ThemeManager.GetString("autotuning.renderer.none"));
            var capabilities = AutoVocalTuning.GetCapabilities(project, part);
            SupportsDynamics = capabilities.Dynamics;
            SupportsBreathiness = capabilities.Breathiness;
            SupportsArticulation = capabilities.Articulation;
            DynamicsStatus = GetCapabilityStatus(SupportsDynamics, capabilities.DynamicsReason);
            BreathinessStatus = GetCapabilityStatus(SupportsBreathiness, capabilities.BreathinessReason);
            ArticulationStatus = GetCapabilityStatus(SupportsArticulation, capabilities.ArticulationReason);
            this.WhenAnyValue(x => x.Strength, x => x.Pitch, x => x.Vibrato,
                    x => x.Dynamics, x => x.Breathiness, x => x.Articulation)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(CanApply)));
        }

        public AutoVocalTuningOptions GetOptions() => new AutoVocalTuningOptions {
            Preset = (VocalTuningPreset)Math.Clamp(PresetIndex, 0, 2),
            Strength = Math.Clamp(Strength / 100, 0, 1),
            Pitch = Pitch,
            Vibrato = Vibrato,
            Dynamics = Dynamics,
            Breathiness = Breathiness,
            Articulation = Articulation,
        };

        static string GetCapabilityStatus(bool supported, string? reason) {
            if (supported) {
                return ThemeManager.GetString("autotuning.supported");
            }
            var unsupported = ThemeManager.GetString("autotuning.unsupported");
            return string.IsNullOrWhiteSpace(reason) ? unsupported : $"{unsupported} ({reason})";
        }
    }
}
