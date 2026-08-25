using System.Buffers;
using System.IO.Pipes;
using System.Text;

namespace NetScope.Windows.Ipc;

/// <summary>
/// 命名管道字节分帧：[4 字节小端长度][UTF-8 负载]。
/// 直接使用 PipeStream 的原始读写，避免 StreamReader/StreamWriter 在双向(InOut)管道上构造即挂起的问题。
/// </summary>
public static class PipeFrame
{
    private const int LengthBytes = 4;

    /// <summary>写入一个 JSON 帧。超时由调用方的取消令牌负责。</summary>
    public static ValueTask WriteAsync(PipeStream pipe, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > CollectorProtocol.MaxMessageBytes)
            throw new IOException($"消息过大: {bytes.Length} 字节，上限 {CollectorProtocol.MaxMessageBytes}");
        var header = BitConverter.GetBytes(bytes.Length);
        return WriteAsync(pipe, header, bytes, cancellationToken);
    }

    private static async ValueTask WriteAsync(PipeStream pipe, byte[] header, byte[] body, CancellationToken cancellationToken)
    {
        await pipe.WriteAsync(header.AsMemory(), cancellationToken);
        await pipe.WriteAsync(body.AsMemory(), cancellationToken);
    }

    /// <summary>
    /// 读取一帧。对端正常关闭(首个长度读取读到 0 字节)时返回 null；
    /// 帧被截断或超长时抛出 IOException，由调用方视为连接结束。
    /// </summary>
    public static async ValueTask<string?> ReadAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        var header = new byte[LengthBytes];
        var offset = 0;
        while (offset < LengthBytes)
        {
            var read = await pipe.ReadAsync(header.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                // 尚未读到任何长度字节即 EOF：对端干净关闭。
                if (offset == 0) return null;
                throw new IOException("管道在帧头部中途关闭");
            }
            offset += read;
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length < 0 || length > CollectorProtocol.MaxMessageBytes)
            throw new IOException($"非法的帧长度: {length}");

        var body = new byte[length];
        await pipe.ReadExactlyAsync(body.AsMemory(), cancellationToken);
        return Encoding.UTF8.GetString(body);
    }
}
