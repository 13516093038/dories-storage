using System.Collections.Generic;
using System.IO;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 一个存储文件在内存里的内容：key → 已序列化的值。
    /// 二进制布局：魔数 "DRSF" + 格式版本(1 字节) + 数据版本 + 条目数 + [key, 长度, 字节]...
    /// </summary>
    internal sealed class StorageFile
    {
        private const uint Magic = 0x46535244; // "DRSF"

        public readonly Dictionary<string, byte[]> Entries = new();
        public bool Dirty;

        /// <summary>
        /// 业务数据版本，用于迁移（见 <see cref="IStorageMigration"/>）。
        /// </summary>
        public int DataVersion;

        public byte[] ToBytes()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic);
            writer.Write(DataVersion);
            writer.Write(Entries.Count);
            foreach (var pair in Entries)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value.Length);
                writer.Write(pair.Value);
            }

            writer.Flush();
            return stream.ToArray();
        }

        public static StorageFile FromBytes(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException("不是存储文件（魔数不匹配）");

            var file = new StorageFile { DataVersion = reader.ReadInt32() };
            for (int i = reader.ReadInt32(); i > 0; i--)
            {
                var key = reader.ReadString();
                int length = reader.ReadInt32();
                var value = reader.ReadBytes(length);
                if (value.Length != length)
                    throw new EndOfStreamException("存储文件被截断");
                file.Entries[key] = value;
            }

            return file;
        }
    }
}
