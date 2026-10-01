using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.UiTest {
    public class QuickStartTest {
        static void WithFirstLaunch(Action<MainWindow, MainWindowViewModel> test) => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            HeadlessUi.Errors.Clear();
            var prefs = Preferences.Default;
            var originalCompleted = prefs.QuickStartCompleted;
            var originalRecovery = prefs.RecoveryPath;
            var originalRecents = prefs.RecentFiles.ToList();
            var originalSize = prefs.MainWindowSize;
            var originalProject = DocManager.Inst.Project;
            var originalRecovered = DocManager.Inst.Recovered;
            prefs.QuickStartCompleted = false;
            prefs.RecoveryPath = string.Empty;
            prefs.MainWindowSize = new WindowSize();
            var window = new MainWindow { Width = 1280, Height = 800 };
            try {
                window.Show();
                test(window, Assert.IsType<MainWindowViewModel>(window.DataContext));
                HeadlessUi.Flush();
                var errors = HeadlessUi.Errors.Snapshot();
                Assert.True(errors.Count == 0, string.Join("\n", errors));
            } finally {
                window.Close();
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(originalProject));
                DocManager.Inst.Recovered = originalRecovered;
                prefs.QuickStartCompleted = originalCompleted;
                prefs.RecoveryPath = originalRecovery;
                prefs.RecentFiles = originalRecents;
                prefs.MainWindowSize = originalSize;
                Preferences.Save();
            }
        });

        static void Click(Window window, string name) {
            var button = window.FindControl<Button>(name);
            Assert.NotNull(button);
            Assert.True(button.IsEffectivelyVisible);
            var point = button.TranslatePoint(
                new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
            Assert.NotNull(point);
            window.MouseDown(point.Value, MouseButton.Left);
            window.MouseUp(point.Value, MouseButton.Left);
            HeadlessUi.Flush();
        }

        [Theory]
        [InlineData("QuickStartSkipButton")]
        [InlineData("QuickStartContinueButton")]
        public void CompletingQuickStartPersistsAndDoesNotRepeat(string buttonName) =>
            WithFirstLaunch((window, viewModel) => {
                window.InitProject(new[] { "NyaUTAU" });
                HeadlessUi.Flush();
                Assert.True(viewModel.ShowQuickStart);
                Assert.False(Preferences.Default.QuickStartCompleted);
                HeadlessUi.SaveScreenshot(window, "QuickStart_FirstRun");

                Click(window, buttonName);
                Assert.False(viewModel.ShowQuickStart);
                Assert.Equal(0, viewModel.Page);
                Assert.True(Preferences.Default.QuickStartCompleted);
                var saved = Json.Deserialize<Preferences.SerializablePreferences>(
                    File.ReadAllText(PathManager.Inst.PrefsFilePath));
                Assert.NotNull(saved);
                Assert.True(saved.QuickStartCompleted);

                var nextWindow = new MainWindow { Width = 1280, Height = 800 };
                try {
                    nextWindow.Show();
                    nextWindow.InitProject(new[] { "NyaUTAU" });
                    HeadlessUi.Flush();
                    var next = Assert.IsType<MainWindowViewModel>(nextWindow.DataContext);
                    Assert.False(next.ShowQuickStart);
                    Assert.Equal(0, next.Page);
                } finally {
                    nextWindow.Close();
                }
            });

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CompletingQuickStartPreservesPendingStartupProject(bool recovery) {
            var directory = Path.Combine(Path.GetTempPath(),
                "NyaUTAU.QuickStartTest." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "startup.ustx");
            File.WriteAllText(file,
                $"ustx_version: {Core.Format.Ustx.kUstxVersion}\n" +
                "name: Startup project\ntempos:\n- {position: 0, bpm: 145}\n");
            try {
                WithFirstLaunch((window, viewModel) => {
                    var original = DocManager.Inst.Project;
                    if (recovery) {
                        Preferences.Default.RecoveryPath = file;
                    }
                    var arguments = recovery ? new[] { "NyaUTAU" } : new[] { "NyaUTAU", file };
                    window.InitProject(arguments);
                    HeadlessUi.Flush();
                    Assert.True(viewModel.ShowQuickStart);
                    Assert.False(viewModel.HasRecovery);
                    Assert.Same(original, DocManager.Inst.Project);

                    Click(window, "QuickStartSkipButton");
                    Assert.False(viewModel.ShowQuickStart);
                    if (recovery) {
                        Assert.True(viewModel.HasRecovery);
                        Assert.Equal(file, viewModel.RecoveryPath);
                        Assert.Equal(0, viewModel.Page);
                    } else {
                        Assert.Equal(file, DocManager.Inst.Project.FilePath);
                        Assert.Equal(145, Assert.Single(DocManager.Inst.Project.tempos).bpm);
                        Assert.Equal(1, viewModel.Page);
                    }
                });
            } finally {
                Directory.Delete(directory, true);
            }
        }
    }
}
