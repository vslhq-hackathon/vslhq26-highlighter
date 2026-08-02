using Highlighter.Api.Contracts;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Highlighter.Api.Services;

/// <summary>Rasterizes caption lines and text overlays to transparent PNGs for
/// ffmpeg's overlay filter (this machine's ffmpeg ships without libass or
/// drawtext). Styles mirror the studio preview: boxed (opaque dark box),
/// plain (soft shadow), karaoke (spoken words flip to the accent lime).</summary>
public static class CaptionRasterizer
{
    private static readonly Color Ink = Color.FromRgba(20, 19, 18, 242);
    private static readonly Color Fg = Color.FromRgb(237, 234, 226);
    private static readonly Color SoftGrey = Color.FromRgb(186, 196, 201);
    private static readonly Color Accent = Color.FromRgb(217, 242, 75);
    private static readonly Color Shadow = Color.FromRgba(10, 9, 8, 200);

    private static readonly Lazy<FontFamily> Family = new(() =>
    {
        foreach (var name in new[] { "Arial", "Helvetica Neue", "Helvetica", "Verdana" })
            if (SystemFonts.Collection.TryGet(name, out var family)) return family;
        return SystemFonts.Collection.Families.First();
    });

    public static Font CaptionFont(int videoHeight) =>
        Family.Value.CreateFont(Math.Max(20, videoHeight * 0.034f), FontStyle.Bold);

    /// <summary>One caption line. For karaoke, highlightWords > 0 paints that many
    /// leading words in the accent colour (a "state" of the sweep).</summary>
    public static byte[] RenderCaption(
        string text, string style, int videoWidth, int videoHeight,
        IReadOnlyList<string>? words = null, int highlightWords = 0)
    {
        var font = CaptionFont(videoHeight);
        var maxWidth = videoWidth * 0.84f;
        var options = new RichTextOptions(font)
        {
            WrappingLength = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Origin = new SixLabors.ImageSharp.PointF(videoWidth / 2f, 0),
        };
        var bounds = TextMeasurer.MeasureAdvance(text, options);
        var padX = font.Size * 0.45f;
        var padY = font.Size * 0.28f;
        var height = (int)Math.Ceiling(bounds.Height + padY * 2 + font.Size * 0.35f);

        using var image = new Image<Rgba32>(videoWidth, height);
        image.Mutate(context =>
        {
            var textOrigin = new SixLabors.ImageSharp.PointF(videoWidth / 2f, padY);
            var textOptions = new RichTextOptions(font)
            {
                WrappingLength = maxWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Origin = textOrigin,
            };

            if (style == "boxed")
            {
                var box = new SixLabors.ImageSharp.Drawing.RectangularPolygon(
                    videoWidth / 2f - bounds.Width / 2f - padX, 0,
                    bounds.Width + padX * 2, bounds.Height + padY * 2);
                context.Fill(Ink, box);
                context.DrawText(textOptions, text, Fg);
                return;
            }

            // Soft shadow for the unboxed styles.
            var shadowOptions = new RichTextOptions(font)
            {
                WrappingLength = maxWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Origin = new SixLabors.ImageSharp.PointF(videoWidth / 2f + 2, padY + 3),
            };
            context.DrawText(shadowOptions, text, Shadow);

            if (style == "karaoke" && words is { Count: > 0 })
            {
                // Single-line sweep: draw word by word, measuring cumulative
                // offsets. Per-word layout can't wrap, so an over-long line is
                // shrunk to fit rather than running off both edges.
                var line = string.Join(' ', words);
                var lineWidth = TextMeasurer.MeasureAdvance(line, new TextOptions(font)).Width;
                var wordFont = lineWidth > maxWidth
                    ? Family.Value.CreateFont(font.Size * maxWidth / lineWidth, FontStyle.Bold)
                    : font;
                if (lineWidth > maxWidth)
                    lineWidth = TextMeasurer.MeasureAdvance(line, new TextOptions(wordFont)).Width;
                var x = videoWidth / 2f - lineWidth / 2f;
                for (var i = 0; i < words.Count; i++)
                {
                    var wordText = words[i];
                    var colour = i < highlightWords ? Accent : SoftGrey;
                    context.DrawText(new RichTextOptions(wordFont)
                    {
                        Origin = new SixLabors.ImageSharp.PointF(x, padY),
                    }, wordText, colour);
                    x += TextMeasurer.MeasureAdvance(wordText + " ", new TextOptions(wordFont)).Width;
                }
            }
            else
            {
                context.DrawText(textOptions, text, Fg);
            }
        });

        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    /// <summary>A standalone text overlay (the editor's Text tool): bold white
    /// with an outline-ish shadow, centered.</summary>
    public static byte[] RenderTextOverlay(string text, int videoWidth, int videoHeight)
    {
        var font = Family.Value.CreateFont(Math.Max(22, videoHeight * 0.042f), FontStyle.Bold);
        var maxWidth = videoWidth * 0.9f;
        var measure = TextMeasurer.MeasureAdvance(text, new RichTextOptions(font)
        {
            WrappingLength = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Origin = new SixLabors.ImageSharp.PointF(videoWidth / 2f, 0),
        });
        var height = (int)Math.Ceiling(measure.Height + font.Size * 0.6f);

        using var image = new Image<Rgba32>(videoWidth, height);
        image.Mutate(context =>
        {
            foreach (var (dx, dy) in new[] { (-2f, 0f), (2f, 0f), (0f, -2f), (0f, 3f) })
            {
                context.DrawText(new RichTextOptions(font)
                {
                    WrappingLength = maxWidth,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Origin = new SixLabors.ImageSharp.PointF(videoWidth / 2f + dx, font.Size * 0.3f + dy),
                }, text, Shadow);
            }
            context.DrawText(new RichTextOptions(font)
            {
                WrappingLength = maxWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Origin = new SixLabors.ImageSharp.PointF(videoWidth / 2f, font.Size * 0.3f),
            }, text, Fg);
        });

        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }
}
