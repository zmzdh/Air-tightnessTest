using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace LabelTransPrint.Services;

public static class WpfBitmapConverter
{
    public static BitmapImage? ToBitmapImage(Bitmap? bitmap)
    {
        if (bitmap == null)
            return null;

        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static BitmapImage? FromPngBytes(byte[]? pngBytes)
    {
        if (pngBytes == null || pngBytes.Length == 0)
            return null;

        using var ms = new MemoryStream(pngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static BitmapImage? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
