using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Windows.Network;

public sealed class HttpNetworkPerformanceTester : INetworkPerformanceTester
{
    private const double DownloadTargetSeconds = 2.5;
    private const double UploadTargetSeconds = 2.0;

    public async ValueTask<NetworkPerformanceResult> RunAsync(NetworkPerformanceTestOptions options,
        IProgress<NetworkPerformanceProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var idleSamples = new List<double>();
        var downloadLoaded = new List<double>();
        var uploadLoaded = new List<double>();
        long downloadBytes = 0;
        long uploadBytes = 0;

        try
        {
            ValidateOptions(options);
            using var bandwidthClient = CreateClient();
            using var latencyClient = CreateClient();

            Report(progress, PerformanceTestPhase.Preparing, .02, "正在连接测速节点");
            _ = await MeasureLatencyOnceAsync(latencyClient, options.DownloadEndpoint, cancellationToken);

            Report(progress, PerformanceTestPhase.IdleLatency, .06, "测量空闲 HTTP 往返延迟");
            for (var index = 0; index < options.IdleLatencySamples; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                idleSamples.Add(await MeasureLatencyOnceAsync(latencyClient, options.DownloadEndpoint, cancellationToken));
                Report(progress, PerformanceTestPhase.IdleLatency, .06 + .12 * (index + 1) / options.IdleLatencySamples,
                    $"空闲延迟样本 {index + 1}/{options.IdleLatencySamples}");
                if (index + 1 < options.IdleLatencySamples) await Task.Delay(120, cancellationToken);
            }

            Report(progress, PerformanceTestPhase.Download, .20, "下载预热样本");
            var downloadWarmup = await DownloadAsync(bandwidthClient, options.DownloadEndpoint, options.DownloadWarmupBytes,
                (bytes, _) => Report(progress, PerformanceTestPhase.Download, .20, "下载预热样本", bytes), cancellationToken);
            downloadBytes += downloadWarmup.Bytes;
            var downloadTarget = AdaptiveBytes(downloadWarmup.MegabitsPerSecond, DownloadTargetSeconds,
                options.DownloadMinimumBytes, options.DownloadMaximumBytes);

            Report(progress, PerformanceTestPhase.Download, .24, $"下载测试 · {FormatBytes(downloadTarget)}");
            var downloadTask = DownloadAsync(bandwidthClient, options.DownloadEndpoint, downloadTarget,
                (bytes, total) => Report(progress, PerformanceTestPhase.Download, .24 + .34 * bytes / Math.Max(1d, total),
                    $"下载测试 · {FormatBytes(bytes)}/{FormatBytes(total)}", bytes), cancellationToken);
            await CollectLoadedLatencyAsync(downloadTask, latencyClient, options.DownloadEndpoint, downloadLoaded, cancellationToken);
            var download = await downloadTask;
            downloadBytes += download.Bytes;

            Report(progress, PerformanceTestPhase.Upload, .60, "上传预热样本", downloadBytes, download.MegabitsPerSecond);
            var uploadWarmup = await UploadAsync(bandwidthClient, options.UploadEndpoint, options.UploadWarmupBytes, cancellationToken);
            uploadBytes += uploadWarmup.Bytes;
            var uploadTarget = AdaptiveBytes(uploadWarmup.MegabitsPerSecond, UploadTargetSeconds,
                options.UploadMinimumBytes, options.UploadMaximumBytes);

            Report(progress, PerformanceTestPhase.Upload, .66, $"上传测试 · {FormatBytes(uploadTarget)}", downloadBytes, download.MegabitsPerSecond);
            var uploadTask = UploadAsync(bandwidthClient, options.UploadEndpoint, uploadTarget, cancellationToken);
            await CollectLoadedLatencyAsync(uploadTask, latencyClient, options.DownloadEndpoint, uploadLoaded, cancellationToken,
                phaseProgress: value => Report(progress, PerformanceTestPhase.Upload, .66 + value * .27,
                    $"上传测试 · {FormatBytes(uploadTarget)}", downloadBytes + uploadBytes, uploadWarmup.MegabitsPerSecond));
            var upload = await uploadTask;
            uploadBytes += upload.Bytes;

            Report(progress, PerformanceTestPhase.Finalizing, .96, "计算负载延迟与 Bufferbloat", downloadBytes + uploadBytes);
            var idleLatency = NetworkPerformanceMath.Percentile(idleSamples, .5);
            var idleJitter = NetworkPerformanceMath.AverageJitter(idleSamples);
            var downLoadedLatency = downloadLoaded.Count == 0 ? idleLatency : NetworkPerformanceMath.Percentile(downloadLoaded, .5);
            var upLoadedLatency = uploadLoaded.Count == 0 ? idleLatency : NetworkPerformanceMath.Percentile(uploadLoaded, .5);
            var delta = Math.Max(0, Math.Max(downLoadedLatency, upLoadedLatency) - idleLatency);
            var grade = NetworkPerformanceMath.BufferbloatGrade(delta);
            var summary = BuildSummary(download.MegabitsPerSecond, upload.MegabitsPerSecond, delta, grade);

            Report(progress, PerformanceTestPhase.Completed, 1, "测速完成", downloadBytes + uploadBytes);
            return new NetworkPerformanceResult(started, DateTimeOffset.Now, true, idleLatency, idleJitter,
                download.MegabitsPerSecond, upload.MegabitsPerSecond, downLoadedLatency, upLoadedLatency,
                delta, grade, summary, downloadBytes, uploadBytes, idleSamples.ToImmutableArray(),
                downloadLoaded.ToImmutableArray(), uploadLoaded.ToImmutableArray());
        }
        catch (OperationCanceledException)
        {
            return Failure(started, "测速已取消", downloadBytes, uploadBytes, idleSamples, downloadLoaded, uploadLoaded, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return Failure(started, "测速节点不可用或连接中断", downloadBytes, uploadBytes, idleSamples, downloadLoaded, uploadLoaded, false, ex.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetScope", "0.1.2"));
        return client;
    }

    private static async Task<double> MeasureLatencyOnceAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var timer = Stopwatch.StartNew();
        using var response = await client.GetAsync(DownloadUri(endpoint, 0), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(Stream.Null, timeout.Token);
        return timer.Elapsed.TotalMilliseconds;
    }

    private static async Task<TransferResult> DownloadAsync(HttpClient client, Uri endpoint, long requestedBytes,
        Action<long, long>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var timer = Stopwatch.StartNew();
        using var response = await client.GetAsync(DownloadUri(endpoint, requestedBytes), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long received = 0;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
                if (count == 0) break;
                received += count;
                progress?.Invoke(received, requestedBytes);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return new TransferResult(received, timer.Elapsed, NetworkPerformanceMath.ToMegabitsPerSecond(received, timer.Elapsed));
    }

    private static async Task<TransferResult> UploadAsync(HttpClient client, Uri endpoint, long requestedBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var payload = new byte[checked((int)requestedBytes)];
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var timer = Stopwatch.StartNew();
        using var response = await client.PostAsync(CacheBustedUri(endpoint), content, timeout.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(Stream.Null, timeout.Token);
        return new TransferResult(requestedBytes, timer.Elapsed, NetworkPerformanceMath.ToMegabitsPerSecond(requestedBytes, timer.Elapsed));
    }

    private static async Task CollectLoadedLatencyAsync(Task<TransferResult> transfer, HttpClient latencyClient, Uri latencyEndpoint,
        ICollection<double> samples, CancellationToken cancellationToken, Action<double>? phaseProgress = null)
    {
        var index = 0;
        while (!transfer.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { samples.Add(await MeasureLatencyOnceAsync(latencyClient, latencyEndpoint, cancellationToken)); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            index++;
            phaseProgress?.Invoke(Math.Min(.95, index / 12d));
            if (!transfer.IsCompleted) await Task.Delay(250, cancellationToken);
        }
    }

    private static long AdaptiveBytes(double megabitsPerSecond, double targetSeconds, long minimum, long maximum)
    {
        var estimated = (long)(Math.Max(.1, megabitsPerSecond) * 1_000_000d / 8d * targetSeconds);
        return Math.Clamp(estimated, minimum, maximum);
    }

    private static void ValidateOptions(NetworkPerformanceTestOptions options)
    {
        if (options.DownloadEndpoint.Scheme != Uri.UriSchemeHttps || options.UploadEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("测速端点必须使用 HTTPS");
        if (options.IdleLatencySamples is < 3 or > 30) throw new InvalidOperationException("延迟样本数量无效");
        if (options.DownloadMaximumBytes > 100_000_000 || options.UploadMaximumBytes > 25_000_000)
            throw new InvalidOperationException("测速流量超过安全上限");
    }

    private static Uri DownloadUri(Uri endpoint, long bytes) => AppendQuery(endpoint, $"bytes={bytes}&measId={Guid.NewGuid():N}");
    private static Uri CacheBustedUri(Uri endpoint) => AppendQuery(endpoint, $"measId={Guid.NewGuid():N}");
    private static Uri AppendQuery(Uri endpoint, string query) => new($"{endpoint}{(string.IsNullOrEmpty(endpoint.Query) ? "?" : "&")}{query}");
    private static string FormatBytes(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000d:0.#} MB" : $"{bytes / 1_000d:0} KB";

    private static string BuildSummary(double download, double upload, double delta, string grade)
    {
        if (grade is "D" or "F") return $"负载下延迟增加 {delta:0} ms，上传或下载占满时可能明显卡顿";
        if (download < 10) return $"到测速节点的下载速率为 {download:0.#} Mbps，可能限制高清视频或大文件下载";
        if (upload < 3) return $"到测速节点的上传速率为 {upload:0.#} Mbps，视频会议或云同步可能受限";
        return $"到测速节点的吞吐与负载延迟表现正常，Bufferbloat 等级 {grade}";
    }

    private static void Report(IProgress<NetworkPerformanceProgress>? progress, PerformanceTestPhase phase, double value,
        string message, long bytes = 0, double? speed = null) =>
        progress?.Report(new NetworkPerformanceProgress(phase, Math.Clamp(value, 0, 1), message, bytes, speed));

    private static NetworkPerformanceResult Failure(DateTimeOffset started, string summary, long downloadBytes, long uploadBytes,
        IEnumerable<double> idle, IEnumerable<double> downLoaded, IEnumerable<double> upLoaded, bool cancelled, string? error = null) =>
        new(started, DateTimeOffset.Now, false, 0, 0, 0, 0, 0, 0, 0, "—", summary, downloadBytes, uploadBytes,
            idle.ToImmutableArray(), downLoaded.ToImmutableArray(), upLoaded.ToImmutableArray(), error, cancelled);

    private sealed record TransferResult(long Bytes, TimeSpan Duration, double MegabitsPerSecond);
}
