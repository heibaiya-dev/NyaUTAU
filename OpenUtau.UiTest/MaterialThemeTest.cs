using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Colors;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.UiTest {
    public class MaterialThemeTest {
        static void SetTheme(string theme) {
            Preferences.Default.ThemeName = theme;
            App.App.SetTheme();
            HeadlessUi.Flush();
        }

        static Color ResourceColor(Control control, string key) =>
            Assert.IsAssignableFrom<ISolidColorBrush>(control.FindResource(key)).Color;

        static double Luminance(Color color) {
            static double Linear(byte channel) {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        static void AssertReadable(Control control, string foreground, string background) {
            var first = Luminance(ResourceColor(control, foreground));
            var second = Luminance(ResourceColor(control, background));
            var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
            Assert.True(contrast >= 4.5, $"{foreground} on {background}: {contrast:F2}:1 contrast");
        }

        static void AssertVisible(Control? control) {
            Assert.NotNull(control);
            Assert.True(control.IsEffectivelyVisible, $"{control} is hidden");
            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"{control} has no size");
        }

        static (float X, float Y, PitchPointShape Shape)[] PitchPoints(UPitch pitch) =>
            pitch.data.Select(point => (point.X, point.Y, point.shape)).ToArray();

        static void AssertPitchEqual(UPitch expected, UPitch actual) {
            Assert.Equal(expected.snapFirst, actual.snapFirst);
            Assert.Equal(PitchPoints(expected), PitchPoints(actual));
        }

        static void InitializePianoRollOnWorker(PianoRoll pianoRoll) {
            var task = Task.Run(pianoRoll.InitializePianoRollWindowAsync);
            var timer = Stopwatch.StartNew();
            while (!task.IsCompleted && timer.ElapsedMilliseconds < 10000) {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.True(task.IsCompleted, "Piano roll initialization did not finish");
            task.GetAwaiter().GetResult();
        }

        static void Click(Window window, Control control) {
            AssertVisible(control);
            var center = control.TranslatePoint(
                new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
            Assert.NotNull(center);
            window.MouseDown(center.Value, MouseButton.Left);
            window.MouseUp(center.Value, MouseButton.Left);
            HeadlessUi.Flush();
        }

        [Fact]
        public void PreferencesPagesRemainUsableAcrossLiveThemeChanges() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            HeadlessUi.Errors.Clear();
            var originalTheme = Preferences.Default.ThemeName;
            var viewModel = new PreferencesViewModel();
            var window = new PreferencesDialog { DataContext = viewModel };
            try {
                window.Show();
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                var drawer = window.FindControl<ListBox>("DrawerList")!;
                var pages = window.FindControl<Carousel>("PageCarousel")!;
                Assert.True(drawer.ItemCount > 0);
                Assert.Equal(drawer.ItemCount, pages.ItemCount);
                Color? previousSurface = null;
                foreach (var theme in new[] { "Light", "Dark" }) {
                    viewModel.ThemeName = theme;
                    SetTheme(theme);
                    var surface = ResourceColor(window, "MaterialSurfaceBrush");
                    Assert.NotEqual(previousSurface, surface);
                    Assert.Equal(surface, Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
                    previousSurface = surface;
                    AssertReadable(window, "SystemControlForegroundBaseHighBrush", "MaterialSurfaceBrush");
                    AssertReadable(window, "MaterialOnPrimaryBrush", "MaterialPrimaryBrush");
                    AssertReadable(window, "MaterialOnPrimaryContainerBrush", "MaterialPrimaryContainerBrush");
                    for (int index = 0; index < drawer.ItemCount; index++) {
                        drawer.ScrollIntoView(index);
                        HeadlessUi.Flush();
                        var item = drawer.ContainerFromIndex(index)!;
                        Click(window, item);
                        Assert.Equal(index, pages.SelectedIndex);
                        var page = Assert.IsAssignableFrom<Control>(pages.SelectedItem);
                        AssertVisible(page);
                        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
                            text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text));
                        Assert.True(HeadlessUi.CountRenderedColors(window) > 10);
                        HeadlessUi.SaveScreenshot(window, $"Preferences{index}_{theme}");
                    }
                }
                var errors = HeadlessUi.Errors.Snapshot();
                Assert.True(errors.Count == 0, string.Join("\n", errors));
            } finally {
                window.Close();
                viewModel.ThemeName = originalTheme;
                SetTheme(originalTheme);
            }
        });

        [Fact]
        public void CustomThemeKeepsFilledActionsReadable() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            HeadlessUi.Errors.Clear();
            var originalTheme = Preferences.Default.ThemeName;
            var originalCustom = CustomTheme.Default;
            var themeName = "Material test " + Guid.NewGuid().ToString("N");
            var themeFile = Path.Combine(Path.GetTempPath(), themeName + ".yaml");
            var button = new Button { Content = "Action", Classes = { "material-primary" } };
            var window = new Window { Width = 240, Height = 120, Content = button };
            try {
                CustomTheme.Themes.Add(themeName, themeFile);
                window.Show();
                foreach (var accent in new[] { "#4EA6EA", "#FFFF00", "#111111" }) {
                    var theme = new CustomTheme.ThemeYaml { Name = themeName, AccentColor1 = accent };
                    File.WriteAllText(themeFile, Yaml.DefaultSerializer.Serialize(theme));
                    SetTheme(themeName);
                    Assert.Equal(Color.Parse(accent), ResourceColor(button, "MaterialPrimaryBrush"));
                    Assert.Equal(ResourceColor(button, "MaterialPrimaryBrush"),
                        Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
                    Assert.Equal(ResourceColor(button, "MaterialOnPrimaryBrush"),
                        Assert.IsAssignableFrom<ISolidColorBrush>(button.Foreground).Color);
                    AssertReadable(button, "MaterialOnPrimaryBrush", "MaterialPrimaryBrush");
                }
                Assert.Empty(HeadlessUi.Errors.Snapshot());
            } finally {
                window.Close();
                CustomTheme.Themes.Remove(themeName);
                File.Delete(themeFile);
                CustomTheme.Default = originalCustom;
                SetTheme(originalTheme);
            }
        });

        [Fact]
        public void PianoRollKeepsNotesAndToolbarUsableAcrossLiveThemeChanges() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            HeadlessUi.Errors.Clear();
            var originalTheme = Preferences.Default.ThemeName;
            var originalProject = DocManager.Inst.Project;
            var originalShowPitch = Preferences.Default.ShowPitch;
            var originalShowTips = Preferences.Default.ShowTips;
            var originalShowExpressions = Preferences.Default.ShowExpressions;
            var project = Core.Format.Ustx.Create();
            var part = new UVoicePart { name = "Theme preview", trackNo = 0, Duration = 3840 };
            var melody = new[] { 60, 62, 64, 67, 64, 62 };
            var lyrics = new[] { "do", "re", "mi", "sol", "mi", "re" };
            foreach (var (tone, index) in melody.Select((tone, index) => (tone, index))) {
                var note = project.CreateNote(tone, index * 480, 420);
                note.lyric = lyrics[index];
                part.notes.Add(note);
            }
            project.parts.Add(part);
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            var playback = new PlaybackViewModel();
            var viewModel = new PianoRollViewModel { PlaybackViewModel = playback };
            var pianoRoll = new PianoRoll(viewModel);
            var window = new Window { Width = 1200, Height = 720, Content = pianoRoll };
            try {
                InitializePianoRollOnWorker(pianoRoll);
                window.Show();
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
                pianoRoll.AttachExpressions();
                viewModel.NotesViewModel.TrackOffset = 60;
                var firstNote = part.notes.First();
                viewModel.NotesViewModel.SelectNote(firstNote);
                viewModel.NotesViewModel.ShowExpressions = true;
                viewModel.NotesViewModel.ShowTips = true;
                HeadlessUi.Flush();
                var noteHelp = pianoRoll.FindControl<Control>("NoteHelpPanel")!;
                var expressionHelp = pianoRoll.FindControl<Control>("ExpressionHelpPanel")!;
                AssertVisible(noteHelp);
                AssertVisible(expressionHelp);
                Click(window, pianoRoll.FindControl<Button>("DismissNoteTipsButton")!);
                Assert.False(viewModel.NotesViewModel.ShowTips);
                Assert.False(noteHelp.IsEffectivelyVisible);
                Assert.False(expressionHelp.IsEffectivelyVisible);
                window.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.T, null);
                HeadlessUi.Flush();
                Assert.True(viewModel.NotesViewModel.ShowTips);
                AssertVisible(noteHelp);
                AssertVisible(expressionHelp);
                Click(window, pianoRoll.FindControl<Button>("DismissExpressionTipsButton")!);
                Assert.False(viewModel.NotesViewModel.ShowTips);
                Assert.False(noteHelp.IsEffectivelyVisible);
                Assert.False(expressionHelp.IsEffectivelyVisible);
                var savedPreferences = Json.Deserialize<Preferences.SerializablePreferences>(
                    File.ReadAllText(PathManager.Inst.PrefsFilePath));
                Assert.NotNull(savedPreferences);
                Assert.False(savedPreferences.ShowTips);
                foreach (var theme in new[] { "Light", "Dark" }) {
                    SetTheme(theme);
                    Assert.Same(part, viewModel.NotesViewModel.Part);
                    Assert.Equal(6, part.notes.Count);
                    var notes = pianoRoll.GetVisualDescendants().OfType<Control>()
                        .Single(control => control.GetType().Name == "NotesCanvas");
                    AssertVisible(notes);
                    var snap = pianoRoll.FindControl<ToggleButton>("SnapToggle")!;
                    snap.BringIntoView();
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, $"PianoRoll_{theme}");
                    var oldSnap = viewModel.NotesViewModel.IsSnapOn;
                    Click(window, snap);
                    Assert.Equal(!oldSnap, viewModel.NotesViewModel.IsSnapOn);
                    Click(window, snap);
                    Assert.Equal(oldSnap, viewModel.NotesViewModel.IsSnapOn);
                    var originalTone = firstNote.tone;
                    pianoRoll.Focus();
                    window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                    HeadlessUi.Flush();
                    Assert.Equal(originalTone + 1, firstNote.tone);
                    window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
                    HeadlessUi.Flush();
                    Assert.Equal(originalTone, firstNote.tone);
                    Assert.True(HeadlessUi.CountRenderedColors(window) > 10);
                    HeadlessUi.SaveScreenshot(window, $"PianoRoll_{theme}");
                }

                var allNotes = part.notes.ToArray();
                var originalPitches = allNotes.Select(note => note.pitch.Clone()).ToArray();
                var smartPitch = pianoRoll.FindControl<Button>("SmartPitchButton")!;
                smartPitch.BringIntoView();
                HeadlessUi.Flush();
                viewModel.NotesViewModel.SelectNote(allNotes[0]);
                viewModel.NotesViewModel.SelectNote(allNotes[1], false);
                viewModel.NotesViewModel.ShowPitch = false;
                Click(window, smartPitch);
                Assert.True(viewModel.NotesViewModel.ShowPitch);
                for (int index = 0; index < allNotes.Length; index++) {
                    if (index < 2) {
                        Assert.False(PitchPoints(originalPitches[index])
                            .SequenceEqual(PitchPoints(allNotes[index].pitch)));
                    } else {
                        AssertPitchEqual(originalPitches[index], allNotes[index].pitch);
                    }
                }
                HeadlessUi.SaveScreenshot(window, "SmartPitch_Selected");
                void UndoPitchEdit() {
                    pianoRoll.Focus();
                    window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
                    HeadlessUi.Flush();
                    for (int index = 0; index < allNotes.Length; index++) {
                        AssertPitchEqual(originalPitches[index], allNotes[index].pitch);
                    }
                }
                UndoPitchEdit();

                viewModel.NotesViewModel.DeselectNotes();
                viewModel.NotesViewModel.ShowPitch = false;
                Click(window, smartPitch);
                Assert.True(viewModel.NotesViewModel.ShowPitch);
                for (int index = 0; index < allNotes.Length; index++) {
                    Assert.False(PitchPoints(originalPitches[index])
                        .SequenceEqual(PitchPoints(allNotes[index].pitch)));
                }
                HeadlessUi.SaveScreenshot(window, "SmartPitch_AllNotes");
                UndoPitchEdit();
                window.Width = 640;
                HeadlessUi.Flush();
                var tools = pianoRoll.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(viewer => viewer.Classes.Contains("editor-tools"));
                HeadlessUi.SaveScreenshot(window, "PianoRoll_Narrow");
                Assert.True(tools.Viewport.Width >= 32, "The editor toolbar has no usable viewport");
                var errors = HeadlessUi.Errors.Snapshot();
                Assert.True(errors.Count == 0, string.Join("\n", errors));
            } finally {
                window.Close();
                viewModel.NotesViewModel.ShowPitch = originalShowPitch;
                viewModel.NotesViewModel.ShowTips = originalShowTips;
                viewModel.NotesViewModel.ShowExpressions = originalShowExpressions;
                DocManager.Inst.RemoveSubscriber(pianoRoll);
                DocManager.Inst.RemoveSubscriber(viewModel);
                DocManager.Inst.RemoveSubscriber(viewModel.NotesViewModel);
                DocManager.Inst.RemoveSubscriber(viewModel.CurveViewModel);
                DocManager.Inst.RemoveSubscriber(playback);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(originalProject));
                SetTheme(originalTheme);
            }
        });
    }
}
