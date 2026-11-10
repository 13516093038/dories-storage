using System.Collections.Generic;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 存储介质：按名字读写整块字节。实现它即可接入新平台（如小游戏文件系统、云存档）。
    /// Write 应尽量原子（写一半崩溃不能留下半个文件）；IO 失败直接抛异常，由 StorageEntity 处理。
    /// </summary>
    public interface IStorageProvider
    {
        /// <summary>
        /// Read/Write 能否在后台线程调用，为 true 时 FlushAsync/PreloadAsync 才会真正异步。
        /// StorageEntity 保证在此之前已在主线程调用过本对象的方法，只能在主线程做的初始化可以放在那时。
        /// </summary>
        bool IsThreadSafe { get; }

        bool Exists(string name);

        /// <summary>
        /// 不存在返回 null。
        /// </summary>
        byte[] Read(string name);

        void Write(string name, byte[] data);
        void Delete(string name);

        /// <summary>所有已存储的名字。</summary>
        IEnumerable<string> GetNames();
    }
}
