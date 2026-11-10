namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 存档数据升级的一步：把 <see cref="FromVersion"/> 版本的文件改成下一版。
    /// 文件的数据版本低于 StorageEntity 的 Data Version 时，在首次打开该文件时按 FromVersion 从小到大依次执行。
    /// </summary>
    public interface IStorageMigration
    {
        int FromVersion { get; }

        /// <summary>
        /// 用 storage 的常规接口（Load/Save/DeleteKey，file 参数原样传回）改写数据。抛异常则本次打开失败，文件不会被改动。
        /// </summary>
        void Migrate(StorageEntity storage, string file);
    }
}
