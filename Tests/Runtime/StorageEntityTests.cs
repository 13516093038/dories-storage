using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Dories.Storage.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Dories.Storage.Tests
{
    public class StorageEntityTests
    {
        [Serializable]
        private class PlayerData
        {
            public string Name;
            public int Level;
            public List<int> Items = new();
            public Dictionary<string, float> Stats = new();
            public Vector3 Position;
        }

        private const string File = "save.dat";

        private readonly List<GameObject> _objects = new();
        private MemoryStorageProvider _provider;
        private StorageEntity _storage;

        [SetUp]
        public void SetUp()
        {
            _provider = new MemoryStorageProvider();
            _storage = Create();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects)
                Object.DestroyImmediate(go);
            _objects.Clear();
        }

        // 同一个 provider 上新建实体，相当于重启游戏
        private StorageEntity Create(params IStorageTransform[] transforms)
        {
            var go = new GameObject("Storage");
            _objects.Add(go);
            var storage = go.AddComponent<StorageEntity>();
            storage.Provider = _provider;
            storage.Transforms.AddRange(transforms);
            return storage;
        }

        [Test]
        public void SaveLoad_RoundTripsValuesAcrossRestart()
        {
            var data = new PlayerData { Name = "dories", Level = 7, Items = { 1, 2 }, Stats = { ["hp"] = 1.5f }, Position = new Vector3(1, 2, 3) };
            _storage.Save("gold", 100);
            _storage.Save("name", "多莉");
            _storage.Save("player", data);
            _storage.Save("color", new Color(0.1f, 0.2f, 0.3f, 1f));
            _storage.Save("pos", (Vector2Int?)new Vector2Int(3, 4));
            _storage.Save("rot", Quaternion.Euler(0, 90, 0));
            Assert.IsTrue(_storage.Flush());

            var reloaded = Create();
            Assert.AreEqual(100, reloaded.Load<int>("gold"));
            Assert.AreEqual("多莉", reloaded.Load<string>("name"));
            Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 1f), reloaded.Load<Color>("color"));
            Assert.AreEqual(new Vector2Int(3, 4), reloaded.Load<Vector2Int?>("pos"));
            Assert.AreEqual(Quaternion.Euler(0, 90, 0), reloaded.Load<Quaternion>("rot"));
            var player = reloaded.Load<PlayerData>("player");
            Assert.AreEqual("dories", player.Name);
            Assert.AreEqual(7, player.Level);
            CollectionAssert.AreEqual(new[] { 1, 2 }, player.Items);
            Assert.AreEqual(1.5f, player.Stats["hp"]);
            Assert.AreEqual(new Vector3(1, 2, 3), player.Position);
        }

        [Test]
        public void Load_MissingKey_ReturnsDefault()
        {
            Assert.AreEqual(5, _storage.Load("missing", 5));
            Assert.IsFalse(_storage.TryLoad<int>("missing", out _));
            Assert.IsFalse(_storage.FileExists());
        }

        [Test]
        public void Load_WrongType_ReturnsDefaultAndLogs()
        {
            _storage.Save("name", "abc");
            LogAssert.Expect(LogType.Error, new Regex("读取 name"));
            Assert.AreEqual(-1, _storage.Load("name", -1));
        }

        [Test]
        public void Save_SnapshotsValue()
        {
            var data = new PlayerData { Level = 1 };
            _storage.Save("player", data);
            data.Level = 99;
            Assert.AreEqual(1, _storage.Load<PlayerData>("player").Level);
        }

        [Test]
        public void Keys_HasDeleteAndEnumerate()
        {
            _storage.Save("a", 1);
            _storage.Save("b", 2);
            Assert.IsTrue(_storage.HasKey("a"));
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, _storage.GetKeys());

            Assert.IsTrue(_storage.DeleteKey("a"));
            Assert.IsFalse(_storage.DeleteKey("a"));
            _storage.Flush();

            CollectionAssert.AreEquivalent(new[] { "b" }, Create().GetKeys());
        }

        [Test]
        public void Files_AreIsolatedAndDeletable()
        {
            _storage.Save("gold", 1, "slot1");
            _storage.Save("gold", 2, "slot2");
            Assert.IsTrue(_storage.FileExists("slot1"), "未落盘的修改也算存在");
            _storage.Flush();

            Assert.AreEqual(1, _storage.Load<int>("gold", file: "slot1"));
            Assert.AreEqual(2, _storage.Load<int>("gold", file: "slot2"));

            _storage.DeleteFile("slot1");
            Assert.IsFalse(_storage.FileExists("slot1"));
            Assert.IsFalse(_storage.HasKey("gold", "slot1"));
            Assert.IsTrue(_storage.FileExists("slot2"));
        }

        [Test]
        public void Save_DoesNotTouchStorageUntilFlush()
        {
            _storage.Save("gold", 1);
            Assert.IsFalse(_provider.Exists(File));
            _storage.Flush();
            Assert.IsTrue(_provider.Exists(File));
        }

        [Test]
        public void Transforms_CompressAndEncrypt()
        {
            var text = new string('a', 4096);
            var storage = Create(new GzipTransform(), new AesTransform("pw"));
            storage.Save("text", text);
            storage.Flush();

            var raw = _provider.Read(File);
            Assert.Less(raw.Length, 1024, "应已压缩");
            Assert.AreNotEqual(raw, new AesTransform("pw").Encode(new GzipTransform().Decode(new AesTransform("pw").Decode(raw))), "每次加密 IV 不同");
            Assert.AreEqual(text, Create(new GzipTransform(), new AesTransform("pw")).Load<string>("text"));
        }

        [Test]
        public void Aes_RejectsTamperedDataAndWrongPassword()
        {
            var encoded = new AesTransform("pw").Encode(new byte[] { 1, 2, 3 });
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, new AesTransform("pw").Decode(encoded));
            Assert.Catch(() => new AesTransform("other").Decode(encoded));
            encoded[20] ^= 1;
            Assert.Catch(() => new AesTransform("pw").Decode(encoded));
        }

        [Test]
        public void CorruptFile_FallsBackToBackupFromLastLoad()
        {
            _storage.Save("gold", 1);
            _storage.Flush();
            // 第二次启动：读取成功时生成备份，然后改档并落盘
            var second = Create();
            Assert.AreEqual(1, second.Load<int>("gold"));
            second.Save("gold", 2);
            second.Flush();

            var corrupt = new byte[] { 9, 9, 9, 9, 9 };
            _provider.Write(File, corrupt);
            LogAssert.Expect(LogType.Error, new Regex("损坏"));

            var third = Create();
            Assert.AreEqual(1, third.Load<int>("gold"), "回退到上次启动时的内容");
            CollectionAssert.AreEqual(corrupt, _provider.Read(File + StorageEntity.CorruptSuffix));
            third.Flush();
            Assert.AreEqual(1, Create().Load<int>("gold"), "恢复的内容已写回主文件");
        }

        [Test]
        public void CorruptFile_WithoutBackup_IsEmptyAndKeepsCorruptCopy()
        {
            _provider.Write(File, new byte[] { 1, 2, 3, 4, 5, 6 });
            LogAssert.Expect(LogType.Error, new Regex("损坏"));

            Assert.AreEqual(0, _storage.GetKeys().Count);
            Assert.IsTrue(_provider.Exists(File + StorageEntity.CorruptSuffix));
        }

        [Test]
        public void Backup_CreateAndRestore()
        {
            Assert.IsFalse(_storage.CreateBackup(), "没有文件可备份");
            Assert.IsFalse(_storage.RestoreBackup());

            _storage.Save("gold", 1);
            Assert.IsTrue(_storage.CreateBackup());
            _storage.Save("gold", 2);
            _storage.Flush();

            Assert.IsTrue(_storage.RestoreBackup());
            Assert.AreEqual(1, _storage.Load<int>("gold"));
            Assert.AreEqual(1, Create().Load<int>("gold"));
        }

        [Test]
        public void Flush_Failure_KeepsDirtyAndRaisesEvent()
        {
            var failing = new FailingProvider();
            _storage.Provider = failing;
            string failedFile = null;
            _storage.FlushFailed += (file, _) => failedFile = file;
            _storage.Save("gold", 1);

            LogAssert.Expect(LogType.Error, new Regex("落盘失败"));
            Assert.IsFalse(_storage.Flush());
            Assert.AreEqual(File, failedFile);

            failing.Fail = false;
            Assert.IsTrue(_storage.Flush(), "失败的文件保持脏状态，下次重试");
            Assert.IsTrue(failing.Exists(File));
        }

        [Test]
        public void Open_ReadFailure_ThrowsInsteadOfTreatingAsEmpty()
        {
            _storage.Provider = new FailingProvider { FailRead = true };
            Assert.Throws<IOException>(() => _storage.Load<int>("gold"));
        }

        [Test]
        public void FileProvider_WritesAtomicallyAndRejectsBadNames()
        {
            var root = Path.Combine(Application.temporaryCachePath, "DoriesStorageTests_" + Guid.NewGuid().ToString("N"));
            try
            {
                var provider = new FileStorageProvider(root);
                Assert.IsNull(provider.Read("a.dat"));

                provider.Write("slots/a.dat", new byte[] { 1 });
                provider.Write("slots/a.dat", new byte[] { 2 });
                CollectionAssert.AreEqual(new byte[] { 2 }, provider.Read("slots/a.dat"));
                Assert.IsFalse(System.IO.File.Exists(Path.Combine(root, "slots/a.dat.tmp")));

                // 模拟删除旧文件后、改名前崩溃
                System.IO.File.Move(Path.Combine(root, "slots/a.dat"), Path.Combine(root, "slots/a.dat.tmp"));
                Assert.IsTrue(provider.Exists("slots/a.dat"));
                CollectionAssert.AreEqual(new byte[] { 2 }, provider.Read("slots/a.dat"));

                provider.Delete("slots/a.dat");
                Assert.IsFalse(provider.Exists("slots/a.dat"));

                Assert.Throws<ArgumentException>(() => provider.Write("../escape.dat", new byte[] { 1 }));
                Assert.Throws<ArgumentException>(() => provider.Read("/etc/passwd"));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        private sealed class FailingProvider : IStorageProvider
        {
            private readonly MemoryStorageProvider _inner = new();
            public bool Fail = true;
            public bool FailRead;

            public bool IsThreadSafe => false;
            public bool Exists(string name) => _inner.Exists(name);
            public IEnumerable<string> GetNames() => _inner.GetNames();
            public byte[] Read(string name) => FailRead ? throw new IOException("read failed") : _inner.Read(name);

            public void Write(string name, byte[] data)
            {
                if (Fail)
                    throw new IOException("disk full");
                _inner.Write(name, data);
            }

            public void Delete(string name) => _inner.Delete(name);
        }
    }
}
