using System;
using System.Collections.Generic;
using System.Linq;
using Dories.Logging;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 本地键值存储。数据按文件分组（file 参数，留空用默认文件；多存档槽用不同文件名即可）。
    /// Save 只改内存并标脏，按 <see cref="_flushInterval"/> 合并落盘，切后台/退出/销毁时立即落盘，也可手动 <see cref="Flush"/> / FlushAsync。
    /// 落盘管线：各 key 的值经 Serializer 序列化 → 打包 → 依次经过 Transforms（压缩/加密）→ Provider 写入。
    /// 仅限主线程使用。
    /// </summary>
    public partial class StorageEntity : MonoBehaviour
    {
        private const string Tag = "Storage";
        private const float RetryDelay = 5f;
        internal const string BackupSuffix = ".bak";
        internal const string CorruptSuffix = ".corrupt";

        [SerializeField] 
        private string _defaultFile = "save.dat";

        [SerializeField, Min(0f)]
        [Tooltip("数据变更后最多延迟多少秒落盘；0 表示当帧 LateUpdate 落盘")]
        private float _flushInterval = 1f;

        [SerializeField]
        [Tooltip("文件读取成功时把它存为备份（.bak）；之后文件损坏会自动回退到备份")]
        private bool _backupOnLoad = true;

        [SerializeField, Min(0)]
        [Tooltip("当前业务数据版本。打开版本更低的文件时依次执行 Migrations，然后把文件升到该版本")]
        private int _dataVersion;

        [SerializeReference]
        [Tooltip("存储介质；留空用 FileStorageProvider")]
        private IStorageProvider _provider;

        [SerializeReference]
        [Tooltip("值的序列化方式；留空用 JsonStorageSerializer")]
        private IStorageSerializer _serializer;

        [SerializeReference]
        [Tooltip("落盘前依次执行的变换，读取时逆序执行。压缩应排在加密前面")]
        private List<IStorageTransform> _transforms = new();

        [SerializeReference]
        [Tooltip("数据版本迁移步骤，见 Data Version")]
        private List<IStorageMigration> _migrations = new();

        private readonly Dictionary<string, StorageFile> _files = new();
        private bool _hasDirty;
        private float _nextFlushTime;

        /// <summary>
        /// 落盘失败（磁盘满、无权限等）时触发，参数为文件名和异常。失败的文件保持脏状态，稍后自动重试。
        /// </summary>
        public event Action<string, Exception> FlushFailed;

        /// <summary>
        /// 切后台/失焦/退出/销毁即将落盘前触发，在这里把运行时状态 Save 进来。
        /// </summary>
        public event Action Saving;

        /// <summary>
        /// Provider / Serializer / Transforms / Migrations / DataVersion 需在第一次读写前设置，之后不要更换（已有存档会读不出来）。
        /// </summary>
        public IStorageProvider Provider
        {
            get => _provider ??= new FileStorageProvider();
            set => _provider = value;
        }

        public IStorageSerializer Serializer
        {
            get => _serializer ??= new JsonStorageSerializer();
            set => _serializer = value;
        }

        public List<IStorageTransform> Transforms => _transforms;

        public List<IStorageMigration> Migrations => _migrations;

        public int DataVersion
        {
            get => _dataVersion;
            set => _dataVersion = value;
        }

        // 仅供 Inspector 监视
        internal Dictionary<string, StorageFile> Files => _files;

        public void Save<T>(string key, T value, string file = null)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key 不能为空", nameof(key));

            var storageFile = Open(file);
            // 立即序列化：之后再改 value 不会影响已保存的内容
            storageFile.Entries[key] = Serializer.Serialize(value);
            MarkDirty(storageFile);
        }

        /// <summary>
        /// key 不存在或反序列化失败时返回 defaultValue。
        /// </summary>
        public T Load<T>(string key, T defaultValue = default, string file = null) =>
            TryLoad(key, out T value, file) ? value : defaultValue;

        public bool TryLoad<T>(string key, out T value, string file = null)
        {
            if (Open(file).Entries.TryGetValue(key, out var data))
            {
                try
                {
                    value = Serializer.Deserialize<T>(data);
                    return true;
                }
                catch (Exception e)
                {
                    Log.Error(Tag, $"读取 {key} 为 {typeof(T).Name} 失败（保存后类型变了？）: {e.Message}", this);
                }
            }

            value = default;
            return false;
        }

        public bool HasKey(string key, string file = null) => Open(file).Entries.ContainsKey(key);

        public IReadOnlyCollection<string> GetKeys(string file = null) => Open(file).Entries.Keys;

        public bool DeleteKey(string key, string file = null)
        {
            var storageFile = Open(file);
            if (!storageFile.Entries.Remove(key))
                return false;

            MarkDirty(storageFile);
            return true;
        }

        public bool FileExists(string file = null)
        {
            var name = NameOf(file);
            return (_files.TryGetValue(name, out var storageFile) && storageFile.Dirty) || Provider.Exists(name);
        }

        /// <summary>
        /// 所有文件名（含 SaveRaw 写入的文件），不含备份和损坏副本。
        /// </summary>
        public IReadOnlyCollection<string> GetFiles()
        {
            var names = new HashSet<string>();
            foreach (var name in Provider.GetNames())
                if (!name.EndsWith(BackupSuffix) && !name.EndsWith(CorruptSuffix))
                    names.Add(name);
            // 还没落盘的新文件
            foreach (var pair in _files)
                if (pair.Value.Dirty)
                    names.Add(pair.Key);
            return names;
        }

        /// <summary>
        /// 原样写入一个文件：不进键值容器、不经过 Transforms、立即落盘。用于 CSV、图片等需要被外部工具打开的文件。
        /// 文件名不要与键值文件重名。
        /// </summary>
        public void SaveRaw(string file, byte[] data)
        {
            WaitPendingWrite();
            Provider.Write(file, data);
        }

        /// <summary>
        /// 读取 SaveRaw 写入的文件，不存在返回 null。
        /// </summary>
        public byte[] LoadRaw(string file) => Provider.Read(file);

        /// <summary>
        /// 删除文件及其备份，未落盘的修改一并丢弃。
        /// </summary>
        public void DeleteFile(string file = null)
        {
            var name = NameOf(file);
            WaitPendingWrite();
            _files.Remove(name);
            Provider.Delete(name);
            Provider.Delete(name + BackupSuffix);
        }

        /// <summary>
        /// 把所有未落盘的修改写入存储。全部成功返回 true；失败的文件见 <see cref="FlushFailed"/>。
        /// </summary>
        public bool Flush()
        {
            WaitPendingWrite();
            _hasDirty = false;
            foreach (var pair in _files)
            {
                if (!pair.Value.Dirty)
                    continue;

                try
                {
                    Provider.Write(pair.Key, Encode(pair.Value.ToBytes()));
                    pair.Value.Dirty = false;
                }
                catch (Exception e)
                {
                    _hasDirty = true;
                    Log.Error(Tag, $"存储文件 {pair.Key} 落盘失败: {e}", this);
                    FlushFailed?.Invoke(pair.Key, e);
                }
            }

            if (_hasDirty)
                _nextFlushTime = Time.unscaledTime + Mathf.Max(_flushInterval, RetryDelay);
            return !_hasDirty;
        }

        /// <summary>
        /// 把文件当前内容存为备份（与 Backup On Load 共用同一个 .bak）。文件不存在时返回 false。
        /// </summary>
        public bool CreateBackup(string file = null)
        {
            var name = NameOf(file);
            Flush();
            var raw = Provider.Read(name);
            if (raw == null)
                return false;

            Provider.Write(name + BackupSuffix, raw);
            return true;
        }

        /// <summary>
        /// 用备份覆盖文件，未落盘的修改丢弃。没有备份或备份损坏时返回 false，文件保持不变。
        /// </summary>
        public bool RestoreBackup(string file = null)
        {
            var name = NameOf(file);
            WaitPendingWrite();
            var raw = Provider.Read(name + BackupSuffix);
            if (raw == null)
                return false;

            StorageFile restored;
            try
            {
                restored = Decode(raw);
            }
            catch (Exception e)
            {
                Log.Error(Tag, $"存储文件 {name} 的备份已损坏: {e.Message}", this);
                return false;
            }

            Provider.Write(name, raw);
            _files[name] = restored;
            return true;
        }

        private string NameOf(string file) => string.IsNullOrEmpty(file) ? _defaultFile : file;

        private StorageFile Open(string file)
        {
            var name = NameOf(file);
            if (_files.TryGetValue(name, out var storageFile))
                return storageFile;

            // Read 抛 IO 异常时直接抛给调用方：不能当成空文件，否则下次落盘会覆盖掉玩家的存档
            var raw = Provider.Read(name);
            storageFile = TryDecode(raw, out var error);
            return Install(name, raw, storageFile, error);
        }

        private StorageFile TryDecode(byte[] raw, out Exception error)
        {
            error = null;
            if (raw == null)
                return null;

            try
            {
                return Decode(raw);
            }
            catch (Exception e)
            {
                error = e;
                return null;
            }
        }

        /// <summary>
        /// 把读到的内容放进缓存：处理损坏恢复、备份、数据迁移。raw 为 null 表示文件不存在
        /// </summary>
        /// <param name="name"></param>
        /// <param name="raw"></param>
        /// <param name="storageFile"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private StorageFile Install(string name, byte[] raw, StorageFile storageFile, Exception error)
        {
            if (error != null)
            {
                Log.Error(Tag, $"存储文件 {name} 损坏，原文件另存为 {name}{CorruptSuffix}，尝试从备份恢复: {error.Message}", this);
                storageFile = RecoverCorrupt(name, raw);
            }
            else if (storageFile != null && _backupOnLoad)
            {
                TryWrite(name + BackupSuffix, raw);
            }

            _files[name] = storageFile ??= new StorageFile { DataVersion = _dataVersion };
            if (storageFile.Dirty)
            {
                MarkDirty(storageFile);
            }
            
            try
            {
                Migrate(name, storageFile);
            }
            catch
            {
                // 迁移到一半的数据不能落盘
                _files.Remove(name);
                throw;
            }

            return storageFile;
        }

        private void Migrate(string name, StorageFile storageFile)
        {
            int from = storageFile.DataVersion;
            if (from == _dataVersion)
                return;

            if (from > _dataVersion)
            {
                Log.Warn(Tag, $"存储文件 {name} 的数据版本 {from} 高于当前 {_dataVersion}（来自更新的客户端？），按原样读取", this);
                return;
            }

            foreach (var migration in _migrations
                         .Where(m => m != null && m.FromVersion >= from && m.FromVersion < _dataVersion)
                         .OrderBy(m => m.FromVersion))
                migration.Migrate(this, name);

            storageFile.DataVersion = _dataVersion;
            MarkDirty(storageFile);
        }

        /// <summary>
        /// 返回从备份恢复的文件（Dirty，待写回主文件）；没有可用备份返回 null
        /// </summary>
        /// <param name="name"></param>
        /// <param name="raw"></param>
        /// <returns></returns>
        private StorageFile RecoverCorrupt(string name, byte[] raw)
        {
            TryWrite(name + CorruptSuffix, raw);
            try
            {
                var backup = Provider.Read(name + BackupSuffix);
                if (backup == null)
                    return null;

                var storageFile = Decode(backup);
                storageFile.Dirty = true;
                return storageFile;
            }
            catch (Exception e)
            {
                Log.Error(Tag, $"存储文件 {name} 的备份也无法读取，按空文件处理: {e.Message}", this);
                return null;
            }
        }

        private void TryWrite(string patch, byte[] data)
        {
            try
            {
                Provider.Write(patch, data);
            }
            catch (Exception e)
            {
                Log.Warn(Tag, $"写入 {patch} 失败: {e.Message}", this);
            }
        }

        private void MarkDirty(StorageFile storageFile)
        {
            storageFile.Dirty = true;
            if (_hasDirty)
                return;

            _hasDirty = true;
            _nextFlushTime = Time.unscaledTime + _flushInterval;
        }

        private byte[] Encode(byte[] data)
        {
            foreach (var transform in _transforms)
                if (transform != null)
                    data = transform.Encode(data);
            return data;
        }

        private StorageFile Decode(byte[] data)
        {
            for (int i = _transforms.Count - 1; i >= 0; i--)
                if (_transforms[i] != null)
                    data = _transforms[i].Decode(data);
            return StorageFile.FromBytes(data);
        }

        private void LateUpdate()
        {
            if (_hasDirty && Time.unscaledTime >= _nextFlushTime)
                Flush();
        }

        private void FlushOnLifecycle()
        {
            try
            {
                Saving?.Invoke();
            }
            catch (Exception e)
            {
                // 订阅方出错不能挡住落盘
                Log.Exception(Tag, e, this);
            }

            Flush();
        }

        // 移动端/小游戏切后台后可能直接被杀，不会有 OnApplicationQuit
        private void OnApplicationPause(bool paused)
        {
            if (paused)
                FlushOnLifecycle();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
                FlushOnLifecycle();
        }

        private void OnApplicationQuit() => FlushOnLifecycle();

        private void OnDestroy() => FlushOnLifecycle();
    }
}
