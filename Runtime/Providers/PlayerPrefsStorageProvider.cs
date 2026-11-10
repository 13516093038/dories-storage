using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 存到 PlayerPrefs（Base64）。适合没有文件系统或数据量很小的平台。
    /// </summary>
    [Serializable]
    public class PlayerPrefsStorageProvider : IStorageProvider
    {
        // PlayerPrefs 不能枚举键，名字列表单独存一个键
        private const string NamesKey = "__names";

        [SerializeField, Tooltip("PlayerPrefs 键前缀，避免与其他键冲突")]
        private string _keyPrefix = "Storage/";

        public bool IsThreadSafe => false;

        public bool Exists(string name) => PlayerPrefs.HasKey(_keyPrefix + name);

        public byte[] Read(string name) => Exists(name) ? Convert.FromBase64String(PlayerPrefs.GetString(_keyPrefix + name)) : null;

        public void Write(string name, byte[] data)
        {
            if (!Exists(name))
                SetNames(new List<string>(GetNames()) { name });
            PlayerPrefs.SetString(_keyPrefix + name, Convert.ToBase64String(data));
            PlayerPrefs.Save();
        }

        public void Delete(string name)
        {
            var names = new List<string>(GetNames());
            if (names.Remove(name))
                SetNames(names);
            PlayerPrefs.DeleteKey(_keyPrefix + name);
            PlayerPrefs.Save();
        }

        public IEnumerable<string> GetNames() =>
            PlayerPrefs.GetString(_keyPrefix + NamesKey, string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        private void SetNames(List<string> names) => PlayerPrefs.SetString(_keyPrefix + NamesKey, string.Join("\n", names));
    }
}
