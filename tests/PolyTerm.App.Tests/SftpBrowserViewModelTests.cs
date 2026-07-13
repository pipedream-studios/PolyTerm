using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the chmod add-on to the SFTP browser's CRUD logic (rename/delete/mkdir already had no
/// coverage either, but chmod is the newest addition and the one this file exists for) against a
/// fake <see cref="IFileTransferChannel"/> â€” no real SSH connection needed.
/// </summary>
public sealed class SftpBrowserViewModelTests
{
    private sealed class FakeFileTransferChannel : IFileTransferChannel
    {
        public string InitialDirectory => "/home/user";
        public List<(string Path, string Permissions)> ChmodCalls { get; } = [];
        public bool ThrowOnChmod { get; set; }

        // For EditInLocalEditorAsync coverage: DownloadAsync writes canned content per remote path;
        // UploadAsync records every re-upload (path + content) so a test can assert what was saved.
        public Dictionary<string, byte[]> DownloadContent { get; } = [];
        public List<(string RemotePath, byte[] Content)> UploadCalls { get; } = [];
        private readonly object _uploadCallsLock = new();
        public bool ThrowOnUpload { get; set; }

        public Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteFileInfo>>([]);

        public async Task DownloadAsync(string remotePath, Stream local, IProgress<TransferProgress>? progress, CancellationToken ct = default)
        {
            if (DownloadContent.TryGetValue(remotePath, out var content))
                await local.WriteAsync(content, ct);
        }

        public async Task UploadAsync(Stream local, string remotePath, IProgress<TransferProgress>? progress, CancellationToken ct = default)
        {
            if (ThrowOnUpload) throw new InvalidOperationException("upload rejected");
            using var ms = new MemoryStream();
            await local.CopyToAsync(ms, ct);
            lock (_uploadCallsLock) UploadCalls.Add((remotePath, ms.ToArray()));
        }

        public Task DeleteAsync(string remotePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken ct = default) => Task.CompletedTask;

        public Task MkDirAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RemoteFileInfo?> StatAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<RemoteFileInfo?>(null);

        public Task ChmodAsync(string path, string permissions, CancellationToken ct = default)
        {
            if (ThrowOnChmod) throw new InvalidOperationException("permission denied");
            ChmodCalls.Add((path, permissions));
            return Task.CompletedTask;
        }
    }

    private static RemoteEntryViewModel MakeEntry(string name = "script.sh", bool isDirectory = false) =>
        new(new RemoteFileInfo { Name = name, FullPath = $"/home/user/{name}", IsDirectory = isDirectory });

    [AvaloniaFact]
    public async Task ChmodAsync_AppliesEnteredOctalMode_ToTheEntrysPath()
    {
        var channel = new FakeFileTransferChannel();
        var vm = new SftpBrowserViewModel(channel) { PromptForNameAsync = (_, _) => Task.FromResult<string?>("640") };
        var entry = MakeEntry();

        await vm.ChmodAsync(entry);

        channel.ChmodCalls.Should().ContainSingle().Which.Should().Be((entry.FullPath, "640"));
    }

    [AvaloniaFact]
    public async Task ChmodAsync_DoesNothing_WhenPromptCancelled()
    {
        var channel = new FakeFileTransferChannel();
        var vm = new SftpBrowserViewModel(channel) { PromptForNameAsync = (_, _) => Task.FromResult<string?>(null) };

        await vm.ChmodAsync(MakeEntry());

        channel.ChmodCalls.Should().BeEmpty();
    }

    [AvaloniaTheory]
    [InlineData("888")]
    [InlineData("75")]
    [InlineData("rwxr-xr-x")]
    public async Task ChmodAsync_RejectsInvalidOctalMode_WithoutCallingTheChannel(string invalid)
    {
        var channel = new FakeFileTransferChannel();
        var vm = new SftpBrowserViewModel(channel) { PromptForNameAsync = (_, _) => Task.FromResult<string?>(invalid) };

        await vm.ChmodAsync(MakeEntry());

        channel.ChmodCalls.Should().BeEmpty();
        vm.Error.Should().NotBeNullOrEmpty();
    }

    [AvaloniaFact]
    public async Task ChmodAsync_SetsError_WhenChannelThrows()
    {
        var channel = new FakeFileTransferChannel { ThrowOnChmod = true };
        var vm = new SftpBrowserViewModel(channel) { PromptForNameAsync = (_, _) => Task.FromResult<string?>("755") };

        await vm.ChmodAsync(MakeEntry());

        vm.Error.Should().Contain("permission denied");
    }

    /// <summary>
    /// Covers the M4 "remote-file edit-in-local-editor round-trip" feature: real temp-file I/O and a
    /// real <see cref="FileSystemWatcher"/> (not mocked — that's the actual mechanism catching saves),
    /// against a fake channel so no SSH connection is needed. <see cref="SftpBrowserViewModel.LaunchEditor"/>
    /// is overridden so the test never actually spawns a GUI editor.
    /// </summary>
    public sealed class EditInLocalEditorTests : IDisposable
    {
        private readonly List<string> _launchedPaths = [];
        private SftpBrowserViewModel? _vm;

        private SftpBrowserViewModel MakeVm(FakeFileTransferChannel channel)
        {
            _vm = new SftpBrowserViewModel(channel) { LaunchEditor = path => _launchedPaths.Add(path) };
            return _vm;
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(50);
            }
            return condition();
        }

        [AvaloniaFact]
        public async Task DownloadsToATempFile_AndLaunchesTheEditorOnIt()
        {
            var channel = new FakeFileTransferChannel();
            var entry = MakeEntry("script.sh");
            channel.DownloadContent[entry.FullPath] = "#!/bin/sh\necho hi\n"u8.ToArray();
            var vm = MakeVm(channel);

            await vm.EditInLocalEditorAsync(entry);

            _launchedPaths.Should().ContainSingle();
            var localPath = _launchedPaths[0];
            Path.GetFileName(localPath).Should().Be("script.sh");
            File.Exists(localPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(localPath)).Should().BeEquivalentTo(channel.DownloadContent[entry.FullPath]);
        }

        [AvaloniaFact]
        public async Task SavingTheLocalFile_ReuploadsItToTheRemotePath()
        {
            var channel = new FakeFileTransferChannel();
            var entry = MakeEntry("notes.txt");
            channel.DownloadContent[entry.FullPath] = "original"u8.ToArray();
            var vm = MakeVm(channel);
            await vm.EditInLocalEditorAsync(entry);
            var localPath = _launchedPaths[0];

            await File.WriteAllTextAsync(localPath, "edited by the user");

            // FileSystemWatcher's own event delivery (on top of EditSession's 500ms debounce) isn't
            // instant and can be delayed well beyond "a few seconds" under real I/O/CPU contention - seen
            // failing at exactly 5s on windows-latest's "full solution" CI leg (which runs every other
            // test project's real background threads/servers concurrently), never locally. Widened to
            // give real headroom rather than assuming a fixed-desktop dev machine's responsiveness.
            var uploaded = await WaitUntilAsync(() => channel.UploadCalls.Count > 0, TimeSpan.FromSeconds(20));
            uploaded.Should().BeTrue("the watcher should have caught the save and re-uploaded within a generous window");
            var (remotePath, content) = channel.UploadCalls[0];
            remotePath.Should().Be(entry.FullPath);
            System.Text.Encoding.UTF8.GetString(content).Should().Be("edited by the user");
        }

        [AvaloniaFact]
        public async Task EditingTheSameRemoteFileTwice_TracksBothSessionsIndependently()
        {
            var channel = new FakeFileTransferChannel();
            var entry = MakeEntry("dup.txt");
            channel.DownloadContent[entry.FullPath] = "v1"u8.ToArray();
            var vm = MakeVm(channel);

            await vm.EditInLocalEditorAsync(entry);
            await vm.EditInLocalEditorAsync(entry);

            _launchedPaths.Should().HaveCount(2);
            _launchedPaths[0].Should().NotBe(_launchedPaths[1], "each edit session gets its own temp file, even for the same remote path");
            File.Exists(_launchedPaths[0]).Should().BeTrue();
            File.Exists(_launchedPaths[1]).Should().BeTrue();
        }

        [AvaloniaFact]
        public async Task Dispose_StopsWatching_SoLaterSavesAreNotUploaded()
        {
            var channel = new FakeFileTransferChannel();
            var entry = MakeEntry("live.txt");
            channel.DownloadContent[entry.FullPath] = "v1"u8.ToArray();
            var vm = MakeVm(channel);
            await vm.EditInLocalEditorAsync(entry);
            var localPath = _launchedPaths[0];

            vm.Dispose();
            try { await File.WriteAllTextAsync(localPath, "should not be uploaded"); } catch { /* temp dir may already be gone */ }
            await Task.Delay(700); // longer than the 500ms debounce, to prove nothing fires

            channel.UploadCalls.Should().BeEmpty();
        }

        public void Dispose() => _vm?.Dispose();
    }
}
