using System;
using Dories.Logging;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 自动存取所在对象的状态：Transform，以及同对象上所有 <see cref="IStorageAutoSaveState"/> 组件。
    /// Start 时读取；StorageEntity 因切后台/退出落盘前、以及本对象销毁（如切场景）时保存。也可手动调 <see cref="Save"/> / <see cref="Load"/>。
    /// </summary>
    [RequireComponent(typeof(StorageId))]
    public class StorageAutoSave : MonoBehaviour
    {
        private const string Tag = "Storage";

        [Serializable]
        private struct TransformState
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        [SerializeField, Tooltip("留空时从场景里找")]
        private StorageEntity _storage;

        [SerializeField, Tooltip("存到哪个文件；留空用 StorageEntity 的默认文件")]
        private string _file;

        [SerializeField, Tooltip("保存本地位置、旋转、缩放")]
        private bool _saveTransform = true;

        [SerializeField, Tooltip("Start 时自动读取")]
        private bool _loadOnStart = true;

        private bool _subscribed;

        /// <summary>存到哪个文件，可在运行时切换存档槽。</summary>
        public string File
        {
            get => _file;
            set => _file = value;
        }

        public void Save()
        {
            if (!TryGetContext(out var id))
                return;

            if (_saveTransform)
            {
                _storage.Save(id + "/Transform", new TransformState
                {
                    Position = transform.localPosition,
                    Rotation = transform.localRotation,
                    Scale = transform.localScale,
                }, _file);
            }

            foreach (var state in GetComponents<IStorageAutoSaveState>())
                state.SaveState(_storage, StateKey(id, state), _file);
        }

        public void Load()
        {
            if (!TryGetContext(out var id))
                return;

            if (_saveTransform && _storage.TryLoad<TransformState>(id + "/Transform", out var saved, _file))
            {
                transform.localPosition = saved.Position;
                transform.localRotation = saved.Rotation;
                transform.localScale = saved.Scale;
            }

            foreach (var state in GetComponents<IStorageAutoSaveState>())
                state.LoadState(_storage, StateKey(id, state), _file);
        }

        private static string StateKey(string id, IStorageAutoSaveState state) => $"{id}/{state.GetType().Name}";

        private bool TryGetContext(out string id)
        {
            id = GetComponent<StorageId>().Id;
            if (_storage == null)
                _storage = FindObjectOfType<StorageEntity>();
            if (_storage != null && !string.IsNullOrEmpty(id))
                return true;

            Log.Warn(Tag, _storage == null ? "StorageAutoSave 找不到 StorageEntity" : "StorageAutoSave 所在对象的 StorageId 为空（运行时实例化的对象需代码赋值）", this);
            return false;
        }

        // 放在 Start：StorageEntity 和被引用对象的 StorageId 都已 Awake
        private void Start()
        {
            if (_loadOnStart)
                Load();

            if (_storage != null)
            {
                _storage.Saving += Save;
                _subscribed = true;
            }
        }

        private void OnDestroy()
        {
            // 退出时 StorageEntity 可能先被销毁：它销毁前已通过 Saving 通知过一次
            if (_storage == null)
                return;

            if (_subscribed)
                _storage.Saving -= Save;
            Save();
        }
    }
}
