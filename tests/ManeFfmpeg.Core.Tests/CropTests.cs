using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Tests;

/// <summary>The output crop: its pixel math, how each export mode applies it, and how projects keep it.</summary>
public class CropTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Pixels_are_even_and_inside_the_frame()
    {
        Assert.Equal(new PixelCrop(480, 0, 960, 1080), new CropRect(0.25, 0, 0.5, 1).ToPixels(1920, 1080));
        // 0.3333 × 1920 = 639.9 → 640; the right edge 0.6667 × 1920 = 1280.1 → 1280.
        Assert.Equal(new PixelCrop(640, 360, 640, 360), new CropRect(1 / 3.0, 1 / 3.0, 1 / 3.0, 1 / 3.0).ToPixels(1920, 1080));
        // An odd frame: everything stays even and inside the even part of it.
        var odd = new CropRect(0.5, 0.5, 0.5, 0.5).ToPixels(1279, 719);
        Assert.True(odd.X % 2 == 0 && odd.Y % 2 == 0 && odd.Width % 2 == 0 && odd.Height % 2 == 0, odd.ToString());
        Assert.True(odd.X + odd.Width <= 1278 && odd.Y + odd.Height <= 718, odd.ToString());
        // A sliver still keeps two pixels.
        Assert.Equal(2, new CropRect(0.999, 0, 0.0001, 1).ToPixels(100, 100).Width);
        Assert.Equal("crop=960:1080:480:0", new PixelCrop(480, 0, 960, 1080).Filter);
    }

    [Fact]
    public void Clamping_keeps_the_crop_inside_the_frame()
    {
        Assert.Equal(new CropRect(0.5, 0, 0.5, 1), new CropRect(0.7, -0.2, 0.5, 1.5).Clamped());
        Assert.Equal(CropRect.MinSize, new CropRect(0.5, 0.5, 0, 0).Clamped()!.Width);
        Assert.Null(new CropRect(double.NaN, 0, 1, 1).Clamped());
        Assert.True(new CropRect(0, 0, 1, 1).IsFull);
        Assert.False(new CropRect(0, 0, 0.99, 1).IsFull);
    }

    [Fact]
    public void Reshaping_keeps_the_center_and_fits_the_frame()
    {
        const double wide = 16.0 / 9;
        // The whole frame becomes the largest crop of that shape, centered.
        var square = CropRect.Full.WithAspect(1, wide);
        Assert.Equal(0.5625, square.Width, 6);
        Assert.Equal(1, square.Height, 6);
        Assert.Equal(0.21875, square.X, 6);
        Assert.Equal(1, square.Width / square.Height * wide, 6);
        // A small crop near a corner keeps about its area and stays inside.
        var corner = new CropRect(0, 0, 0.2, 0.2).WithAspect(9.0 / 16, wide);
        Assert.Equal(9.0 / 16, corner.Width / corner.Height * wide, 6);
        Assert.Equal(0.04, corner.Width * corner.Height, 6);
        Assert.True(corner.X >= 0 && corner.Y >= 0);
    }

    [Fact]
    public void A_crop_of_the_whole_frame_is_no_crop()
    {
        var state = ProjectState.Empty;
        Assert.Null(state.WithCrop(CropRect.Full).Crop);
        Assert.Null(state.WithCrop(new CropRect(-0.1, -0.1, 1.3, 1.3)).Crop);
        var cropped = state.WithCrop(new CropRect(0.1, 0.1, 0.5, 0.5));
        Assert.NotNull(cropped.Crop);
        Assert.NotEqual(state, cropped);
        Assert.Equal(cropped, state.WithCrop(new CropRect(0.1, 0.1, 0.5, 0.5)));
    }

    // ---- Export ------------------------------------------------------------------------------------

    private static MediaInfo Media(int width = 1920, int height = 1080, int rotation = 0, Rational? sar = null) => new()
    {
        Duration = S(10), FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
        Video = new VideoStreamInfo
        {
            Index = 0, Codec = "h264", Width = width, Height = height, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p", Rotation = rotation,
            SampleAspectRatio = sar ?? new Rational(1, 1),
        },
        AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2, ChannelLayout = "stereo", Duration = S(10) }],
    };

    private static readonly Preset Encode = new() { Id = "e", Name = "encode", Arguments = "-c:v libx264 -crf 20 -c:a copy", Extension = "mp4" };

    private static readonly CropRect LeftHalf = new(0, 0, 0.5, 1);

    private static ExportJob Job(Preset preset, IReadOnlyDictionary<string, MediaInfo> media, CropRect? crop, params Clip[] clips) => new()
    {
        State = new ProjectState([.. media.Keys.Select(id => new SourceMedia { Id = id, Path = $"/m/{id}.src" })], new Timeline([.. clips])).WithCrop(crop),
        Media = media,
        Preset = preset,
        OutputPath = "/out/edit.mp4",
        WorkDirectory = "/work",
        Keyframes = media.Keys.ToDictionary(id => id, _ => new KeyframeIndex([.. Enumerable.Range(0, 10).Select(i => S(i))], TimeSpan.Zero)),
    };

    private static string Command(ExportPlan plan) => plan.CommandText("ffmpeg");

    [Fact]
    public void A_whole_file_is_cropped_by_the_preset_run()
    {
        var plan = ExportPlanner.Plan(Job(Encode, new Dictionary<string, MediaInfo> { ["a"] = Media() }, LeftHalf, Clip.Create("a", S(0), S(10))));
        Assert.Equal(ExportMode.Passthrough, plan.Mode);
        Assert.Contains("-vf crop=960:1080:0:0 -c:v libx264", Command(plan));
        Assert.Contains(plan.Notes, n => n.Contains("960×1080"));
    }

    [Fact]
    public void The_crop_comes_before_the_preset_filter()
    {
        var preset = Encode with { Arguments = "-vf scale=640:-2 -c:v libx264 -c:a copy" };
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media() };
        var single = ExportPlanner.Plan(Job(preset, media, LeftHalf, Clip.Create("a", S(1), S(4))));
        Assert.Equal(ExportMode.SingleClipAudioCopy, single.Mode);
        Assert.Contains("-vf crop=960:1080:0:0,scale=640:-2", Command(single));

        var joined = ExportPlanner.Plan(Job(preset, media, LeftHalf, Clip.Create("a", S(1), S(3)), Clip.Create("a", S(5), S(6))));
        Assert.Equal(ExportMode.Encode, joined.Mode);
        Assert.Contains("concat=n=2:v=1:a=1[vcat][aout];[vcat]crop=960:1080:0:0,scale=640:-2[vout]", Command(joined));
    }

    [Fact]
    public void Joined_clips_are_cropped_after_being_fitted_to_the_first()
    {
        // The second clip is scaled and padded to the first's 1280×720 frame, then the frame is cropped.
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media(1280, 720), ["b"] = Media(1920, 1080) };
        var plan = ExportPlanner.Plan(Job(Encode, media, LeftHalf, Clip.Create("a", S(1), S(3)), Clip.Create("b", S(1), S(3))));
        Assert.Contains("scale=1280:720", Command(plan));
        Assert.Contains("[vcat]crop=640:720:0:0[vout]", Command(plan));
    }

    [Fact]
    public void A_portrait_phone_video_is_cropped_upright()
    {
        // Stored 1920×1080 with a 90° display matrix: filters see it 1080×1920.
        var plan = ExportPlanner.Plan(Job(Encode, new Dictionary<string, MediaInfo> { ["a"] = Media(rotation: 90) },
            new CropRect(0, 0.25, 1, 0.5), Clip.Create("a", S(0), S(10))));
        Assert.Contains("crop=1080:960:0:480", Command(plan));
    }

    [Theory]
    [InlineData("anamorphic")]
    [InlineData("odd")]
    [InlineData("portrait")]
    [InlineData("mixed")]
    [InlineData("mixed anamorphic")]
    public void The_frame_the_preview_shows_is_the_frame_the_export_crops(string timeline)
    {
        var media = timeline switch
        {
            "anamorphic" => new Dictionary<string, MediaInfo> { ["a"] = Media(1440, 1080, sar: new Rational(4, 3)) },
            "odd" => new Dictionary<string, MediaInfo> { ["a"] = Media(1279, 719) },
            "portrait" => new Dictionary<string, MediaInfo> { ["a"] = Media(rotation: 90) },
            "mixed" => new Dictionary<string, MediaInfo> { ["a"] = Media(1280, 720), ["b"] = Media(640, 480) },
            _ => new Dictionary<string, MediaInfo> { ["a"] = Media(1440, 1080, sar: new Rational(4, 3)), ["b"] = Media() },
        };
        var clips = media.Keys.Select(id => Clip.Create(id, S(1), S(3))).ToArray();
        var frame = OutputFrame.Of([.. media.Values.Select(m => m.Video!)]);
        var crop = new CropRect(0.1, 0.2, 0.5, 0.6);
        var plan = ExportPlanner.Plan(Job(Encode, media, crop, clips));
        Assert.Contains(frame.Pixels(crop).Filter, Command(plan));
        Assert.Contains(plan.Notes, n => n.Contains($"of the {frame.Width}×{frame.Height} frame"));
    }

    [Fact]
    public void An_anamorphic_frame_is_cropped_in_its_stored_pixels()
    {
        // 1440×1080 shown as 1920×1080: alone it is cropped as stored, joined with a clip of another shape it is
        // first scaled to its display size.
        var anamorphic = Media(1440, 1080, sar: new Rational(4, 3)).Video!;
        Assert.Equal(new OutputFrame(1440, 1080, 16.0 / 9, false), OutputFrame.Of([anamorphic]));
        Assert.Equal(new OutputFrame(1920, 1080, 16.0 / 9, true), OutputFrame.Of([anamorphic, Media(1280, 720).Video!]));
        Assert.Equal(new PixelCrop(0, 0, 720, 1080), OutputFrame.Of([anamorphic]).Pixels(LeftHalf));
    }

    [Fact]
    public void Stream_copy_refuses_a_crop()
    {
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media() };
        var ex = Assert.Throws<ExportPlanException>(() => ExportPlanner.Plan(Job(Preset.PlaneCopy, media, LeftHalf, Clip.Create("a", S(0), S(10)))));
        Assert.Contains("can't be cropped", ex.Message);
        // Without a crop the same edit still copies.
        Assert.Equal(ExportMode.Passthrough, ExportPlanner.Plan(Job(Preset.PlaneCopy, media, null, Clip.Create("a", S(0), S(10)))).Mode);
    }

    [Fact]
    public void No_crop_leaves_the_commands_as_they_were()
    {
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media() };
        var plan = ExportPlanner.Plan(Job(Encode, media, null, Clip.Create("a", S(1), S(3)), Clip.Create("a", S(5), S(6))));
        Assert.DoesNotContain("crop", Command(plan));
        Assert.Contains("concat=n=2:v=1:a=1[vout][aout]", Command(plan));
    }

    // ---- Project files -----------------------------------------------------------------------------

    [Fact]
    public void Projects_keep_the_crop_and_say_they_need_a_newer_app()
    {
        var state = new ProjectState([new SourceMedia { Id = "s", Path = "/m/a.mp4" }], new Timeline([Clip.Create("s", S(0), S(2))]))
            .WithCrop(new CropRect(0.125, 0.25, 0.5, 0.625));
        var json = ProjectFile.Serialize(new Project { State = state }, "/p/x.mfvp");
        Assert.Contains("\"version\": 3", json);
        Assert.Contains("\"crop\"", json);
        Assert.Equal(state.Crop, ProjectFile.Deserialize(json, "/p/x.mfvp").State.Crop);

        // Without a crop nothing is written and the version stays as before.
        var plain = ProjectFile.Serialize(new Project { State = state.WithCrop(null) }, "/p/x.mfvp");
        Assert.Contains("\"version\": 1", plain);
        Assert.DoesNotContain("\"crop\"", plain);
    }

    [Fact]
    public void A_hand_edited_crop_is_clamped_or_dropped()
    {
        const string header = "\"format\": \"mane-ffmpeg-project\", \"version\": 3";
        var outside = ProjectFile.Deserialize($"{{ {header}, \"crop\": {{ \"x\": 0.8, \"y\": 0, \"width\": 0.5, \"height\": 1 }} }}", "/p/x.mfvp");
        Assert.Equal(new CropRect(0.5, 0, 0.5, 1), outside.State.Crop);
        var whole = ProjectFile.Deserialize($"{{ {header}, \"crop\": {{ \"x\": 0 }} }}", "/p/x.mfvp");
        Assert.Null(whole.State.Crop);
    }
}
