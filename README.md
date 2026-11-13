# Dories Storage

本地键值存储（`StorageEntity`）。只提供机制，存什么、分几个文件由项目决定。

- 任意类型按 key 存取：基础类型、自定义类、List/Dictionary、Vector3/Color 等 Unity 结构体
- 多文件：`file` 参数留空用默认文件，多存档槽用不同文件名
- 内存缓存 + 合并落盘：`Save` 只改内存，按间隔落盘，切后台/退出/销毁时立即落盘
- 原子写：先写临时文件再改名，写一半崩溃不会损坏原存档
- 备份与损坏恢复：读取成功时自动备份，之后文件损坏自动回退，损坏文件另存为 `.corrupt`
- 压缩（GZip）、加密（AES-256 + HMAC 防篡改）
- 异步落盘 / 预读（后台线程，不卡主线程）
- 数据版本迁移
- 场景对象引用（`StorageId`）与自动存档（`StorageAutoSave`）
- 表格（CSV）、图片（PNG/JPG）、原始文件
- 存储介质、序列化、数据变换三处可替换

## 用法

`StorageEntity` 挂在 GameEntry 预制体的 `Storage` 节点下，通过 `GameEntry.Storage` 访问（不用 GameFramework 时可自己挂到任意常驻对象上）。

```csharp
GameEntry.Storage.Save("gold", 100);
GameEntry.Storage.Save("player", new PlayerData { Level = 3 });

int gold = GameEntry.Storage.Load("gold", 0);              // 不存在返回默认值
if (GameEntry.Storage.TryLoad<PlayerData>("player", out var player)) { }

GameEntry.Storage.HasKey("gold");
GameEntry.Storage.DeleteKey("gold");
GameEntry.Storage.GetKeys();

// 多存档槽
GameEntry.Storage.Save("gold", 100, "slot1.dat");
GameEntry.Storage.FileExists("slot1.dat");
GameEntry.Storage.DeleteFile("slot1.dat");
GameEntry.Storage.GetFiles();                        // 所有文件名，不含备份

// 关键节点（如支付到账）立即落盘
GameEntry.Storage.Flush();

// 手动备份 / 回档
GameEntry.Storage.CreateBackup();
GameEntry.Storage.RestoreBackup();

// 落盘失败（磁盘满等）：失败的文件保持未落盘状态，稍后自动重试
GameEntry.Storage.FlushFailed += (file, e) => ShowTip("存档失败，请清理存储空间");
```

| Inspector | 说明 |
| --- | --- |
| Default File | 未传 `file` 时用的文件名 |
| Flush Interval | 数据变更后最多延迟多少秒落盘，0 = 当帧 LateUpdate |
| Backup On Load | 文件读取成功时存一份 `.bak`，损坏时自动回退到它（即上次启动时的内容） |
| Data Version | 当前业务数据版本，见"数据版本迁移" |
| Provider | 存储介质，默认 `FileStorageProvider`（persistentDataPath/Storage） |
| Serializer | 默认 `JsonStorageSerializer`（Newtonsoft.Json） |
| Transforms | 落盘前依次执行，如先 `GzipTransform` 后 `AesTransform` |
| Migrations | 数据版本迁移步骤 |

Play Mode 下 Inspector 显示已打开的文件、数据版本、各 key 的大小，未落盘的文件标黄。

示例场景：`Sample/StorageSample.unity`。

Sample 中还可以操作复杂角色数据：角色名、等级、位置、嵌套背包物品及属性字典、任务进度。

1. 修改角色草稿，点击“保存角色到当前槽”。保存会立即落盘；继续修改后点击“读取角色”，可观察快照恢复。
2. 切换 slot1 / slot2 / slot3，为不同槽保存不同角色。切槽、读取和成功回档会替换当前草稿，未保存的修改会丢弃。
3. 点击“运行迁移与复杂数据自检”：在独立内存仓库中，用 GZip → AES 写入版本 0 的数据，再模拟版本 2 启动。依次把 `gold=5` 转为 `coins=50`、新增已访问区域 `camp`，最后再次启动验证不会重复迁移。该演示不改动磁盘存档。

实现和可重复执行的断言入口位于 `Sample/Scripts/StorageSample.cs` 的 `RunAdvancedExamples()`；面板支持滚动。

注意：

- `Save` 时立即序列化，之后再改对象不影响已保存内容；改完要再 `Save` 一次。
- Provider / Serializer / Transforms（含加密密码）定下后不要改，否则旧存档读不出来（会被当作损坏处理）。
- `CreateBackup` 与 Backup On Load 共用同一个 `.bak`；只想手动备份就关掉 Backup On Load。
- 读取存储介质本身失败（IO 异常）时 `Load` 会抛异常而不是返回默认值，避免空数据覆盖玩家存档。
- 加密密码随包体分发，只防普通玩家改档；重要数值仍需服务端校验。
- 仅限主线程。

## 异步

存档较大时把压缩、加密和 IO 放到后台线程。安装了 UniTask 时返回 `UniTask`，否则返回 `Task`。

```csharp
await GameEntry.Storage.PreloadAsync();          // 加载界面预读，之后 Load/Save 不再有 IO
bool ok = await GameEntry.Storage.FlushAsync();  // 写入的是调用时刻的数据，之后的修改留到下次落盘
```

Provider 不支持后台线程（`IsThreadSafe` 为 false，如 PlayerPrefs）或在 WebGL 上时，自动退化为同步执行。

## 数据版本迁移

存档结构变了（改 key 名、换类型）时：把 Inspector 的 Data Version 加 1，并添加一个迁移步骤。打开版本更低的文件时按 `FromVersion` 从小到大依次执行，然后文件升到当前版本；新文件直接是当前版本。

```csharp
[Serializable]
public class GoldToCoinsMigration : IStorageMigration
{
    public int FromVersion => 0;   // 把版本 0 的文件改成版本 1

    public void Migrate(StorageEntity storage, string file)
    {
        storage.Save("coins", storage.Load<int>("gold", file: file) * 10, file);
        storage.DeleteKey("gold", file);
    }
}
```

迁移抛异常时本次打开失败（`Load` 抛出该异常），文件不会被改动。旧结构可以用 `Load<JObject>` 按 JSON 读。

## 场景对象引用与自动存档

- **`StorageId`**：给场景对象一个稳定 id（编辑器下自动生成）。存档里的 `GameObject` / 组件字段存的是这个 id，读取时按 id 找回场景里的对象；没挂 `StorageId` 或对象已不存在时为 null。
- **`StorageAutoSave`**：自动存取所在对象的 Transform，以及同对象上实现了 `IStorageAutoSaveState` 的组件。Start 时读取；切后台/退出落盘前、对象销毁（切场景）时保存。

```csharp
public class Health : MonoBehaviour, IStorageAutoSaveState
{
    private int _hp = 100;

    public void SaveState(StorageEntity storage, string key, string file) => storage.Save(key, _hp, file);
    public void LoadState(StorageEntity storage, string key, string file) => _hp = storage.Load(key, _hp, file);
}
```

不用 `StorageAutoSave` 也可以订阅 `GameEntry.Storage.Saving`，在切后台/退出落盘前把运行时状态 `Save` 进来。

注意：

- 复制（Ctrl+D）带 `StorageId` 的对象后两者 id 相同，运行时会报错，在组件右键菜单选 Regenerate Id。
- 预制体资源本身不带 id；运行时实例化的对象需要代码给 `StorageId.Id` 赋值。
- 只支持引用场景里的对象，不支持引用资源（预制体、Sprite、ScriptableObject），资源请存自己的地址/名字。

## 表格、图片、原始文件

这三类以原始文件存储：不加密、不压缩、立即落盘，方便用外部工具打开。文件名不要与键值文件重名。

```csharp
// 表格：CSV（带 BOM，Excel 可直接打开）
var sheet = GameEntry.Storage.LoadSheet("rank.csv") ?? new StorageSheet();
sheet.SetCell(0, sheet.RowCount, "player");          // (列, 行, 值)
int score = sheet.GetCell<int>(1, 0);
GameEntry.Storage.SaveSheet("rank.csv", sheet);

// 图片：按扩展名存 PNG 或 JPG，贴图需可读
GameEntry.Storage.SaveImage("shot.png", texture);
Texture2D loaded = GameEntry.Storage.LoadImage("shot.png");   // 用完自己 Destroy

// 任意字节
GameEntry.Storage.SaveRaw("replay.bin", bytes);
byte[] data = GameEntry.Storage.LoadRaw("replay.bin");
```

## 扩展

三个接口都用 `[Serializable]` 类 + 无参构造实现，即会出现在 Inspector 下拉里；也可以在第一次读写前用代码设置。

```csharp
// 新平台 / 云存档：实现存储介质（如抖音小游戏的文件系统）
[Serializable]
public class DouyinStorageProvider : IStorageProvider
{
    public bool IsThreadSafe => false;           // Read/Write 能否在后台线程调用
    public bool Exists(string name) { ... }
    public byte[] Read(string name) { ... }      // 不存在返回 null
    public void Write(string name, byte[] data) { ... }
    public void Delete(string name) { ... }
    public IEnumerable<string> GetNames() { ... }
}

// 换序列化：IStorageSerializer（如 MemoryPack）
// 加变换：IStorageTransform（Decode 遇到非法数据必须抛异常，用于触发备份恢复；会在后台线程调用）

GameEntry.Storage.Provider = new DouyinStorageProvider();
GameEntry.Storage.Transforms.Add(new GzipTransform());
GameEntry.Storage.Transforms.Add(new AesTransform("your-password"));
```

内置：`FileStorageProvider`、`PlayerPrefsStorageProvider`、`MemoryStorageProvider`（测试用）。
