using System.Drawing;
using ZXing;
using ZXing.Common;

namespace LabelTransPrint.Services;

public static class BarcodeBitmapService
{
    public static Bitmap? GenerateCode128(string content, int width, int height)
    {
        try
        {
            var writer = new BarcodeWriterPixelData
            {
                Format = BarcodeFormat.CODE_128,
                Options = new EncodingOptions
                {
                    Width = Math.Max(1, width),
                    Height = Math.Max(1, height),
                    Margin = 1,
                    PureBarcode = true
                }
            };
            var pixelData = writer.Write(content);
            var bitmap = new Bitmap(pixelData.Width, pixelData.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, pixelData.Width, pixelData.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);

            try
            {
                System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
