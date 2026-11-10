namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 落盘前对整个文件字节做的变换（压缩、加密等）。Decode 遇到非法数据必须抛异常，用于触发备份恢复。
    /// 异步落盘时会在后台线程调用，实现不要访问 Unity API。
    /// </summary>
    public interface IStorageTransform
    {
        byte[] Encode(byte[] data);
        byte[] Decode(byte[] data);
    }
}
