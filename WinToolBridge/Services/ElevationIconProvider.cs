using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinToolBridge.Services;

internal static class ElevationIconProvider
{
    internal static string? GetUrl(ImageAssetService? assets, string? assetBaseUrl, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (assets == null || assetBaseUrl == null) return null;
        try {
            using var icon = SystemIcons.GetStockIcon(StockIconId.Shield, StockIconOptions.SmallIcon);
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            token.ThrowIfCancellationRequested();
            return assetBaseUrl.TrimEnd('/') + assets.RegisterPng(png.GetBuffer().AsSpan(0, checked((int)png.Length)));
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException) {
            return null;
        }
    }
}
