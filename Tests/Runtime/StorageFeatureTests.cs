using System;
using System.Collections;
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
    /// <summary>文件列表、原始文件、数据迁移、异步、场景引用、自动存档、表格、图片。</summary>
    public class StorageFeatureTests
    {
        private enum Rank
        {
            Bronze,
            Gold,
        }

        private class Quest
        {
            public string Title;
            public GameObject Target;
            public Transform Anchor;
        }

        private sealed class RenameGoldMigration : IStorageMigration
        {
            public int FromVersion => 0;

            public void Migrate(StorageEntity storage, string file)
            {
                storage.Save("coins", storage.Load<int>("gold", file: file) * 10, file);
                storage.DeleteKey("gold", file);
            }
        }

        private sealed class ThrowingMigration : IStorageMigration
        {
            public int FromVersion => 0;

            public void Migrate(StorageEntity storage, string file)
            {
                storage.Save("half", true, file);
                throw new InvalidOperationException("migration failed");
            }
        }

        private sealed class Health : MonoBehaviour, IStorageAutoSaveState
        {
            public int Hp = 100;

            public void SaveState(StorageEntity storage, string key, string file) => storage.Save(key, Hp, file);
            public void LoadState(StorageEntity storage, string key, string file) => Hp = storage.Load(key, Hp, file);
        }

        private const string File = "save.dat";

        private readonly List<Object> _objects = new();
        private MemoryStorageProvider _provider;
        private StorageEntity _storage;
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _provider = new MemoryStorageProvider();
            _storage = Create(_provider);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _objects)
                Object.DestroyImmediate(obj);
            _objects.Clear();
            if (_tempRoot != null && Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, true);
            _tempRoot = null;
        }

        private StorageEntity Create(IStorageProvider provider)
        {
            var storage = NewObject("Storage").AddComponent<StorageEntity>();
            storage.Provider = provider;
            return storage;
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private FileStorageProvider TempFileProvider()
        {
            _tempRoot = Path.Combine(Application.temporaryCachePath, "DoriesStorageTests_" + Guid.NewGuid().ToString("N"));
            return new FileStorageProvider(_tempRoot);
        }

        // EditMode 下不会自动调 Awake，赋 Id 即注册
        private GameObject NewIdentified(string name, string id)
        {
            var go = NewObject(name);
            go.AddComponent<StorageId>().Id = id;
            return go;
        }

        [Test]
        public void GetFiles_ListsSavedAndPendingFilesWithoutBackups()
        {
            _storage.Save("gold", 1, "slot1");
            _storage.Flush();
            Create(_provider).Load<int>("gold", file: "slot1"); // 生成 slot1.bak
            _storage.Save("gold", 2, "slot2");                   // 未落盘
            _storage.SaveRaw("notes.txt", new byte[] { 1 });

            CollectionAssert.AreEquivalent(new[] { "slot1", "slot2", "notes.txt" }, _storage.GetFiles());
        }

        [Test]
        public void FileProvider_GetNames_ReturnsRelativePaths()
        {
            var provider = TempFileProvider();
            CollectionAssert.IsEmpty(provider.GetNames());
            provider.Write("a.dat", new byte[] { 1 });
            provider.Write("slots/b.dat", new byte[] { 1 });
            CollectionAssert.AreEquivalent(new[] { "a.dat", "slots/b.dat" }, provider.GetNames());
        }

        [Test]
        public void Raw_IsStoredVerbatim()
        {
            _storage.Transforms.Add(new AesTransform("pw"));
            var data = new byte[] { 1, 2, 3 };
            _storage.SaveRaw("raw.bin", data);

            CollectionAssert.AreEqual(data, _provider.Read("raw.bin"), "不经过 Transforms");
            CollectionAssert.AreEqual(data, _storage.LoadRaw("raw.bin"));
            Assert.IsNull(_storage.LoadRaw("missing.bin"));
            _storage.DeleteFile("raw.bin");
            Assert.IsFalse(_storage.FileExists("raw.bin"));
        }

        [Test]
        public void Migration_UpgradesOldFileOnce()
        {
            _storage.Save("gold", 5);
            _storage.Flush();

            var upgraded = Create(_provider);
            upgraded.DataVersion = 1;
            upgraded.Migrations.Add(new RenameGoldMigration());
            Assert.AreEqual(50, upgraded.Load<int>("coins"));
            Assert.IsFalse(upgraded.HasKey("gold"));
            upgraded.Flush();

            // 再次启动：文件已是新版本，不再迁移
            var again = Create(_provider);
            again.DataVersion = 1;
            again.Migrations.Add(new RenameGoldMigration());
            Assert.AreEqual(50, again.Load<int>("coins"));
        }

        [Test]
        public void Migration_NewFileStartsAtCurrentVersion()
        {
            _storage.DataVersion = 1;
            _storage.Migrations.Add(new RenameGoldMigration());
            Assert.IsFalse(_storage.HasKey("coins"), "新文件不应执行迁移");
        }

        [Test]
        public void Migration_Failure_LeavesFileUntouched()
        {
            _storage.Save("gold", 5);
            _storage.Flush();
            var before = _provider.Read(File);

            var upgraded = Create(_provider);
            upgraded.DataVersion = 1;
            upgraded.Migrations.Add(new ThrowingMigration());
            Assert.Throws<InvalidOperationException>(() => upgraded.Load<int>("gold"));
            upgraded.Flush();

            CollectionAssert.AreEqual(before, _provider.Read(File));
        }

        [UnityTest]
        public IEnumerator FlushAsync_And_PreloadAsync_RoundTripOnBackgroundThread()
        {
            var provider = TempFileProvider();
            var storage = Create(provider);
            storage.Transforms.Add(new GzipTransform());
            storage.Save("gold", 7);

            var flush = AsTask(storage.FlushAsync());
            storage.Save("gold", 8); // 异步落盘期间的修改留到下次
            yield return Wait(flush);
            Assert.IsTrue(flush.Result);
            Assert.IsTrue(storage.Flush());

            var reloaded = Create(provider);
            reloaded.Transforms.Add(new GzipTransform());
            yield return Wait(AsTask(reloaded.PreloadAsync()));
            Assert.AreEqual(8, reloaded.Load<int>("gold"));
        }

        [UnityTest]
        public IEnumerator FlushAsync_WithoutThreadSupport_FallsBackToSync()
        {
            _storage.Save("gold", 1);
            var flush = AsTask(_storage.FlushAsync());
            yield return Wait(flush);
            Assert.IsTrue(flush.Result);
            Assert.IsTrue(_provider.Exists(File));
        }

        [Test]
        public void SceneReferences_AreSavedByStorageId()
        {
            var target = NewIdentified("Target", "target-1");
            var plain = NewObject("NoId");

            _storage.Save("quest", new Quest { Title = "find", Target = target, Anchor = target.transform });
            LogAssert.Expect(LogType.Warning, new Regex("没有 StorageId"));
            _storage.Save("lost", plain);
            _storage.Flush();

            var reloaded = Create(_provider);
            var quest = reloaded.Load<Quest>("quest");
            Assert.AreEqual("find", quest.Title);
            Assert.AreSame(target, quest.Target);
            Assert.AreSame(target.transform, quest.Anchor);
            Assert.IsNull(reloaded.Load<GameObject>("lost"));

            Object.DestroyImmediate(target);
            Assert.IsNull(reloaded.Load<Quest>("quest").Target, "对象已不存在时引用为 null");
        }

        [Test]
        public void StorageId_DuplicateIsReported()
        {
            var first = NewIdentified("A", "dup");
            LogAssert.Expect(LogType.Error, new Regex("StorageId 重复"));
            NewIdentified("B", "dup");
            Assert.AreSame(first.GetComponent<StorageId>(), StorageId.Find("dup"));
        }

        [Test]
        public void AutoSave_SavesAndRestoresTransformAndStates()
        {
            var go = NewIdentified("Player", "player");
            go.AddComponent<Health>().Hp = 42;
            var autoSave = go.AddComponent<StorageAutoSave>();
            go.transform.localPosition = new Vector3(1, 2, 3);
            go.transform.localScale = Vector3.one * 2;
            autoSave.Save();
            _storage.Flush();

            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one;
            go.GetComponent<Health>().Hp = 0;
            autoSave.Load();

            Assert.AreEqual(new Vector3(1, 2, 3), go.transform.localPosition);
            Assert.AreEqual(Vector3.one * 2, go.transform.localScale);
            Assert.AreEqual(42, go.GetComponent<Health>().Hp);
        }

        [Test]
        public void Sheet_RoundTripsThroughCsv()
        {
            var sheet = new StorageSheet();
            sheet.SetCell(0, 0, "名字");
            sheet.SetCell(1, 0, "score, \"best\"\nline2");
            sheet.SetCell(0, 1, 1.5f);
            sheet.SetCell(2, 1, Rank.Gold);
            _storage.SaveSheet("rank.csv", sheet);

            var loaded = _storage.LoadSheet("rank.csv");
            Assert.AreEqual(2, loaded.RowCount);
            Assert.AreEqual(3, loaded.ColumnCount);
            Assert.AreEqual("名字", loaded.GetCell<string>(0, 0));
            Assert.AreEqual("score, \"best\"\nline2", loaded.GetCell<string>(1, 0));
            Assert.AreEqual(1.5f, loaded.GetCell<float>(0, 1));
            Assert.AreEqual(-1, loaded.GetCell(1, 1, -1), "空单元格返回默认值");
            Assert.AreEqual(Rank.Gold, loaded.GetCell<Rank>(2, 1));
            Assert.AreEqual(0, loaded.GetCell<int>(9, 9));
            Assert.IsNull(_storage.LoadSheet("missing.csv"));
        }

        [Test]
        public void Sheet_Parse_HandlesExcelOutput()
        {
            var sheet = StorageSheet.Parse("﻿a,b\r\n\"x\"\"y\",\r\n1,2");
            Assert.AreEqual(3, sheet.RowCount);
            Assert.AreEqual("b", sheet.GetCell<string>(1, 0));
            Assert.AreEqual("x\"y", sheet.GetCell<string>(0, 1));
            Assert.AreEqual(2, sheet.GetCell<int>(1, 2));
        }

        [Test]
        public void Image_RoundTripsAsPng()
        {
            var texture = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            _objects.Add(texture);
            texture.SetPixel(1, 1, Color.red);
            texture.Apply();
            _storage.SaveImage("shot.png", texture);

            var loaded = _storage.LoadImage("shot.png");
            _objects.Add(loaded);
            Assert.AreEqual(4, loaded.width);
            Assert.AreEqual(2, loaded.height);
            Assert.AreEqual(Color.red, loaded.GetPixel(1, 1));
            Assert.IsNull(_storage.LoadImage("missing.png"));
        }

#if DORIES_UNITASK_SUPPORT
        private static System.Threading.Tasks.Task<bool> AsTask(Cysharp.Threading.Tasks.UniTask<bool> task) => Cysharp.Threading.Tasks.UniTaskExtensions.AsTask(task);
        private static System.Threading.Tasks.Task AsTask(Cysharp.Threading.Tasks.UniTask task) => Cysharp.Threading.Tasks.UniTaskExtensions.AsTask(task);
#else
        private static System.Threading.Tasks.Task<bool> AsTask(System.Threading.Tasks.Task<bool> task) => task;
        private static System.Threading.Tasks.Task AsTask(System.Threading.Tasks.Task task) => task;
#endif

        private static IEnumerator Wait(System.Threading.Tasks.Task task)
        {
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
                throw task.Exception;
        }
    }
}
