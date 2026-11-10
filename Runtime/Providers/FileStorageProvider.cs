using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 存到本地文件，默认在 persistentDataPath 下的子目录。先写临时文件再改名，写入中途崩溃不会损坏原文件。
    /// </summary>
    [Serializable]
    public class FileStorageProvider : IStorageProvider
    {
        private const string TempSuffix = ".tmp";

        [SerializeField]
        [Tooltip("存档目录：相对路径基于 Application.persistentDataPath，也可填绝对路径")]
        private string _directory = "Storage";

        private string _root;

        public FileStorageProvider() { }

        public FileStorageProvider(string directory) => _directory = directory;

        // persistentDataPath 只能在主线程取，首次访问后缓存，后台线程才能用
        public string Root => _root ??= Path.IsPathRooted(_directory) ? _directory : Path.Combine(Application.persistentDataPath, _directory);

        public bool IsThreadSafe => true;

        public bool Exists(string name) => File.Exists(Resolve(name));

        public byte[] Read(string name)
        {
            var path = Resolve(name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void Write(string name, byte[] data)
        {
            var path = Resolve(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path + TempSuffix, data);
            // 不用 File.Replace：部分平台不支持。删除与改名之间崩溃由 Resolve 恢复
            File.Delete(path);
            File.Move(path + TempSuffix, path);
        }

        public void Delete(string name)
        {
            var path = Resolve(name);
            File.Delete(path + TempSuffix);
            File.Delete(path);
        }

        public IEnumerable<string> GetNames()
        {
            if (!Directory.Exists(Root))
                yield break;

            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                if (!path.EndsWith(TempSuffix))
                    yield return Path.GetRelativePath(Root, path).Replace('\\', '/');
        }

        private string Resolve(string name)
        {
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Contains(".."))
                throw new ArgumentException($"非法的存储文件名: {name}", nameof(name));

            var path = Path.Combine(Root, name);
            // 上次 Write 在删除旧文件后、改名前崩溃：临时文件是完整的新数据
            if (!File.Exists(path) && File.Exists(path + TempSuffix))
                File.Move(path + TempSuffix, path);
            return path;
        }
    }
}
