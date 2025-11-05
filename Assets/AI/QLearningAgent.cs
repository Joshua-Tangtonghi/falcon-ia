using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AI
{
    [Serializable]
    public class QStateRow {
        public string state;
        public float[] q;
    }

    [Serializable]
    public class QTableMetadata {
        public int actionCount;
        public float alpha;
        public float gamma;
        public float epsilon;
        public float epsilonDecay;
        public float minEpsilon;
        public string encodingVersion;
        public string author;
        public string createdAt;
    }

    [Serializable]
    public class QTableFile {
        public QTableMetadata metadata;
        public List<QStateRow> rows = new List<QStateRow>();
    }

    [Serializable]
    public class QTableSerializable {
        public List<QStateRow> rows = new List<QStateRow>();
    }

    public class QLearningAgent
    {
        private Dictionary<string, float[]> _qTable = new Dictionary<string, float[]>();
        public int ActionCount { get; private set; } = 9;

        // hyperparams
        public float Alpha { get; set; } = 0.5f;
        public float Gamma { get; set; } = 0.99f;
        public float Epsilon { get; set; } = 0.1f;
        public float EpsilonDecay { get; set; } = 0.9995f;
        public float MinEpsilon { get; set; } = 0.01f;

        private System.Random _rnd = new System.Random();

        public void Initialize(int actionCount, float alpha = 0.5f, float gamma = 0.99f, float epsilon = 0.2f)
        {
            ActionCount = Math.Max(1, actionCount);
            Alpha = alpha; Gamma = gamma; Epsilon = epsilon;
            _qTable = new Dictionary<string, float[]>();
        }

        private float[] EnsureQ(string state)
        {
            if (!_qTable.ContainsKey(state))
            {
                float[] arr = new float[ActionCount];
                for (int i = 0; i < ActionCount; i++) arr[i] = 0f;
                _qTable[state] = arr;
            }
            return _qTable[state];
        }

        public int ChooseAction(string state, bool greedy = false)
        {
            float[] q = EnsureQ(state);
            if (!greedy && _rnd.NextDouble() < Epsilon)
            {
                return _rnd.Next(ActionCount);
            }
            // choose best
            int best = 0; float bestV = q[0];
            for (int i = 1; i < q.Length; i++)
            {
                if (q[i] > bestV) { bestV = q[i]; best = i; }
            }
            return best;
        }

        public void Learn(string state, int action, float reward, string nextState, bool done = false)
        {
            if (action < 0) return;
            float[] q = EnsureQ(state);
            float predict = q[action];
            float target = reward;
            if (!done)
            {
                float[] qnext = EnsureQ(nextState);
                float maxNext = qnext[0];
                for (int i = 1; i < qnext.Length; i++) if (qnext[i] > maxNext) maxNext = qnext[i];
                target += Gamma * maxNext;
            }
            q[action] = predict + Alpha * (target - predict);
            // decay epsilon slightly
            Epsilon = Math.Max(MinEpsilon, Epsilon * EpsilonDecay);
        }

        public void Save(string filename)
        {
            try
            {
                QTableFile file = new QTableFile();
                file.metadata = new QTableMetadata()
                {
                    actionCount = this.ActionCount,
                    alpha = this.Alpha,
                    gamma = this.Gamma,
                    epsilon = this.Epsilon,
                    epsilonDecay = this.EpsilonDecay,
                    minEpsilon = this.MinEpsilon,
                    encodingVersion = "v1",
                    author = System.Environment.UserName,
                    createdAt = DateTime.UtcNow.ToString("o")
                };
                foreach (var kv in _qTable)
                {
                    file.rows.Add(new QStateRow() { state = kv.Key, q = kv.Value });
                }
                string json = JsonUtility.ToJson(file, true);
                File.WriteAllText(Path.Combine(Application.persistentDataPath, filename), json);
                Debug.Log("Q-table saved to " + Path.Combine(Application.persistentDataPath, filename) + " (with metadata)");
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to save Q-table: " + e.Message);
            }
        }

        public void Load(string filename)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, filename);
                if (!File.Exists(path)) { Debug.LogWarning("Q-table file not found: " + path); return; }
                string json = File.ReadAllText(path);
                // try to parse as new wrapper
                QTableFile file = JsonUtility.FromJson<QTableFile>(json);
                if (file != null && file.rows != null && file.rows.Count > 0)
                {
                    // populate
                    _qTable = new Dictionary<string, float[]>();
                    foreach (var row in file.rows)
                    {
                        if (row.q == null) continue;
                        _qTable[row.state] = row.q;
                    }
                    // if metadata present, adopt actionCount and hyperparams
                    if (file.metadata != null)
                    {
                        this.ActionCount = file.metadata.actionCount;
                        this.Alpha = file.metadata.alpha;
                        this.Gamma = file.metadata.gamma;
                        this.Epsilon = file.metadata.epsilon;
                        this.EpsilonDecay = file.metadata.epsilonDecay;
                        this.MinEpsilon = file.metadata.minEpsilon;
                        Debug.Log($"Loaded Q-table (metadata) actionCount={this.ActionCount} author={file.metadata.author} createdAt={file.metadata.createdAt}");
                    }
                    else
                    {
                        Debug.Log("Loaded Q-table (no metadata) from " + path);
                    }
                }
                else
                {
                    // fallback: old format
                    QTableSerializable ser = JsonUtility.FromJson<QTableSerializable>(json);
                    _qTable = new Dictionary<string, float[]>();
                    if (ser != null && ser.rows != null)
                    {
                        foreach (var row in ser.rows)
                        {
                            if (row.q == null) continue;
                            _qTable[row.state] = row.q;
                        }
                        Debug.Log("Loaded legacy Q-table from " + path);
                    }
                    else
                    {
                        Debug.LogWarning("Unrecognized Q-table file format: " + path);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to load Q-table: " + e.Message);
            }
        }
    }
}
