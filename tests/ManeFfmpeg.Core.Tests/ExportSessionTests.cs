using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Running exports: pause, cancel and keeping the computer awake, for the editor and Quick Actions alike.</summary>
public class ExportSessionTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    private sealed class Awake
    {
        public int Held { get; private set; }

        public IDisposable Hold(string reason)
        {
            Held++;
            return new Release(this);
        }

        private sealed class Release(Awake owner) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (!_done)
                    owner.Held--;
                _done = true;
            }
        }
    }

    [Fact]
    public void The_computer_is_kept_awake_while_the_session_works_but_not_while_its_paused()
    {
        var awake = new Awake();
        var session = new ExportSession("Exporting x.mp4", "Test", awake.Hold);
        Assert.Equal(1, awake.Held);
        session.Pause();
        session.Pause();
        Assert.True(session.IsPaused);
        Assert.Equal(0, awake.Held);
        session.Resume();
        session.Resume();
        Assert.False(session.IsPaused);
        Assert.Equal(1, awake.Held);
        session.Dispose();
        Assert.Equal(0, awake.Held);
        // Late clicks after the export ended do nothing.
        session.Resume();
        session.Cancel();
        Assert.Equal(0, awake.Held);
    }

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(0.2, "1s")]
    [InlineData(185, "3m 05s")]
    [InlineData(3720, "1h 02m")]
    public void Durations_are_short(double seconds, string expected) =>
        Assert.Equal(expected, ExportText.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Progress_reads_the_same_everywhere()
    {
        var p = new ExportProgress(0.4, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10), 1.5, TimeSpan.FromSeconds(130), "Encoding");
        Assert.Equal("40% · Encoding · 2m 10s left · 1.5×", ExportText.Progress(p, paused: false, preparing: null));
        Assert.Equal("40% · 2m 10s left · 1.5×", ExportText.Progress(p, paused: false, preparing: null, showStep: false));
        Assert.Equal("Paused · 40% · Encoding", ExportText.Progress(p, paused: true, preparing: "Preparing…"));
        Assert.Equal("Paused · Finding keyframes…", ExportText.Progress(null, paused: true, preparing: "Finding keyframes…"));
        Assert.Equal("", ExportText.Progress(null, paused: false, preparing: null));
    }

    [FfmpegFact]
    public async Task Each_file_is_converted_and_one_that_fails_doesnt_stop_the_others()
    {
        var dir = Directory.CreateTempSubdirectory("mane-convert").FullName;
        try
        {
            var video = Path.Combine(dir, "clip.mp4");
            File.Copy(media.Sync25, video);
            using var session = new ExportSession("Converting", "Test", _ => new MemoryStream());
            var statuses = new List<ConversionStatus>();
            var report = await FileConversion.ConvertAsync([media.Tone, video], Preset.ExtractAudio, FfmpegFixture.Tools!,
                new KeyframeService(FfmpegFixture.Tools!), session, new Recorder(statuses));
            Assert.Equal([Path.Combine(dir, "clip.m4a")], report.Outputs);
            var failure = Assert.Single(report.Failures);
            Assert.StartsWith(Path.GetFileName(media.Tone) + ": ", failure);
            Assert.Contains("no video", failure);
            Assert.Contains(statuses, s => s.Index == 1 && s.Output == report.Outputs[0] && s.Progress is not null);
            Assert.Equal(1, statuses[^1].Fraction, 3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task A_paused_conversion_waits_before_ffmpeg_starts_and_a_cancelled_one_saves_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("mane-convert").FullName;
        try
        {
            var video = Path.Combine(dir, "clip.mp4");
            File.Copy(media.Sync25, video);
            var output = Path.Combine(dir, "clip.m4a");
            using (var session = new ExportSession("Converting", "Test", _ => new MemoryStream()))
            {
                session.Pause();
                var converting = FileConversion.ConvertAsync([video], Preset.ExtractAudio, FfmpegFixture.Tools!,
                    new KeyframeService(FfmpegFixture.Tools!), session, null);
                await Task.Delay(1500);
                Assert.False(converting.IsCompleted);
                // Only the empty file that claims the name.
                Assert.Equal(0, new FileInfo(output).Length);
                session.Resume();
                Assert.Equal([output], (await converting).Outputs);
            }
            File.Delete(output);

            using (var session = new ExportSession("Converting", "Test", _ => new MemoryStream()))
            {
                session.Pause();
                var converting = FileConversion.ConvertAsync([video, video], Preset.ExtractAudio, FfmpegFixture.Tools!,
                    new KeyframeService(FfmpegFixture.Tools!), session, null);
                await Task.Delay(500);
                session.Cancel();
                var report = await converting;
                Assert.Empty(report.Outputs);
                Assert.Empty(report.Failures);
                Assert.Equal(["clip.mp4"], Directory.GetFiles(dir).Select(Path.GetFileName));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Recorder(List<ConversionStatus> statuses) : IProgress<ConversionStatus>
    {
        public void Report(ConversionStatus value)
        {
            lock (statuses)
                statuses.Add(value);
        }
    }
}
