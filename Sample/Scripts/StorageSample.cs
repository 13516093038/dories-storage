using System;
using System.Collections.Generic;
using Dories.Storage.Runtime;
using UnityEngine;
using UnityEngine.Assertions;

namespace Dories.Storage.Sample
{
    /// <summary>
    /// 存储示例：复杂角色数据、存档槽、备份回档、迁移、压缩加密、表格、截图和自动存档。
    /// 运行后点按钮操作，停止再运行可看到数据和方块位置被恢复；选中 Storage 节点查看文件状态。
    /// </summary>
    public class StorageSample : MonoBehaviour
    {
        private const string RankFile = "rank.csv";
        private const string ShotFile = "shot.png";

        [SerializeField] private StorageEntity _storage;
        [SerializeField, Tooltip("挂了 StorageAutoSave 的方块，方向键移动")] private Transform _cube;
        [SerializeField] private float _moveSpeed = 3f;

        private string _slot = "slot1.dat";
        private Texture2D _shot;
        private string _message = "方向键移动方块，位置会自动存档";
        private Vector2 _scroll;
        private CharacterData _character;

        [Serializable]
        public sealed class CharacterData
        {
            public string Name = "多莉";
            public int Level = 7;
            public Vector3 Position;
            public List<ItemData> Inventory = new();
            public Dictionary<string, int> QuestProgress = new();
        }

        [Serializable]
        public sealed class ItemData
        {
            public string Id;
            public int Count;
            public Dictionary<string, float> Attributes = new();
        }

        [Serializable]
        public sealed class GoldToCoins : IStorageMigration
        {
            public int FromVersion => 0;
            public void Migrate(StorageEntity storage, string file)
            {
                storage.Save("coins", storage.Load<int>("gold", file: file) * 10, file);
                storage.DeleteKey("gold", file);
            }
        }

        [Serializable]
        public sealed class AddVisitedAreas : IStorageMigration
        {
            public int FromVersion => 1;
            public void Migrate(StorageEntity storage, string file) =>
                storage.Save("visitedAreas", new List<string> { "camp" }, file);
        }

        private void Start()
        {
            _shot = _storage.LoadImage(ShotFile);
            LoadCharacter();
        }

        private void Update()
        {
            _cube.Translate(new Vector3(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical")) * (_moveSpeed * Time.deltaTime));
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, Mathf.Min(460, Screen.width - 20), Screen.height - 20));
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label($"当前存档槽: {_slot}    所有文件: {string.Join(", ", _storage.GetFiles())}");
            GUILayout.BeginHorizontal();
            for (int i = 1; i <= 3; i++)
                if (GUILayout.Button($"slot{i}"))
                {
                    _slot = $"slot{i}.dat";
                    LoadCharacter();
                }
            if (GUILayout.Button("删除当前槽"))
            {
                _storage.DeleteFile(_slot);
                LoadCharacter();
            }
            GUILayout.EndHorizontal();

            int gold = _storage.Load("gold", 0, _slot);
            GUILayout.Label($"gold = {gold}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("gold +10"))
                _storage.Save("gold", gold + 10, _slot);
            if (GUILayout.Button("立即落盘"))
                _message = _storage.Flush() ? "已落盘" : "落盘失败";
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("备份"))
                _message = _storage.CreateBackup(_slot) ? "已备份" : "没有可备份的文件";
            if (GUILayout.Button("回档"))
            {
                bool restored = _storage.RestoreBackup(_slot);
                _message = restored ? "已回档" : "没有备份";
                if (restored)
                    LoadCharacter();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("复杂角色数据：嵌套对象 / List / Dictionary / Vector3");
            GUILayout.Label("下方修改是草稿；保存后才进入存档。切槽或读取会丢弃未保存草稿。");
            _character.Name = GUILayout.TextField(_character.Name);
            GUILayout.Label($"等级 {_character.Level}，采药进度 {_character.QuestProgress["collect_herbs"]}，存档位置 {_character.Position}");
            foreach (var item in _character.Inventory)
            {
                GUILayout.Label($"{item.Id} × {item.Count}");
                foreach (var attribute in item.Attributes)
                    GUILayout.Label($"    {attribute.Key}: {attribute.Value}");
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("升级"))
                _character.Level++;
            if (GUILayout.Button("药水 +1"))
                _character.Inventory[0].Count++;
            if (GUILayout.Button("采药 +1"))
                _character.QuestProgress["collect_herbs"]++;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("保存角色到当前槽"))
            {
                _character.Position = _cube.position;
                _storage.Save("character", _character, _slot);
                _message = _storage.Flush() ? "角色已落盘；继续修改草稿后点读取，可验证保存的是快照" : "落盘失败，等待重试";
            }
            if (GUILayout.Button("读取角色"))
                LoadCharacter();
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("独立演示：内存存储 + GZip + AES + 两步迁移（v0 → v1 → v2）");
            if (GUILayout.Button("运行迁移与复杂数据自检"))
            {
                try { _message = RunAdvancedExamples(); }
                catch (Exception e) { _message = $"示例失败: {e.Message}"; Debug.LogException(e, this); }
            }
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("记一条排行（CSV）"))
                AppendRank(gold);
            if (GUILayout.Button("截图（PNG）"))
                SaveShot();
            GUILayout.EndHorizontal();

            GUILayout.Label(_message);
            if (_shot != null)
                GUILayout.Label(_shot, GUILayout.Width(160), GUILayout.Height(90));

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void LoadCharacter() =>
            _character = _storage.Load<CharacterData>("character", file: _slot) ?? CreateCharacter();

        // 示例条目在这里填充，避免反序列化时向已有默认列表重复追加。
        private static CharacterData CreateCharacter() => new()
        {
            Inventory =
            {
                new ItemData { Id = "potion", Count = 3, Attributes = { ["heal"] = 25f } },
                new ItemData { Id = "sword", Count = 1, Attributes = { ["attack"] = 12.5f } },
            },
            QuestProgress = { ["collect_herbs"] = 2 },
        };

        /// <summary>独立内存仓库模拟三次启动，同时留下可重复执行的断言检查。</summary>
        public static string RunAdvancedExamples()
        {
            var host = new GameObject("Storage sample checks") { hideFlags = HideFlags.HideAndDontSave };
            host.SetActive(false);
            try
            {
                var provider = new MemoryStorageProvider();
                StorageEntity Create(int version)
                {
                    var storage = host.AddComponent<StorageEntity>();
                    storage.Provider = provider;
                    storage.DataVersion = version;
                    storage.Transforms.Add(new GzipTransform());
                    // 演示密码仅用于此次内存数据，正式项目应自行配置。
                    storage.Transforms.Add(new AesTransform("sample-password"));
                    storage.Migrations.Add(new GoldToCoins());
                    storage.Migrations.Add(new AddVisitedAreas());
                    return storage;
                }

                var old = Create(0);
                var character = CreateCharacter();
                character.Position = new Vector3(1, 2, 3);
                old.Save("character", character);
                old.Save("gold", 5);
                var otherCharacter = CreateCharacter();
                otherCharacter.Name = "另一个角色";
                old.Save("character", otherCharacter, "slot2.dat");
                character.Inventory[0].Count = 99; // 保存后修改原对象，不应改变快照。
                Assert.IsTrue(old.Flush());

                var upgraded = Create(2);
                var loaded = upgraded.Load<CharacterData>("character");
                Assert.AreEqual("多莉", loaded.Name);
                Assert.AreEqual(7, loaded.Level);
                Assert.AreEqual(new Vector3(1, 2, 3), loaded.Position);
                Assert.AreEqual(2, loaded.Inventory.Count);
                Assert.AreEqual(3, loaded.Inventory[0].Count);
                Assert.AreEqual(12.5f, loaded.Inventory[1].Attributes["attack"]);
                Assert.AreEqual(2, loaded.QuestProgress["collect_herbs"]);
                Assert.AreEqual("另一个角色", upgraded.Load<CharacterData>("character", file: "slot2.dat").Name);
                Assert.AreEqual(50, upgraded.Load<int>("coins"));
                Assert.IsFalse(upgraded.HasKey("gold"));
                Assert.AreEqual("camp", upgraded.Load<List<string>>("visitedAreas")[0]);
                Assert.IsTrue(upgraded.Flush());

                // 第三次启动仍注册迁移；已升级文件不应重复迁移，否则 coins 会被重置。
                var restarted = Create(2);
                Assert.AreEqual(50, restarted.Load<int>("coins"));
                Assert.AreEqual(3, restarted.Load<CharacterData>("character").Inventory[0].Count);
                return "自检通过：复杂数据/快照/分槽/GZip/AES；gold 5 → coins 50，新增 camp，重启不重复迁移。";
            }
            finally
            {
                if (Application.isPlaying)
                    Destroy(host);
                else
                    DestroyImmediate(host);
            }
        }

        private void AppendRank(int gold)
        {
            var sheet = _storage.LoadSheet(RankFile) ?? new StorageSheet();
            int row = sheet.RowCount;
            sheet.SetCell(0, row, _slot);
            sheet.SetCell(1, row, gold);
            _storage.SaveSheet(RankFile, sheet);
            _message = $"{RankFile} 共 {sheet.RowCount} 行，可用 Excel 打开";
        }

        private void SaveShot()
        {
            if (_shot != null)
                Destroy(_shot);
            // 示例从简：OnGUI 里截屏会带上当前已绘制的界面
            _shot = ScreenCapture.CaptureScreenshotAsTexture();
            _storage.SaveImage(ShotFile, _shot);
            _message = $"已保存 {ShotFile}";
        }

        private void OnDestroy()
        {
            if (_shot != null)
                Destroy(_shot);
        }
    }
}
