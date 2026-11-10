using System;
using System.Collections.Generic;
using Dories.Logging;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 给场景对象一个跨运行稳定的 id。存档里对 GameObject/组件的引用存的是这个 id（见 <see cref="JsonStorageSerializer"/>），
    /// <see cref="StorageAutoSave"/> 也用它作为 key 前缀。
    /// 场景里的对象在编辑器下自动生成 id；预制体资源本身不生成，运行时实例化的对象需要代码给 <see cref="Id"/> 赋值。
    /// </summary>
    [DisallowMultipleComponent]
    public class StorageId : MonoBehaviour
    {
        private const string Tag = "Storage";

        private static readonly Dictionary<string, StorageId> Registry = new();

        [SerializeField, Tooltip("唯一 id。复制对象后两者 id 相同，请在组件右键菜单选 Regenerate Id")]
        private string _id;

        public string Id
        {
            get => _id;
            set
            {
                Unregister();
                _id = value;
                Register();
            }
        }

        /// <summary>按 id 找已加载（Awake 过）且未销毁的对象，找不到返回 null。</summary>
        public static StorageId Find(string id) =>
            !string.IsNullOrEmpty(id) && Registry.TryGetValue(id, out var storageId) && storageId != null ? storageId : null;

        private void Awake() => Register();

        private void OnDestroy() => Unregister();

        private void Register()
        {
            if (string.IsNullOrEmpty(_id))
                return;

            if (Registry.TryGetValue(_id, out var other) && other != null && other != this)
            {
                Log.Error(Tag, $"StorageId 重复: {_id}（{other.name} 与 {name}），后者不会被存档引用到", this);
                return;
            }

            Registry[_id] = this;
        }

        private void Unregister()
        {
            if (!string.IsNullOrEmpty(_id) && Registry.TryGetValue(_id, out var current) && current == this)
                Registry.Remove(_id);
        }

#if UNITY_EDITOR
        private void Reset() => EnsureId();

        private void OnValidate() => EnsureId();

        [ContextMenu("Regenerate Id")]
        private void RegenerateId()
        {
            _id = null;
            EnsureId();
        }

        private void EnsureId()
        {
            // 预制体资源（含预制体编辑模式）不生成：否则它的所有实例共用同一个 id
            if (!string.IsNullOrEmpty(_id) || Application.isPlaying || !gameObject.scene.IsValid()
                || UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject) != null)
                return;

            _id = Guid.NewGuid().ToString("N");
            UnityEditor.EditorUtility.SetDirty(this);
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this))
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }
#endif
    }
}
