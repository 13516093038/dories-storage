using System;
using System.IO;
using System.IO.Compression;

namespace Dories.Storage.Runtime
{
    /// <summary>GZip 压缩。与加密同时使用时放在加密前面（密文无法压缩）。</summary>
    [Serializable]
    public class GzipTransform : IStorageTransform
    {
        public byte[] Encode(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
                gzip.Write(data, 0, data.Length);
            return output.ToArray();
        }

        public byte[] Decode(byte[] data)
        {
            using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
    }
}
