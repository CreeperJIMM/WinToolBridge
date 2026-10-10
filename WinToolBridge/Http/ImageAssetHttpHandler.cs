using System.Net;
using WinToolBridge.Services;

namespace WinToolBridge.Http;

/// <summary>Serves registered PNGs only; never maps request paths to filesystem files.</summary>
internal sealed class ImageAssetHttpHandler(ImageAssetService images)
{
    public async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        const string prefix = "/assets/icons/";
        var path = context.Request.Url?.AbsolutePath;
        if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal) ||
            path.Length != prefix.Length + 64 + 4 || !path.EndsWith(".png", StringComparison.Ordinal)) {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        var id = path.Substring(prefix.Length, 64);
        if (id.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f'))) {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        if (context.Request.HttpMethod is not ("GET" or "HEAD")) {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.Headers["Allow"] = "GET, HEAD";
            return;
        }
        if (!images.TryGet(id, out var asset)) {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        response.ContentType = asset.ContentType;
        response.Headers["ETag"] = asset.ETag;
        response.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
        response.Headers["Vary"] = "Origin";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        var matches = context.Request.Headers["If-None-Match"]?.Split(',').Any(value => {
            var tag = value.Trim();
            return tag == "*" || tag == asset.ETag || tag == "W/" + asset.ETag;
        }) == true;
        if (matches) {
            response.StatusCode = (int)HttpStatusCode.NotModified;
            return;
        }
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentLength64 = asset.ContentLength;
        if (context.Request.HttpMethod == "HEAD") return;
        using var content = asset.OpenRead();
        await content.CopyToAsync(response.OutputStream, token);
    }
}
