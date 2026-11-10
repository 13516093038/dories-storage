namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 挂在带 <see cref="StorageAutoSave"/> 的对象上的组件实现它，即可随对象一起自动存取自己的状态。
    /// key 对每个对象的每种组件唯一，直接用 storage.Save(key, 状态, file) / storage.TryLoad(key, out 状态, file)。
    /// </summary>
    public interface IStorageAutoSaveState
    {
        void SaveState(StorageEntity storage, string key, string file);
        void LoadState(StorageEntity storage, string key, string file);
    }
}
