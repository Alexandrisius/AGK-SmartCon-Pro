using System.Windows;
using Microsoft.Xaml.Behaviors;
using SmartCon.Core.Logging;
using Image = System.Windows.Controls.Image;
using BitmapImage = System.Windows.Media.Imaging.BitmapImage;
using CroppedBitmap = System.Windows.Media.Imaging.CroppedBitmap;
using BitmapCacheOption = System.Windows.Media.Imaging.BitmapCacheOption;
using BitmapCreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions;
using Int32Rect = System.Windows.Int32Rect;
using Path = System.IO.Path;

namespace SmartCon.FamilyManager.Behaviors;

/// <summary>
/// Live preview of the crop dialog (issue #131 rev 6): sets the attached Image's
/// <see cref="Image.Source"/> to a <see cref="CroppedBitmap"/> of exactly the current
/// selection — a source-level crop identical to what the renderer
/// (WpfAvatarCropService) produces for avatar.png, so the preview always matches
/// the final avatar pixel-for-pixel. The base image decode is cached per path and
/// capped at 2048px wide; the selection rect arrives in native source pixels and is
/// rescaled into decoded pixel space.
/// Layout/hard clips (ClipToBounds, UIElement.Clip) were evaluated and rejected —
/// see ADR-047 rev 6.
/// </summary>
public sealed class PreviewCropImageBehavior : Behavior<Image>
{
    private const int MaxDecodeWidth = 2048;
    private const int MaxCacheEntries = 16;

    private static readonly Dictionary<string, BitmapImage> BaseImageCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty ImagePathProperty =
        DependencyProperty.Register(nameof(ImagePath), typeof(string), typeof(PreviewCropImageBehavior),
            new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty NativeWidthProperty =
        DependencyProperty.Register(nameof(NativeWidth), typeof(int), typeof(PreviewCropImageBehavior),
            new PropertyMetadata(0, OnInputChanged));

    public static readonly DependencyProperty NativeHeightProperty =
        DependencyProperty.Register(nameof(NativeHeight), typeof(int), typeof(PreviewCropImageBehavior),
            new PropertyMetadata(0, OnInputChanged));

    public static readonly DependencyProperty RectXProperty =
        DependencyProperty.Register(nameof(RectX), typeof(double), typeof(PreviewCropImageBehavior),
            new PropertyMetadata(0.0, OnInputChanged));

    public static readonly DependencyProperty RectYProperty =
        DependencyProperty.Register(nameof(RectY), typeof(double), typeof(PreviewCropImageBehavior),
            new PropertyMetadata(0.0, OnInputChanged));

    public static readonly DependencyProperty RectWidthProperty =
        DependencyProperty.Register(nameof(RectWidth), typeof(double), typeof(PreviewCropImageBehavior),
            new PropertyMetadata(0.0, OnInputChanged));

    public static readonly DependencyProperty RectHeightProperty =
        DependencyProperty.Register(nameof(RectHeight), typeof(double), typeof(PreviewCropImageBehavior),
            new PropertyMetadata(0.0, OnInputChanged));

    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    public int NativeWidth
    {
        get => (int)GetValue(NativeWidthProperty);
        set => SetValue(NativeWidthProperty, value);
    }

    public int NativeHeight
    {
        get => (int)GetValue(NativeHeightProperty);
        set => SetValue(NativeHeightProperty, value);
    }

    public double RectX
    {
        get => (double)GetValue(RectXProperty);
        set => SetValue(RectXProperty, value);
    }

    public double RectY
    {
        get => (double)GetValue(RectYProperty);
        set => SetValue(RectYProperty, value);
    }

    public double RectWidth
    {
        get => (double)GetValue(RectWidthProperty);
        set => SetValue(RectWidthProperty, value);
    }

    public double RectHeight
    {
        get => (double)GetValue(RectHeightProperty);
        set => SetValue(RectHeightProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        UpdateSource();
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PreviewCropImageBehavior)d).UpdateSource();

    private void UpdateSource()
    {
        if (AssociatedObject is null) return;

        var path = ImagePath;
        if (string.IsNullOrWhiteSpace(path) || NativeWidth <= 0 || NativeHeight <= 0
            || RectWidth <= 0 || RectHeight <= 0)
        {
            AssociatedObject.Source = null;
            return;
        }

        var baseImage = GetBaseImage(path!, NativeWidth);
        if (baseImage is null)
        {
            AssociatedObject.Source = null;
            return;
        }

        var ratio = (double)baseImage.PixelWidth / NativeWidth;
        var x = Clamp((int)Math.Round(RectX * ratio), 0, baseImage.PixelWidth - 1);
        var y = Clamp((int)Math.Round(RectY * ratio), 0, baseImage.PixelHeight - 1);
        var w = Clamp((int)Math.Round(RectWidth * ratio), 1, baseImage.PixelWidth - x);
        var h = Clamp((int)Math.Round(RectHeight * ratio), 1, baseImage.PixelHeight - y);

        var cropped = new CroppedBitmap(baseImage, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        AssociatedObject.Source = cropped;
    }

    private static BitmapImage? GetBaseImage(string path, int nativeWidth)
    {
        if (BaseImageCache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (nativeWidth > MaxDecodeWidth)
                bitmap.DecodePixelWidth = MaxDecodeWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            if (BaseImageCache.Count >= MaxCacheEntries)
                BaseImageCache.Clear();
            BaseImageCache[path] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"PreviewCropImageBehavior: failed to decode '{Path.GetFileName(path)}': {ex.Message} [Action: проверьте формат файла]");
            return null;
        }
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}
