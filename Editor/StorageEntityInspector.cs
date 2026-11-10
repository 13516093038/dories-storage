using System;
using System.Collections.Generic;
using System.Linq;
using Dories.Storage.Runtime;
using UnityEditor;
using UnityEngine;

namespace Dories.Storage.Editor
{
    [CustomEditor(typeof(StorageEntity))]
    public class StorageEntityInspector : UnityEditor.Editor
    {
        private const string ProviderField = "_provider";
        private const string SerializerField = "_serializer";
        private const string TransformsField = "_transforms";
        private const string MigrationsField = "_migrations";

        private static readonly Dictionary<Type, Type[]> TypesByInterface = new();

        private readonly HashSet<string> _expandedFiles = new();
        private Vector2 _scrollPosition;
        private bool _monitorFoldout = true;

        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, ProviderField, SerializerField, TransformsField, MigrationsField);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                DrawSelector<IStorageProvider>(serializedObject.FindProperty(ProviderField), "Provider", "(默认 File)");
                DrawSelector<IStorageSerializer>(serializedObject.FindProperty(SerializerField), "Serializer", "(默认 Json)");
                DrawList<IStorageTransform>(TransformsField, "Transforms", "落盘前依次执行，读取时逆序执行。压缩应排在加密前面");
                DrawList<IStorageMigration>(MigrationsField, "Migrations", "数据版本迁移步骤，按各自的 FromVersion 执行，与列表顺序无关");
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            DrawMonitor();
        }

        // SerializeReference 只能放可序列化的非 UnityEngine.Object 类型，且下拉创建需要无参构造
        private static Type[] TypesOf<T>()
        {
            if (!TypesByInterface.TryGetValue(typeof(T), out var types))
            {
                types = TypeCache.GetTypesDerivedFrom<T>()
                    .Where(t => !t.IsAbstract && !t.IsGenericType && t.IsSerializable
                                && !typeof(UnityEngine.Object).IsAssignableFrom(t)
                                && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.Name)
                    .ToArray();
                TypesByInterface.Add(typeof(T), types);
            }

            return types;
        }

        private static void DrawSelector<T>(SerializedProperty property, string label, string noneLabel)
        {
            var types = TypesOf<T>();
            // managedReferenceFullTypename 格式为 "程序集名 完整类型名"
            var current = property.managedReferenceFullTypename;
            int index = Array.FindIndex(types, t => $"{t.Assembly.GetName().Name} {t.FullName}" == current) + 1;
            int selected = EditorGUILayout.Popup(label, index, new[] { noneLabel }.Concat(types.Select(t => t.Name)).ToArray());
            if (selected != index)
                property.managedReferenceValue = selected == 0 ? null : Activator.CreateInstance(types[selected - 1]);

            DrawChildren(property);
        }

        // SerializeReference 列表："+" 弹出可选类型，每项显示类型名和它的字段
        private void DrawList<T>(string field, string label, string tooltip)
        {
            var list = serializedObject.FindProperty(field);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent(label, tooltip));
            if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(24f)))
            {
                var menu = new GenericMenu();
                foreach (var type in TypesOf<T>())
                {
                    menu.AddItem(new GUIContent(type.Name), false, () =>
                    {
                        serializedObject.Update();
                        var items = serializedObject.FindProperty(field);
                        items.arraySize++;
                        items.GetArrayElementAtIndex(items.arraySize - 1).managedReferenceValue = Activator.CreateInstance(type);
                        serializedObject.ApplyModifiedProperties();
                    });
                }

                menu.ShowAsContext();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                var typeName = element.managedReferenceFullTypename;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{i + 1}. {(string.IsNullOrEmpty(typeName) ? "(空)" : typeName.Substring(typeName.LastIndexOf('.') + 1))}");
                bool remove = GUILayout.Button("-", EditorStyles.miniButton, GUILayout.Width(24f));
                EditorGUILayout.EndHorizontal();
                if (remove)
                {
                    list.DeleteArrayElementAtIndex(i);
                    break;
                }

                DrawChildren(element);
            }

            EditorGUI.indentLevel--;
        }

        private static void DrawChildren(SerializedProperty property)
        {
            if (property.managedReferenceValue == null)
                return;

            EditorGUI.indentLevel++;
            var end = property.GetEndProperty();
            var child = property.Copy();
            for (bool enter = true; child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end); enter = false)
                EditorGUILayout.PropertyField(child, true);
            EditorGUI.indentLevel--;
        }

        private void DrawMonitor()
        {
            _monitorFoldout = EditorGUILayout.Foldout(_monitorFoldout, "Files", true);
            if (!_monitorFoldout)
                return;

            EditorGUI.indentLevel++;

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("进入 Play Mode 后显示已打开的存储文件。", MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            var entity = (StorageEntity)target;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Provider", entity.Provider.GetType().Name);
            if (entity.Provider is FileStorageProvider fileProvider && GUILayout.Button("打开目录", EditorStyles.miniButton, GUILayout.Width(64f)))
                EditorUtility.RevealInFinder(fileProvider.Root);
            if (GUILayout.Button("Flush", EditorStyles.miniButton, GUILayout.Width(48f)))
                entity.Flush();
            EditorGUILayout.EndHorizontal();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.MaxHeight(280f));
            foreach (var pair in entity.Files)
            {
                var file = pair.Value;
                bool expanded = _expandedFiles.Contains(pair.Key);
                // 有未落盘的修改时标黄
                var color = GUI.color;
                if (file.Dirty)
                    GUI.color = Color.yellow;
                bool expand = EditorGUILayout.Foldout(expanded, $"{pair.Key}  (v{file.DataVersion}, {file.Entries.Count} keys{(file.Dirty ? ", 未落盘" : "")})", true);
                GUI.color = color;
                if (expand != expanded)
                {
                    if (expand) _expandedFiles.Add(pair.Key);
                    else _expandedFiles.Remove(pair.Key);
                }

                if (!expand)
                    continue;

                EditorGUI.indentLevel++;
                foreach (var entry in file.Entries)
                    EditorGUILayout.LabelField(entry.Key, $"{entry.Value.Length} B");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndScrollView();
            EditorGUI.indentLevel--;
        }
    }
}
