using System;
using System.Collections.Generic;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 只存在内存里，进程结束即丢失。用于测试或不需要持久化的临时数据。
    /// </summary>
    [Serializable]
    public class MemoryStorageProvider : IStorageProvider
    {
        internal readonly Dictionary<string, byte[]> Data = new();

        public bool IsThreadSafe => false;

        public bool Exists(string name) => Data.ContainsKey(name);
        public byte[] Read(string name) => Data.TryGetValue(name, out var data) ? data : null;
        public void Write(string name, byte[] data) => Data[name] = data;
        public void Delete(string name) => Data.Remove(name);
        public IEnumerable<string> GetNames() => Data.Keys;
    }
}
