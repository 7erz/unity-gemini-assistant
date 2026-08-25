using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System;

public class GeminiDiffWindow : EditorWindow
{
    private string originalCode;
    private string newCode;
    private string assetPath;
    private Action<string> onApply;
    private Action onCancel; // 취소 시 다음 파일로 넘어가기 위한 콜백
    private bool isResolved = false;

    private Vector2 scrollPos;
    private GUIStyle diffStyle;
    private List<DiffLine> diffResults = new List<DiffLine>();

    private struct DiffLine
    {
        public string text;
        public Color bgColor;
        public string prefix;
    }

    public static void ShowWindow(string original, string modified, string path, Action<string> applyCallback, Action cancelCallback)
    {
        GeminiDiffWindow window = GetWindow<GeminiDiffWindow>("Code Diff Preview");
        window.originalCode = original;
        window.newCode = modified;
        window.assetPath = path;
        window.onApply = applyCallback;
        window.onCancel = cancelCallback;
        window.isResolved = false;
        window.CalculateDiff();
        window.Show();
    }

    private void CalculateDiff()
    {
        diffResults.Clear();
        string[] oldLines = originalCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string[] newLines = newCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        int oldIdx = 0;
        int newIdx = 0;

        while (oldIdx < oldLines.Length && newIdx < newLines.Length)
        {
            if (oldLines[oldIdx] == newLines[newIdx])
            {
                diffResults.Add(new DiffLine { text = oldLines[oldIdx], prefix = "  ", bgColor = Color.clear });
                oldIdx++;
                newIdx++;
            }
            else
            {
                int syncOld = -1;
                int syncNew = -1;
                int maxLookahead = 50;

                for (int i = 1; i < maxLookahead; i++)
                {
                    if (newIdx + i < newLines.Length && oldLines[oldIdx] == newLines[newIdx + i]) { syncNew = i; break; }
                    if (oldIdx + i < oldLines.Length && oldLines[oldIdx + i] == newLines[newIdx]) { syncOld = i; break; }
                }

                if (syncNew != -1 && (syncOld == -1 || syncNew <= syncOld))
                {
                    for (int i = 0; i < syncNew; i++)
                        diffResults.Add(new DiffLine { text = newLines[newIdx++], prefix = "+ ", bgColor = new Color(0f, 1f, 0f, 0.2f) });
                }
                else if (syncOld != -1)
                {
                    for (int i = 0; i < syncOld; i++)
                        diffResults.Add(new DiffLine { text = oldLines[oldIdx++], prefix = "- ", bgColor = new Color(1f, 0f, 0f, 0.2f) });
                }
                else
                {
                    diffResults.Add(new DiffLine { text = oldLines[oldIdx++], prefix = "- ", bgColor = new Color(1f, 0f, 0f, 0.2f) });
                    diffResults.Add(new DiffLine { text = newLines[newIdx++], prefix = "+ ", bgColor = new Color(0f, 1f, 0f, 0.2f) });
                }
            }
        }

        while (oldIdx < oldLines.Length) diffResults.Add(new DiffLine { text = oldLines[oldIdx++], prefix = "- ", bgColor = new Color(1f, 0f, 0f, 0.2f) });
        while (newIdx < newLines.Length) diffResults.Add(new DiffLine { text = newLines[newIdx++], prefix = "+ ", bgColor = new Color(0f, 1f, 0f, 0.2f) });
    }

    private void OnGUI()
    {
        if (diffStyle == null)
        {
            diffStyle = new GUIStyle(EditorStyles.label);
            diffStyle.richText = true;
            diffStyle.font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Editor/Fonts/Consolas.ttf");
            if (diffStyle.font == null) diffStyle.wordWrap = false;
        }

        EditorGUILayout.LabelField($"파일 검토: {Path.GetFileName(assetPath)}", EditorStyles.boldLabel);
        GUILayout.Space(10);
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUI.skin.box);

        foreach (var line in diffResults)
        {
            Rect rect = EditorGUILayout.BeginHorizontal();
            if (line.bgColor != Color.clear) EditorGUI.DrawRect(rect, line.bgColor);
            string colorTag = line.prefix == "+ " ? "<color=#66ff66>" : (line.prefix == "- " ? "<color=#ff6666>" : "<color=#ffffff>");
            GUILayout.Label($"{colorTag}{line.prefix}{line.text}</color>", diffStyle);
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
        GUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("적용 및 파일 저장 (Apply)", GUILayout.Height(40)))
        {
            isResolved = true;
            Close();
            onApply?.Invoke(newCode);
        }
        if (GUILayout.Button("취소 (Cancel)", GUILayout.Height(40)))
        {
            isResolved = true;
            Close();
            onCancel?.Invoke();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void OnDestroy()
    {
        // 사용자가 X 버튼을 눌러 창을 강제로 닫았을 때 무한 대기를 막기 위한 안전장치
        if (!isResolved)
        {
            onCancel?.Invoke();
        }
    }
}