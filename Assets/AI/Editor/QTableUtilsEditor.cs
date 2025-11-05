#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;
using AI;
using System.Collections.Generic;
using System.Reflection;

namespace AI.Editor
{
    public static class QTableUtilsEditor
    {
        [MenuItem("Tools/QLearning/Import QTable...")]
        public static void ImportQTable()
        {
            string src = EditorUtility.OpenFilePanel("Select Q-table JSON", "", "json");
            if (string.IsNullOrEmpty(src)) return;

            string json = File.ReadAllText(src);

            // Try to parse metadata
            QTableFile parsed = null;
            try { parsed = JsonUtility.FromJson<QTableFile>(json); } catch { parsed = null; }

            string defaultName = Path.GetFileName(src);
            string savePath = EditorUtility.SaveFilePanel("Save Q-table to persistentDataPath", Application.persistentDataPath, defaultName, "json");
            if (string.IsNullOrEmpty(savePath)) return;

            File.WriteAllText(savePath, json);
            Debug.Log("Imported Q-table to " + savePath);

            // Validate against scene controller if present
            var controller = Object.FindAnyObjectByType<QLearningController>();
            if (controller != null && parsed != null && parsed.metadata != null)
            {
                int expected = 9;
                // try to infer from controller fields (thrustLevels x steerAngles)
                try
                {
                    var t = controller.GetType();
                    var f1 = t.GetField("thrustLevels", BindingFlags.NonPublic | BindingFlags.Instance);
                    var f2 = t.GetField("steerAngles", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f1 != null && f2 != null)
                    {
                        var a1 = f1.GetValue(controller) as System.Array;
                        var a2 = f2.GetValue(controller) as System.Array;
                        if (a1 != null && a2 != null) expected = a1.Length * a2.Length;
                    }
                }
                catch { /* ignore */ }

                if (parsed.metadata.actionCount != expected)
                {
                    EditorUtility.DisplayDialog("QTable Import","Imported Q-table actionCount=" + parsed.metadata.actionCount + ", but scene controller expects " + expected + ". You may need to test in a separate slot or adjust controller mapping.", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("QTable Import","Imported Q-table with actionCount=" + parsed.metadata.actionCount + ". Saved to:\n" + savePath, "OK");
                }
            }
            else
            {
                EditorUtility.DisplayDialog("QTable Import","Imported Q-table saved to:\n" + savePath, "OK");
            }
        }

        [MenuItem("Tools/QLearning/Merge QTables...")]
        public static void MergeQTables()
        {
            string srcA = EditorUtility.OpenFilePanel("Select first Q-table JSON", "", "json");
            if (string.IsNullOrEmpty(srcA)) return;
            string srcB = EditorUtility.OpenFilePanel("Select second Q-table JSON", "", "json");
            if (string.IsNullOrEmpty(srcB)) return;

            string jsonA = File.ReadAllText(srcA);
            string jsonB = File.ReadAllText(srcB);

            QTableFile a = null; QTableFile b = null;
            try { a = JsonUtility.FromJson<QTableFile>(jsonA); } catch { a = null; }
            try { b = JsonUtility.FromJson<QTableFile>(jsonB); } catch { b = null; }

            if (a == null || a.rows == null || a.rows.Count == 0)
            {
                EditorUtility.DisplayDialog("Merge QTables","First file invalid or empty.","OK");
                return;
            }
            if (b == null || b.rows == null || b.rows.Count == 0)
            {
                EditorUtility.DisplayDialog("Merge QTables","Second file invalid or empty.","OK");
                return;
            }

            // basic compatibility check
            if (a.metadata != null && b.metadata != null)
            {
                if (a.metadata.actionCount != b.metadata.actionCount)
                {
                    if (!EditorUtility.DisplayDialog("Merge QTables","ActionCount differs ("+a.metadata.actionCount+" vs "+b.metadata.actionCount+"). Continue?","Yes","No")) return;
                }
                if (a.metadata.encodingVersion != b.metadata.encodingVersion)
                {
                    if (!EditorUtility.DisplayDialog("Merge QTables","Encoding version differs ("+a.metadata.encodingVersion+" vs "+b.metadata.encodingVersion+"). Continue?","Yes","No")) return;
                }
            }

            // merge: average where both exist, keep unique
            var dict = new Dictionary<string, float[]>();
            foreach (var row in a.rows) dict[row.state] = (float[])row.q.Clone();
            foreach (var row in b.rows)
            {
                if (dict.ContainsKey(row.state))
                {
                    var old = dict[row.state];
                    var neu = row.q;
                    if (old.Length == neu.Length)
                    {
                        for (int i=0;i<old.Length;i++) old[i] = (old[i] + neu[i]) * 0.5f;
                        dict[row.state] = old;
                    }
                    else
                    {
                        // incompatible action size: skip
                    }
                }
                else
                {
                    dict[row.state] = (float[])row.q.Clone();
                }
            }

            QTableFile outFile = new QTableFile();
            outFile.metadata = new QTableMetadata();
            if (a.metadata != null) outFile.metadata = a.metadata; // base metadata on first
            outFile.metadata.author = System.Environment.UserName + ";merged";
            outFile.metadata.createdAt = System.DateTime.UtcNow.ToString("o");
            outFile.rows = new List<QStateRow>();
            foreach (var kv in dict) outFile.rows.Add(new QStateRow() { state = kv.Key, q = kv.Value });

            string defaultName = "merged_qtable.json";
            string savePath = EditorUtility.SaveFilePanel("Save merged Q-table to", Application.persistentDataPath, defaultName, "json");
            if (string.IsNullOrEmpty(savePath)) return;
            File.WriteAllText(savePath, JsonUtility.ToJson(outFile, true));
            EditorUtility.DisplayDialog("Merge QTables","Merged Q-table saved to:\n" + savePath, "OK");
            Debug.Log("Merged Q-table saved to " + savePath);
        }
    }
}
#endif
