using DoNotModify;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

namespace AI
{
    // Trainer simple pour enchainer des épisodes et sauvegarder le meilleur modèle.
    // Attacher à un GameObject en scène.
    public class QLearningTrainer : MonoBehaviour
    {
        [Header("Bindings")]
        public QLearningController controller; // optionnel: auto-find si null

        [Header("Persistence & runtime")]
        public bool AutoLoad = false;
        public bool AutoSaveOnEnd = true;
        public string SaveFileName = "qtable.json";
        public bool AutoRestartOnEnd = true;
        [Tooltip("Time.timeScale pendant l'entraînement")] public float TrainingTimeScale = 1.0f;
        public bool PersistAcrossEpisodes = true;

        [Header("Logging / best model")]
        [Tooltip("Sauver tous les N épisodes (0 = désactivé)")] public int AutoSaveEveryNEpisodes = 0;
        [Tooltip("Taille de la fenêtre pour les moyennes mobiles")] public int MovingAverageWindow = 20;

        [Header("Evolutionary Q-Learning")]
        public bool EvolutionaryEnabled = true;
        [Tooltip("Taille de la population")] public int PopulationSize = 6;
        [Tooltip("Épisodes évalués par candidat")] public int EpisodesPerCandidate = 3;
        [Tooltip("0 = infini")] public int MaxGenerations = 0;
        [Tooltip("Nombre d'élites conservés")] public int ElitesToKeep = 1;
        [Tooltip("Sigma de mutation (relatif)")] [Range(0f,1f)] public float MutationSigma = 0.15f;
        public Vector2 AlphaRange = new Vector2(0.05f, 1.0f);
        public Vector2 GammaRange = new Vector2(0.5f, 0.9999f);
        public Vector2 EpsilonStartRange = new Vector2(0.01f, 1.0f);
        public Vector2 EpsilonDecayRange = new Vector2(0.9f, 0.99999f);
        public Vector2 MinEpsilonRange = new Vector2(0.0f, 0.2f);

        [Header("Best saving")]
        [Tooltip("Si actif, n’enregistre qu’un seul meilleur modèle (global) dans SaveFileName.")] public bool SaveOnlyOneBest = true;

        private GameManager _gm;
        private bool _handledGameOver = false;
        private int _episodeCount = 0;

        // historiques (scores et victoires)
        private readonly List<int> _scores = new List<int>();
        private readonly List<int> _waypoints = new List<int>();
        private readonly List<bool> _wins = new List<bool>();

        // best snapshot (global)
        private float _bestWinRate = -1f;
        private float _bestAvgScore = float.NegativeInfinity;
        private float _bestAvgWay = float.NegativeInfinity;
        private string BestFileName => System.IO.Path.GetFileNameWithoutExtension(SaveFileName) + ".best.json";
        private string BestWRFileName => System.IO.Path.GetFileNameWithoutExtension(SaveFileName) + ".best.wr.json";
        private string BestScoreFileName => System.IO.Path.GetFileNameWithoutExtension(SaveFileName) + ".best.score.json";
        private string BestWPFileName => System.IO.Path.GetFileNameWithoutExtension(SaveFileName) + ".best.wp.json";

        private static QLearningTrainer _instance;

        // Evolutionary state
        private class Candidate
        {
            public int generation;
            public int index;
            public float alpha, gamma, epsStart, epsDecay, minEps;
            public int episodesRun;
            public int wins;
            public int games;
            public float cumulativeScore;
            public float cumulativeWaypoints;
            public float bestScore = float.NegativeInfinity;
            public string fileName;
            public float WinRate => games > 0 ? (float)wins / games : 0f;
            public float AvgScore => games > 0 ? cumulativeScore / games : 0f;
            public float AvgWaypoints => games > 0 ? cumulativeWaypoints / games : 0f;
            public Candidate Clone(int newGen, int newIdx)
            {
                return new Candidate
                {
                    generation = newGen,
                    index = newIdx,
                    alpha = alpha,
                    gamma = gamma,
                    epsStart = epsStart,
                    epsDecay = epsDecay,
                    minEps = minEps,
                    fileName = $"qtable_g{newGen}_c{newIdx}.json"
                };
            }
        }
        private List<Candidate> _population;
        private int _currentGen;
        private int _currentIdx;
        private Candidate _current;
        private Candidate _bestEver;

        private void Awake()
        {
            if (PersistAcrossEpisodes)
            {
                if (_instance != null && _instance != this)
                {
                    Destroy(gameObject);
                    return;
                }
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Start()
        {
            _gm = GameManager.Instance;
            if (controller == null)
            {
                controller = Object.FindAnyObjectByType<QLearningController>();
            }
            if (controller != null)
            {
                controller.SaveFileName = SaveFileName;
                if (AutoLoad)
                {
                    string path = System.IO.Path.Combine(Application.persistentDataPath, SaveFileName);
                    if (System.IO.File.Exists(path))
                    {
                        QLearningAgent.VerboseLoad = true; // logs détaillés sur le chargement
                        Debug.Log($"[QL-Trainer] AutoLoad: chargement depuis {path}");
                        controller.LoadAgent();
                        Debug.Log($"[QL-Trainer] AutoLoad OK: {path}");
                        // Log des métadonnées de la Q-table
                        try
                        {
                            string json = System.IO.File.ReadAllText(path);
                            QTableFile file = JsonUtility.FromJson<QTableFile>(json);
                            if (file != null && file.metadata != null)
                            {
                                var md = file.metadata;
                                int rows = (file.rows != null) ? file.rows.Count : 0;
                                Debug.Log($"[QL-Trainer] AutoLoad META: actionCount={md.actionCount} alpha={md.alpha:F6} gamma={md.gamma:F6} epsilon={md.epsilon:F6} epsDecay={md.epsilonDecay:F6} minEps={md.minEpsilon:F6} encoding={md.encodingVersion} author={md.author} createdAt={md.createdAt} rows={rows}");
                            }
                            else
                            {
                                Debug.Log("[QL-Trainer] AutoLoad META: format sans metadata ou invalide");
                            }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogWarning("[QL-Trainer] AutoLoad META lecture échouée: " + e.Message);
                        }
                    }
                    else Debug.Log($"[QL-Trainer] AutoLoad ignoré, fichier absent: {path}");
                }
            }
            Time.timeScale = TrainingTimeScale > 0 ? TrainingTimeScale : 1.0f;

            if (PersistAcrossEpisodes)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
            }

            // Evolutionary init
            if (EvolutionaryEnabled)
            {
                if (_population == null || _population.Count == 0) BuildInitialPopulation();
                BeginOrResumeCurrentCandidate();
            }
        }

        private void OnDestroy()
        {
            if (PersistAcrossEpisodes)
                SceneManager.sceneLoaded -= OnSceneLoaded;
            if (GameManager.Instance != null && GameManager.Instance.IsGameFinished())
                Time.timeScale = 1.0f;
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            _gm = GameManager.Instance;
            if (controller == null)
            {
                controller = Object.FindAnyObjectByType<QLearningController>();
                if (controller != null) controller.SaveFileName = SaveFileName;
            }
            _handledGameOver = false;
            Time.timeScale = TrainingTimeScale > 0 ? TrainingTimeScale : 1.0f;

            if (EvolutionaryEnabled)
            {
                BeginOrResumeCurrentCandidate();
            }
        }

        private void Update()
        {
            if (controller == null) return;

            // hotkeys
            if (Input.GetKeyDown(KeyCode.K)) controller.SaveAgent();
            if (Input.GetKeyDown(KeyCode.L)) { QLearningAgent.VerboseLoad = true; Debug.Log("[QL-Trainer] Hotkey L: LoadAgent()"); controller.LoadAgent(); }
            if (Input.GetKeyDown(KeyCode.P)) controller.TrainingMode = !controller.TrainingMode;
            if (Input.GetKeyDown(KeyCode.R)) SceneManager.LoadScene(SceneManager.GetActiveScene().name);

            if (_gm == null) return;
            if (_gm.IsGameFinished())
            {
                if (_handledGameOver) return;
                _handledGameOver = true;

                // log et récompense terminale
                var ship = _gm.GetSpaceShipForController(controller);
                int owner = ship != null ? ship.Owner : 0;
                int opp = owner == 0 ? 1 : 0;
                int score = _gm.GetScoreForPlayer(owner);
                int oppScore = _gm.GetScoreForPlayer(opp);
                int wps = _gm.GetWayPointScoreForPlayer(owner);
                bool win = score > oppScore;

                _episodeCount++;
                _scores.Add(score);
                _waypoints.Add(wps);
                _wins.Add(win);

                if (controller.TrainingMode)
                {
                    controller.ApplyTerminalResult(win);
                }

                // evolutionary accounting
                if (EvolutionaryEnabled && _current != null)
                {
                    _current.games++;
                    if (win) _current.wins++;
                    _current.cumulativeScore += score;
                    _current.cumulativeWaypoints += wps;
                    if (score > _current.bestScore) _current.bestScore = score;
                    _current.episodesRun++;
                }

                // moving averages
                int avail = (EvolutionaryEnabled && _current != null) ? _current.episodesRun : _scores.Count;
                int n = Mathf.Min(MovingAverageWindow > 0 ? MovingAverageWindow : avail, avail);
                float avgScore = 0f; float avgWay = 0f; int winCount = 0;
                int start = Mathf.Max(0, _scores.Count - n);
                for (int i = start; i < _scores.Count; i++)
                {
                    avgScore += _scores[i];
                    avgWay += _waypoints[i];
                }
                int wstart = Mathf.Max(0, _wins.Count - n);
                for (int i = wstart; i < _wins.Count; i++)
                {
                    if (_wins[i]) winCount++;
                }
                if (n > 0) { avgScore /= n; avgWay /= n; }
                float winRate = n > 0 ? (float)winCount / n : 0f;
                string genInfo = EvolutionaryEnabled && _current != null ? $"G={_currentGen} C={_current.index} EpCand={_current.episodesRun}/{Mathf.Max(1, EpisodesPerCandidate)}" : "G=- C=- EpCand=-/-";
                Debug.Log($"Episode {_episodeCount}: {genInfo}, score={score}, waypoints={wps}, win={(win ? 1 : 0)}, MA(n={n}) score={avgScore:F2} way={avgWay:F2} winRate={winRate:P1}");

                // autosave courant: uniquement si AutoSaveOnEnd
                if (AutoSaveOnEnd && AutoSaveEveryNEpisodes > 0 && (_episodeCount % AutoSaveEveryNEpisodes) == 0)
                {
                    controller.SaveAgent();
                }
                if (AutoSaveOnEnd)
                {
                    controller.SaveAgent();
                }

                // best snapshots (best-only si SaveOnlyOneBest)
                if (winRate > _bestWinRate + 1e-4f || (Mathf.Approximately(winRate, _bestWinRate) && avgScore > _bestAvgScore))
                {
                    _bestWinRate = winRate;
                    if (avgScore > _bestAvgScore) _bestAvgScore = avgScore;
                    // si best-only -> écrire dans SaveFileName, sinon BestFileName
                    string target = SaveOnlyOneBest ? SaveFileName : BestFileName;
                    SaveModelWithMetrics(target, winRate, avgScore, avgWay, _episodeCount);
                }
                if (!SaveOnlyOneBest)
                {
                    // best par winrate
                    if (winRate > (_bestWinRate + 1e-4f) || (_bestWinRate < 0f && winRate > 0f))
                    {
                        SaveModelWithMetrics(BestWRFileName, winRate, avgScore, avgWay, _episodeCount);
                    }
                    // best par score moyen
                    if (avgScore > _bestAvgScore + 1e-4f)
                    {
                        _bestAvgScore = avgScore;
                        SaveModelWithMetrics(BestScoreFileName, winRate, avgScore, avgWay, _episodeCount);
                    }
                    // best par waypoints moyen
                    if (avgWay > _bestAvgWay + 1e-4f)
                    {
                        _bestAvgWay = avgWay;
                        SaveModelWithMetrics(BestWPFileName, winRate, avgScore, avgWay, _episodeCount);
                    }
                }

                // Evolution step
                if (EvolutionaryEnabled)
                {
                    HandleEvolutionProgression();
                }

                // restart si demandé
                if (AutoRestartOnEnd && controller.TrainingMode)
                {
                    SceneManager.LoadScene(SceneManager.GetActiveScene().name);
                }
                else
                {
                    Time.timeScale = 1.0f;
                }
            }
            else
            {
                _handledGameOver = false;
            }
        }

        private void SaveModelWithMetrics(string modelFile, float winRate, float avgScore, float avgWay, int episodes)
        {
            if (controller == null) return;
            string prev = controller.SaveFileName;
            controller.SaveFileName = modelFile;
            controller.SaveAgent();
            controller.SaveFileName = prev;
            // write sidecar metrics json
            var metrics = new BestMetrics
            {
                movingWinRate = winRate,
                movingAvgScore = avgScore,
                movingAvgWaypoints = avgWay,
                episodes = episodes,
                timestamp = System.DateTime.UtcNow.ToString("o"),
                generation = (EvolutionaryEnabled && _current != null) ? _current.generation : -1,
                candidate = (EvolutionaryEnabled && _current != null) ? _current.index : -1
            };
            string json = JsonUtility.ToJson(metrics, true);
            string path = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, modelFile + ".metrics.json");
            System.IO.File.WriteAllText(path, json);
            Debug.Log($"[BEST] Saved snapshot {modelFile} with metrics -> WR={winRate:P1} Score={avgScore:F2} WP={avgWay:F2}");
        }

        [System.Serializable]
        private class BestMetrics
        {
            public float movingWinRate;
            public float movingAvgScore;
            public float movingAvgWaypoints;
            public int episodes;
            public string timestamp;
            public int generation;
            public int candidate;
        }

        // ---- Evolutionary helpers ----
        private void BuildInitialPopulation()
        {
            _population = new List<Candidate>();
            _currentGen = 0; _currentIdx = 0; _bestEver = null;
            int sz = Mathf.Max(1, PopulationSize);
            for (int i = 0; i < sz; i++)
            {
                Candidate c = new Candidate
                {
                    generation = _currentGen,
                    index = i,
                    alpha = RandomInRange(AlphaRange),
                    gamma = RandomInRange(GammaRange),
                    epsStart = RandomInRange(EpsilonStartRange),
                    epsDecay = RandomInRange(EpsilonDecayRange),
                    minEps = RandomInRange(MinEpsilonRange),
                    fileName = $"qtable_g{_currentGen}_c{i}.json"
                };
                _population.Add(c);
            }
            _current = _population[_currentIdx];
            Debug.Log($"[EVOL] Initial population Gen={_currentGen} Size={_population.Count}");
        }

        private void BeginOrResumeCurrentCandidate()
        {
            if (!EvolutionaryEnabled) return;
            if (_population == null || _population.Count == 0) BuildInitialPopulation();
            _currentIdx = Mathf.Clamp(_currentIdx, 0, _population.Count - 1);
            _current = _population[_currentIdx];

            if (controller == null) return;
            controller.SaveFileName = _current.fileName;
            controller.TrainingMode = true;
            bool reset = _current.episodesRun == 0;
            controller.ApplyHyperParamsAndReset(_current.alpha, _current.gamma, _current.epsStart, _current.epsDecay, _current.minEps, reset);
            if (!reset)
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, _current.fileName);
                if (System.IO.File.Exists(path)) controller.LoadAgent();
                else Debug.Log($"[EVOL] Pas de Q-table existante pour ce candidat (fresh): {path}");
            }
        }

        private void HandleEvolutionProgression()
        {
            if (_current == null) return;

            // best-ever: on sauve toujours le meilleur candidat (best-only)
            if (_bestEver == null || IsBetter(_current, _bestEver))
            {
                _bestEver = _current;
                string prev = controller.SaveFileName;
                controller.SaveFileName = _current.fileName;
                controller.SaveAgent();
                controller.SaveFileName = prev;
                TryCopyCandidateToPublic(_current.fileName, SaveFileName);
                Debug.Log($"[EVOL] New BEST -> Gen {_current.generation} Cand {_current.index} WR={_current.WinRate:P1} Avg={_current.AvgScore:F2}");
            }

            if (_current.episodesRun >= Mathf.Max(1, EpisodesPerCandidate))
            {
                // fin de candidat: ne sauvegarder le fichier candidat que si AutoSaveOnEnd
                if (AutoSaveOnEnd)
                {
                    string prev = controller.SaveFileName;
                    controller.SaveFileName = _current.fileName;
                    controller.SaveAgent();
                    controller.SaveFileName = prev;
                }

                _currentIdx++;
                if (_currentIdx >= _population.Count)
                {
                    // fin de génération
                    if (MaxGenerations > 0 && _currentGen + 1 >= MaxGenerations)
                    {
                        Debug.Log("[EVOL] MaxGenerations atteint. Arrêt de l'évolution.");
                        EvolutionaryEnabled = false;
                        return;
                    }
                    _population = BreedNextGeneration(_population, _currentGen + 1);
                    _currentGen++;
                    _currentIdx = 0;
                    Debug.Log($"[EVOL] New Generation = {_currentGen}");
                }
                _current = _population[_currentIdx];
            }
        }

        private List<Candidate> BreedNextGeneration(List<Candidate> prev, int newGen)
        {
            // tri par winrate puis avg score puis avg waypoints
            prev.Sort((a, b) =>
            {
                int c = b.WinRate.CompareTo(a.WinRate);
                if (c != 0) return c;
                c = b.AvgScore.CompareTo(a.AvgScore);
                if (c != 0) return c;
                return b.AvgWaypoints.CompareTo(a.AvgWaypoints);
            });

            List<Candidate> next = new List<Candidate>();
            int elites = Mathf.Clamp(ElitesToKeep, 0, Mathf.Min(prev.Count, Mathf.Max(1, PopulationSize)));
            for (int i = 0; i < elites; i++)
            {
                Candidate e = prev[i].Clone(newGen, next.Count);
                // reset stats pour la nouvelle génération
                e.episodesRun = 0; e.wins = 0; e.games = 0; e.cumulativeScore = 0; e.bestScore = float.NegativeInfinity;
                next.Add(e);
            }

            int parentPool = Mathf.Max(1, prev.Count / 2);
            System.Random rnd = new System.Random();
            while (next.Count < Mathf.Max(1, PopulationSize))
            {
                Candidate p = prev[rnd.Next(parentPool)];
                Candidate child = p.Clone(newGen, next.Count);
                Mutate(child);
                child.episodesRun = 0; child.wins = 0; child.games = 0; child.cumulativeScore = 0; child.bestScore = float.NegativeInfinity;
                next.Add(child);
            }
            return next;
        }

        private void Mutate(Candidate c)
        {
            float Jitter(float value, Vector2 range, float sigma)
            {
                float u1 = Mathf.Clamp01(Random.value);
                float u2 = Mathf.Clamp01(Random.value);
                float z = Mathf.Sqrt(-2.0f * Mathf.Log(Mathf.Max(1e-6f, u1))) * Mathf.Cos(2.0f * Mathf.PI * u2);
                float v = value * (1.0f + z * sigma);
                return Mathf.Clamp(v, range.x, range.y);
            }
            c.alpha = Jitter(c.alpha, AlphaRange, MutationSigma);
            c.gamma = Jitter(c.gamma, GammaRange, MutationSigma);
            c.epsStart = Jitter(c.epsStart, EpsilonStartRange, MutationSigma);
            c.epsDecay = Jitter(c.epsDecay, EpsilonDecayRange, MutationSigma);
            c.minEps = Jitter(c.minEps, MinEpsilonRange, MutationSigma);
        }

        private bool IsBetter(Candidate a, Candidate b)
        {
            if (b == null) return true;
            if (!Mathf.Approximately(a.WinRate, b.WinRate)) return a.WinRate > b.WinRate;
            return a.AvgScore > b.AvgScore;
        }

        private void TryCopyCandidateToPublic(string candidateFile, string publicFile)
        {
            try
            {
                string src = System.IO.Path.Combine(Application.persistentDataPath, candidateFile);
                string dst = System.IO.Path.Combine(Application.persistentDataPath, publicFile);
                if (System.IO.File.Exists(src))
                {
                    System.IO.File.Copy(src, dst, true);
                }
            }
            catch { }
        }

        private float RandomInRange(Vector2 r)
        {
            return Random.Range(r.x, r.y);
        }
    }
}
