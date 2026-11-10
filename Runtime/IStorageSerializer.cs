namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 单个值与字节的互转。默认 <see cref="JsonStorageSerializer"/>，可换成 MemoryPack 等。
    /// </summary>
    public interface IStorageSerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(byte[] data);
    }
}
