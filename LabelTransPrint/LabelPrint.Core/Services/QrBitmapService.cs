using System.Drawing;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace LabelTransPrint.Services;

public static class QrBitmapService
{
    public static Bitmap? GenerateQRCode(string content, int size, string? errorCorrection = null, int? margin = null)
    {
        try
        {
            var writer = new BarcodeWriterPixelData
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new QrCodeEncodingOptions
                {
                    Width = size,
                    Height = size,
                    Margin = Math.Max(0, margin ?? 1),
                    ErrorCorrection = errorCorrection?.Trim().ToUpperInvariant() switch
                    {
                        "M" => ErrorCorrectionLevel.M,
                        "Q" => ErrorCorrectionLevel.Q,
                        "H" => ErrorCorrectionLevel.H,
                        _ => ErrorCorrectionLevel.L
                    }
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
