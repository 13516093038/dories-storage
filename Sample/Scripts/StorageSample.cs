using Dories.Storage.Runtime;
using UnityEngine;

namespace Dories.Storage.Sample
{
    /// <summary>
    /// 存储示例：键值存取、存档槽、备份回档、表格、截图，以及方块位置的自动存档。
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

        private void Start()
        {
            _shot = _storage.LoadImage(ShotFile);
        }

        private void Update()
        {
            _cube.Translate(new Vector3(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical")) * (_moveSpeed * Time.deltaTime));
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 360, Screen.height - 20));

            GUILayout.Label($"当前存档槽: {_slot}    所有文件: {string.Join(", ", _storage.GetFiles())}");
            GUILayout.BeginHorizontal();
            for (int i = 1; i <= 3; i++)
                if (GUILayout.Button($"slot{i}"))
                    _slot = $"slot{i}.dat";
            if (GUILayout.Button("删除当前槽"))
                _storage.DeleteFile(_slot);
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
                _message = _storage.RestoreBackup(_slot) ? "已回档" : "没有备份";
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("记一条排行（CSV）"))
                AppendRank(gold);
            if (GUILayout.Button("截图（PNG）"))
                SaveShot();
            GUILayout.EndHorizontal();

            GUILayout.Label(_message);
            if (_shot != null)
                GUILayout.Label(_shot, GUILayout.Width(160), GUILayout.Height(90));

            GUILayout.EndArea();
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
