using System.IO.Compression;
using System.Runtime.InteropServices;
using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Sync;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>The guards that keep a mistake from damaging the game, the library or the machine.</summary>
public partial class HardeningTests
{
    [LibraryImport("libc", SetLastError = true)]
    private static partial nint mmap(nint address, nuint length, int protection, int flags, int fd, nint offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(nint address, nuint length);

    /// <summary>
    /// Freezing writes forty times a second without anyone watching, so a route gone stale that
    /// lands in read-only memory must fail rather than be forced through. A deliberate write may.
    /// </summary>
    [Fact]
    public unsafe void An_unattended_write_never_lifts_page_protection()
    {
        if (!OperatingSystem.IsLinux()) return;

        const int PROT_READ = 1, MAP_PRIVATE = 2, MAP_ANONYMOUS = 0x20;
        nint page = mmap(0, 4096, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        Assert.NotEqual(-1, (long)page);
        try
        {
            using var process = TargetProcess.Open(Environment.ProcessId, out string error);
            Assert.True(process is not null, error);

            int value = 0x1234;
            var bytes = new ReadOnlySpan<byte>(&value, 4);
            Assert.False(process!.Write((ulong)page, bytes, liftProtection: false));
            Assert.Equal(0, *(int*)page);

            Assert.True(process.Write((ulong)page, bytes, liftProtection: true));
            Assert.Equal(0x1234, *(int*)page);
        }
        finally
        {
            munmap(page, 4096);
        }
    }

    [Fact]
    public void A_closed_process_refuses_reads_and_writes()
    {
        var process = TargetProcess.Open(Environment.ProcessId, out string error);
        Assert.True(process is not null, error);
        int[] box = [5];
        var pin = GCHandle.Alloc(box, GCHandleType.Pinned);
        try
        {
            ulong at = (ulong)pin.AddrOfPinnedObject();
            process!.Dispose();
            Assert.False(process.WriteValue(at, 9));
            Assert.Equal(-1, process.ReadValue(at, -1));
            Assert.Equal(5, box[0]);
        }
        finally
        {
            pin.Free();
        }
    }

    [Theory]
    [InlineData("tables/../../.bashrc")]
    [InlineData("tables/..")]
    [InlineData("tables//etc/passwd")]
    [InlineData("tables/sub/game.cmt")]
    [InlineData("art/..\\..\\x.jpg")]
    [InlineData("other/game.cmt")]
    [InlineData("tables/")]
    public void A_backup_name_never_leaves_the_library(string remote)
    {
        Assert.Equal(string.Empty, GitHubSyncService.LocalPathFor(remote));
    }

    [Fact]
    public void A_plain_backup_name_lands_in_the_library()
    {
        string path = GitHubSyncService.LocalPathFor("tables/game-1234ABCD.cmt");
        Assert.Equal(Path.Combine(Path.GetFullPath(CheatLibrary.DefaultRoot), "game-1234ABCD.cmt"), path);
    }

    /// <summary>A few kilobytes of zip that would inflate to far more than any table is refused, not loaded.</summary>
    [Fact]
    public void A_compressed_table_that_inflates_too_far_is_refused()
    {
        string file = Path.Combine(Path.GetTempPath(), "cheatmaster-bomb-" + Guid.NewGuid().ToString("N")[..8] + ".CT");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("table.xml", CompressionLevel.SmallestSize);
                using var stream = entry.Open();
                var block = new byte[1 << 20];
                Array.Fill(block, (byte)' ');
                for (int i = 0; i < 80; i++) stream.Write(block);
            }
            Assert.True(new FileInfo(file).Length < 1 << 20, "the test archive should be small on disk");

            var report = CheatEngineTable.Load(file);
            Assert.True(report.Failed);
            Assert.Contains("too large", report.Error);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
