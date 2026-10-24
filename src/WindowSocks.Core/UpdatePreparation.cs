using System.Text.Json;

namespace WindowSocks.Core;

public static class UpdatePreparation
{
    public static void Validate(UpdateDownload download, string version)
    {
        var release = AppReleasePackage.Read(JsonSerializer.Serialize(download.Release, Json.Options));
        if (release.Version != version || (download.Source is null) == (download.LocalArchive is null)) throw new InvalidDataException("更新下載工作無效。");
        if (download.Source is { } source) _ = ReleaseClient.AssetUri(ReleaseClient.SourceUri(source), release.Portable.Url);
        else {
            if (!Path.IsPathFullyQualified(download.LocalArchive!)) throw new InvalidDataException("離線更新路徑無效。");
            AppReleasePackage.RejectLinks(download.LocalArchive!);
        }
    }
    public static async Task<string> PrepareAsync(UpdateDownload download, string version, string work,
        Func<CancellationToken, Task> interruptProxy, IProgress<UpdateProgress>? progress, CancellationToken ct, ReleaseClient? client = null)
    {
        Validate(download, version); AppReleasePackage.RejectLinks(work);
        ct.ThrowIfCancellationRequested();
        // This must complete before opening a remote download or copying an offline archive.
        await interruptProxy(ct); ct.ThrowIfCancellationRequested();
        string archive = Path.Combine(work, "update.zip"), payload = Path.Combine(work, "payload");
        if (download.LocalArchive is { } local) {
            progress?.Report(new("複製並驗證離線更新…") { CanCancel = true });
            await using (var input = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, true))
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
                await input.CopyToAsync(output, 256 * 1024, ct);
            AppReleasePackage.VerifyArchive(archive, download.Release.Portable);
        } else {
            progress?.Report(new("下載更新…") { CanCancel = true });
            bool ownsClient = client is null; client ??= new ReleaseClient();
            try { await client.DownloadAsync(ReleaseClient.SourceUri(download.Source!), download.Release, archive, progress, ct); }
            finally { if (ownsClient) client.Dispose(); }
        }
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => AppReleasePackage.Extract(archive, payload, version, ct, progress), ct);
        return payload;
    }
}
