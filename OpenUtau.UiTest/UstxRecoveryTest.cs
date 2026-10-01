using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.UiTest {
    public class UstxRecoveryTest {
        static bool Complete(Task<bool> task) {
            var timer = Stopwatch.StartNew();
            while (!task.IsCompleted && timer.ElapsedMilliseconds < 10000) {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.True(task.IsCompleted, "USTX recovery did not finish");
            return task.GetAwaiter().GetResult();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void VersionRecoveryRequiresConfirmationAndPreservesSource(bool accept) =>
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var originalProject = DocManager.Inst.Project;
                var originalRecovered = DocManager.Inst.Recovered;
                var originalRecents = Preferences.Default.RecentFiles.ToList();
                var originalQuickStartCompleted = Preferences.Default.QuickStartCompleted;
                Preferences.Default.QuickStartCompleted = true;
                var directory = Path.Combine(Path.GetTempPath(),
                    "OpenUtau.UstxRecoveryTest." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var source = Path.Combine(directory, "future.ustx");
                File.WriteAllText(source,
                    "name: Recovery source\nustx_version: 99.0\nbpm: 173\n");
                var originalBytes = File.ReadAllBytes(source);
                var viewModel = new MainWindowViewModel();
                try {
                    var exception = Assert.Throws<UstxVersionException>(() =>
                        viewModel.OpenProject(new[] { source }));
                    Assert.Equal(source, exception.FilePath);
                    int confirmations = 0;
                    viewModel.ConfirmUstxConversion = request => {
                        Assert.Same(exception, request);
                        confirmations++;
                        return Task.FromResult(accept);
                    };

                    var recovered = Complete(viewModel.TryRecoverUstxProject(exception));
                    HeadlessUi.Flush();
                    Assert.Equal(accept, recovered);
                    Assert.Equal(1, confirmations);
                    Assert.Equal(originalBytes, File.ReadAllBytes(source));
                    if (accept) {
                        var loaded = DocManager.Inst.Project;
                        Assert.NotSame(originalProject, loaded);
                        Assert.NotEqual(source, loaded.FilePath);
                        Assert.Equal(directory, Path.GetDirectoryName(loaded.FilePath));
                        Assert.True(File.Exists(loaded.FilePath));
                        Assert.Equal(173, Assert.Single(loaded.tempos).bpm);
                        Assert.Equal(1, viewModel.Page);
                        Assert.Equal(2, Directory.GetFiles(directory, "*.ustx").Length);
                    } else {
                        Assert.Same(originalProject, DocManager.Inst.Project);
                        Assert.Equal(0, viewModel.Page);
                        Assert.Single(Directory.GetFiles(directory, "*.ustx"));
                    }
                    var errors = HeadlessUi.Errors.Snapshot();
                    Assert.True(errors.Count == 0, string.Join("\n", errors));
                } finally {
                    DocManager.Inst.RemoveSubscriber(viewModel);
                    DocManager.Inst.RemoveSubscriber(viewModel.PlaybackViewModel);
                    DocManager.Inst.RemoveSubscriber(viewModel.TracksViewModel);
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(originalProject));
                    DocManager.Inst.Recovered = originalRecovered;
                    Preferences.Default.RecentFiles = originalRecents;
                    Preferences.Default.QuickStartCompleted = originalQuickStartCompleted;
                    Preferences.Save();
                    Directory.Delete(directory, true);
                }
            });
    }
}
