using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dories.Logging;
using UnityEngine;
#if DORIES_UNITASK_SUPPORT
using FlushTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageTask = Cysharp.Threading.Tasks.UniTask;
#else
using FlushTask = System.Threading.Tasks.Task<bool>;
using StorageTask = System.Threading.Tasks.Task;
#endif

namespace Dories.Storage.Runtime
{
    // 异步接口：把压缩/加密和 IO 放到后台线程。Provider 不支持线程或在 WebGL 上时退化为同步执行
    public partial class StorageEntity
    {
        private Task _pendingWrite;

        private bool CanUseThread =>
#if UNITY_WEBGL && !UNITY_EDITOR
            false;
#else
            Provider.IsThreadSafe;
#endif

        /// <summary>
        /// 同 <see cref="Flush"/>，但不卡主线程，适合存档较大时使用。调用时刻的数据即为写入内容，之后的修改留到下次落盘。
        /// </summary>
        public async FlushTask FlushAsync()
        {
            if (!CanUseThread)
                return Flush();

            // 同一时间只允许一个后台写入，避免两次写入同一个文件
            while (_pendingWrite != null)
                await _pendingWrite;

            var snapshots = new List<(string Name, StorageFile File, byte[] Data)>();
            foreach (var pair in _files)
            {
                if (!pair.Value.Dirty)
                    continue;

                snapshots.Add((pair.Key, pair.Value, pair.Value.ToBytes()));
                pair.Value.Dirty = false;
            }

            _hasDirty = false;
            if (snapshots.Count == 0)
                return true;

            var provider = Provider;
            var write = Task.Run(() =>
            {
                var errors = new Exception[snapshots.Count];
                for (int i = 0; i < snapshots.Count; i++)
                {
                    try
                    {
                        provider.Write(snapshots[i].Name, Encode(snapshots[i].Data));
                    }
                    catch (Exception e)
                    {
                        errors[i] = e;
                    }
                }

                return errors;
            });

            _pendingWrite = write;
            Exception[] results;
            try
            {
                results = await write;
            }
            finally
            {
                if (_pendingWrite == write)
                    _pendingWrite = null;
            }

            bool success = true;
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] == null)
                    continue;

                success = false;
                var (name, storageFile, _) = snapshots[i];
                // 期间文件可能已被删除或回档，那就不用重试了
                if (_files.TryGetValue(name, out var current) && current == storageFile)
                {
                    storageFile.Dirty = true;
                    _hasDirty = true;
                    _nextFlushTime = Time.unscaledTime + Mathf.Max(_flushInterval, RetryDelay);
                }

                Log.Error(Tag, $"存储文件 {name} 落盘失败: {results[i]}", this);
                FlushFailed?.Invoke(name, results[i]);
            }

            return success;
        }

        /// <summary>
        /// 在后台线程读取并解码文件，完成后该文件的 Load/Save 不再有 IO。适合在加载界面预读大存档。
        /// </summary>
        public async StorageTask PreloadAsync(string file = null)
        {
            var name = NameOf(file);
            // Exists 在主线程调用，同时满足 IStorageProvider.IsThreadSafe 的约定
            if (_files.ContainsKey(name) || !CanUseThread || !Provider.Exists(name))
            {
                Open(name);
                return;
            }

            var provider = Provider;
            var (raw, storageFile, error) = await Task.Run(() =>
            {
                var data = provider.Read(name);
                var decoded = TryDecode(data, out var decodeError);
                return (data, decoded, decodeError);
            });

            // 等待期间可能已被同步接口打开
            if (!_files.ContainsKey(name))
                Install(name, raw, storageFile, error);
        }

        // 同步写入前等后台写入结束，避免同时写同一个文件。后台任务不依赖主线程，不会死锁
        private void WaitPendingWrite() => _pendingWrite?.Wait();
    }
}
