using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.UiTest {
    public class AutoVocalTuningUiTest {
        static void WaitFor(Func<bool> condition, string message) {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.ElapsedMilliseconds < 10000) {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            HeadlessUi.Flush();
            Assert.True(condition(), message);
        }

        static void Click(Window window, Control control) {
            Assert.True(control.IsEffectivelyVisible);
            Assert.True(control.IsEnabled);
            var center = control.TranslatePoint(
                new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
            Assert.NotNull(center);
            window.MouseDown(center.Value, MouseButton.Left);
            window.MouseUp(center.Value, MouseButton.Left);
            HeadlessUi.Flush();
        }

        static void WithPianoRoll(Action<Window, PianoRoll, PianoRollViewModel, UVoicePart> test) =>
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var originalProject = DocManager.Inst.Project;
                var originalPitch = Preferences.Default.ShowPitch;
                var originalVibrato = Preferences.Default.ShowVibrato;
                var originalTips = Preferences.Default.ShowTips;
                var project = Core.Format.Ustx.Create();
                var part = new UVoicePart { name = "Automatic tuning", Duration = 3840 };
                for (int index = 0; index < 3; index++) {
                    var note = project.CreateNote(60 + index * 2, index * 960, 960);
                    note.lyric = "a";
                    note.vibrato.length = 0;
                    part.notes.Add(note);
                }
                project.parts.Add(part);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                var playback = new PlaybackViewModel();
                var viewModel = new PianoRollViewModel { PlaybackViewModel = playback };
                var pianoRoll = new PianoRoll(viewModel);
                var window = new Window { Width = 1280, Height = 800, Content = pianoRoll };
                try {
                    var initialization = Task.Run(pianoRoll.InitializePianoRollWindowAsync);
                    WaitFor(() => initialization.IsCompleted, "Piano roll did not initialize");
                    initialization.GetAwaiter().GetResult();
                    window.Show();
                    DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
                    pianoRoll.AttachExpressions();
                    viewModel.NotesViewModel.ShowTips = false;
                    viewModel.NotesViewModel.TrackOffset = 60;
                    HeadlessUi.Flush();
                    test(window, pianoRoll, viewModel, part);
                    var errors = HeadlessUi.Errors.Snapshot();
                    Assert.True(errors.Count == 0, string.Join("\n", errors));
                } finally {
                    foreach (var owned in window.OwnedWindows.ToArray()) {
                        owned.Close();
                    }
                    window.Close();
                    viewModel.NotesViewModel.ShowPitch = originalPitch;
                    viewModel.NotesViewModel.ShowVibrato = originalVibrato;
                    viewModel.NotesViewModel.ShowTips = originalTips;
                    DocManager.Inst.RemoveSubscriber(pianoRoll);
                    DocManager.Inst.RemoveSubscriber(viewModel);
                    DocManager.Inst.RemoveSubscriber(viewModel.NotesViewModel);
                    DocManager.Inst.RemoveSubscriber(viewModel.CurveViewModel);
                    DocManager.Inst.RemoveSubscriber(playback);
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(originalProject));
                }
            });

        static AutoTuningDialog OpenDialog(Window window, PianoRoll pianoRoll) {
            var button = pianoRoll.FindControl<Button>("AutoTuningButton")!;
            button.BringIntoView();
            HeadlessUi.Flush();
            Click(window, button);
            WaitFor(() => window.OwnedWindows.OfType<AutoTuningDialog>().Any(),
                "The automatic tuning dialog did not open");
            return window.OwnedWindows.OfType<AutoTuningDialog>().Single();
        }

        static void Apply(Window window, PianoRoll pianoRoll, AutoTuningDialog dialog) {
            Click(dialog, dialog.FindControl<Button>("ApplyButton")!);
            var banner = pianoRoll.FindControl<Control>("AutoTuningResultBanner")!;
            WaitFor(() => !dialog.IsVisible && banner.IsEffectivelyVisible,
                "Automatic tuning did not report completion");
            var result = pianoRoll.FindControl<TextBlock>("AutoTuningResultText")!;
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
        }

        [Fact]
        public void CancelAndZeroStrengthKeepMusicalDataUnchanged() =>
            WithPianoRoll((window, pianoRoll, viewModel, part) => {
                var original = Yaml.DefaultSerializer.Serialize(part);
                var dialog = OpenDialog(window, pianoRoll);
                dialog.FindControl<ComboBox>("PresetSelector")!.SelectedIndex = 2;
                dialog.FindControl<Slider>("StrengthSlider")!.Value = 90;
                Click(dialog, dialog.FindControl<Button>("CancelButton")!);
                Assert.False(dialog.IsVisible);
                Assert.Equal(original, Yaml.DefaultSerializer.Serialize(part));

                dialog = OpenDialog(window, pianoRoll);
                dialog.FindControl<Slider>("StrengthSlider")!.Value = 0;
                HeadlessUi.Flush();
                Assert.True(dialog.FindControl<Button>("ApplyButton")!.IsEnabled);
                Apply(window, pianoRoll, dialog);
                Assert.Equal(original, Yaml.DefaultSerializer.Serialize(part));
            });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void PresetAppliesToRequestedScopeAndOneUndoRestoresAllChanges(bool selectionOnly) =>
            WithPianoRoll((window, pianoRoll, viewModel, part) => {
                var notes = part.notes.ToArray();
                var original = Yaml.DefaultSerializer.Serialize(part);
                var originalNotes = notes.Select(note => Yaml.DefaultSerializer.Serialize(note)).ToArray();
                viewModel.NotesViewModel.DeselectNotes();
                viewModel.NotesViewModel.SelectNote(notes[0]);
                viewModel.NotesViewModel.SelectNote(notes[2], false);
                var dialog = OpenDialog(window, pianoRoll);
                var preset = dialog.FindControl<ComboBox>("PresetSelector")!;
                preset.SelectedIndex = 2;
                var strength = dialog.FindControl<Slider>("StrengthSlider")!;
                strength.Value = 80;
                var options = Assert.IsType<AutoTuningViewModel>(dialog.DataContext);
                HeadlessUi.Flush();
                Assert.Equal(2, options.PresetIndex);
                Assert.Equal(80, options.Strength);
                if (!selectionOnly) {
                    Click(dialog, dialog.FindControl<RadioButton>("PartScopeButton")!);
                }
                HeadlessUi.SaveScreenshot(dialog, "AutoTuning_Dialog");
                Apply(window, pianoRoll, dialog);
                Assert.True(viewModel.NotesViewModel.ShowPitch);
                for (int index = 0; index < notes.Length; index++) {
                    var actual = Yaml.DefaultSerializer.Serialize(notes[index]);
                    if (!selectionOnly || index != 1) {
                        Assert.NotEqual(originalNotes[index], actual);
                        Assert.True(notes[index].vibrato.length > 0);
                    } else {
                        Assert.Equal(originalNotes[index], actual);
                    }
                }
                HeadlessUi.SaveScreenshot(window, selectionOnly
                    ? "AutoTuning_Selected" : "AutoTuning_WholePart");
                DocManager.Inst.Undo();
                HeadlessUi.Flush();
                Assert.Equal(original, Yaml.DefaultSerializer.Serialize(part));
            });
    }
}
