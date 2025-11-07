using DoNotModify;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

namespace AI
{
    public class QLearningTrainer : MonoBehaviour
    {
        [Header("Bindings")]
        public QLearningController controller;

        [Header("Persistence & runtime")]
        public bool AutoLoad = false;
        public bool AutoSaveOnEnd = true;
        public string SaveFileName = "qtable.json";
        public bool AutoRestartOnEnd = true;
        [Tooltip("Time.timeScale pendant l'entraînement")] public float TrainingTimeScale = 30.0f; // x30 vitesse
        public bool PersistAcrossEpisodes = true;

        [Header("Logging / best model")]
        [Tooltip("Sauver tous les N épisodes (0 = désactivé)")] public int AutoSaveEveryNEpisodes = 0;
        [Tooltip("Taille de la fenêtre pour les moyennes mobiles")] public int MovingAverageWindow = 10; // fenêtre plus petite

        [Header("Evolutionary Q-Learning")]
        public bool EvolutionaryEnabled = true;
        [Tooltip("Taille de la population")] public int PopulationSize = 4; // réduit à 4 pour tester plus vite
        [Tooltip("Épisodes évalués par candidat")] public int EpisodesPerCandidate = 200; // AUGMENTÉ : besoin de beaucoup plus d'épisodes pour apprendre
        [Tooltip("0 = infini")] public int MaxGenerations = 5; // limiter à 5 générations
        [Tooltip("Nombre d'élites conservés")] public int ElitesToKeep = 2; // garder les 2 meilleurs
        [Tooltip("Sigma de mutation (relatif)")] [Range(0f,1f)] public float MutationSigma = 0.2f; // mutation plus agressive

        [Header("Hyperparameter Ranges")]
        public Vector2 AlphaRange = new Vector2(0.5f, 0.7f); // resserré autour de 0.6
        public Vector2 GammaRange = new Vector2(0.97f, 0.995f); // resserré autour de 0.98-0.99
        public Vector2 EpsilonStartRange = new Vector2(0.5f, 0.7f); // exploration modérée
        public Vector2 EpsilonDecayRange = new Vector2(0.9997f, 0.99995f); // decay lent pour continuer à explorer
        public Vector2 MinEpsilonRange = new Vector2(0.02f, 0.05f); // min epsilon légèrement plus élevé

        [Header("Reward Evolution")]
        public bool EvolveRewards = true;
        public Vector2 RewardPerWaypointRange = new Vector2(0.8f, 2.0f); // favoriser waypoints
        public Vector2 RewardPerScoreRange = new Vector2(1.0f, 2.5f); // score important
        public Vector2 RewardForHitRange = new Vector2(2.0f, 5.0f); // récompense élevée pour toucher l'ennemi
        public Vector2 RewardForShockwaveRange = new Vector2(0.0f, 0.15f); // shockwave peu récompensée
        public Vector2 PenaltyOnHitRange = new Vector2(-1.5f, -0.3f); // pénalité modérée si touché
        public Vector2 LivingPenaltyRange = new Vector2(-0.02f, -0.005f); // pénalité de vie faible
        public Vector2 TerminalWinRewardRange = new Vector2(5.0f, 15.0f); // grosse récompense pour victoire
        public Vector2 TerminalLossPenaltyRange = new Vector2(-8.0f, -2.0f); // grosse pénalité pour défaite

        [Header("Best saving")]
        public bool SaveOnlyOneBest = true;

        private GameManager _gm;
        private bool _handledGameOver = false;
        private int _episodeCount = 0;

        private readonly List<int> _scores = new List<int>();
        private readonly List<int> _waypoints = new List<int>();
        private readonly List<bool> _wins = new List<bool>();

        private float _bestWinRate = -1f;
        private float _bestAvgScore = float.NegativeInfinity;
        private float _bestAvgWay = float.NegativeInfinity;
        private string BestFileName => System.IO.Path.GetFileNameWithoutExtension(SaveFileName) + ".best.json";

        private static QLearningTrainer _instance;

        private class Candidate
        {
            public int generation, index;
            public float alpha, gamma, epsStart, epsDecay, minEps;
            public float rewardPerWaypoint, rewardPerScore, rewardForHit, rewardForShockwave;
            public float penaltyOnHit, livingPenalty, terminalWinReward, terminalLossPenalty;
            public int episodesRun, wins, games;
            public float cumulativeScore, cumulativeWaypoints;
            public string fileName;
            public float WinRate => games > 0 ? (float)wins / games : 0f;
            public float AvgScore => games > 0 ? cumulativeScore / games : 0f;
            public float AvgWaypoints => games > 0 ? cumulativeWaypoints / games : 0f;

            public Candidate Clone(int newGen, int newIdx)
            {
                return new Candidate
                {
                    generation = newGen, index = newIdx,
                    alpha = alpha, gamma = gamma, epsStart = epsStart, epsDecay = epsDecay, minEps = minEps,
                    rewardPerWaypoint = rewardPerWaypoint, rewardPerScore = rewardPerScore,
                    rewardForHit = rewardForHit, rewardForShockwave = rewardForShockwave,
                    penaltyOnHit = penaltyOnHit, livingPenalty = livingPenalty,
                    terminalWinReward = terminalWinReward, terminalLossPenalty = terminalLossPenalty,
                    fileName = $"qtable_g{newGen}_c{newIdx}.json"
                };
            }
        }

        private List<Candidate> _population;
        private int _currentGen, _currentIdx;
        private Candidate _current, _bestEver;

        private void Awake()
        {
            if (PersistAcrossEpisodes)
            {
                if (_instance != null && _instance != this) { Destroy(gameObject); return; }
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Start()
        {
            _gm = GameManager.Instance;
            if (controller == null) controller = Object.FindAnyObjectByType<QLearningController>();
            if (controller != null) controller.SaveFileName = SaveFileName;
            Time.timeScale = TrainingTimeScale > 0 ? TrainingTimeScale : 1.0f;
            if (PersistAcrossEpisodes) SceneManager.sceneLoaded += OnSceneLoaded;
            if (EvolutionaryEnabled)
            {
                if (_population == null || _population.Count == 0) BuildInitialPopulation();
                BeginOrResumeCurrentCandidate();
            }
        }

        private void OnDestroy()
        {
            if (PersistAcrossEpisodes) SceneManager.sceneLoaded -= OnSceneLoaded;
            if (GameManager.Instance != null && GameManager.Instance.IsGameFinished()) Time.timeScale = 1.0f;
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
            if (EvolutionaryEnabled) BeginOrResumeCurrentCandidate();
        }

        private void Update()
        {
            if (controller == null) return;
            if (Input.GetKeyDown(KeyCode.K)) controller.SaveAgent();
            if (Input.GetKeyDown(KeyCode.L)) controller.LoadAgent();
            if (Input.GetKeyDown(KeyCode.P)) controller.TrainingMode = !controller.TrainingMode;
            if (Input.GetKeyDown(KeyCode.R)) SceneManager.LoadScene(SceneManager.GetActiveScene().name);

            if (_gm == null) return;
            if (_gm.IsGameFinished())
            {
                if (_handledGameOver) return;
                _handledGameOver = true;

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

                if (controller.TrainingMode) controller.ApplyTerminalResult(win);

                if (EvolutionaryEnabled && _current != null)
                {
                    _current.games++;
                    if (win) _current.wins++;
                    _current.cumulativeScore += score;
                    _current.cumulativeWaypoints += wps;
                    _current.episodesRun++;
                }

                int avail = (EvolutionaryEnabled && _current != null) ? _current.episodesRun : _scores.Count;
                int n = Mathf.Min(MovingAverageWindow > 0 ? MovingAverageWindow : avail, avail);
                float avgScore = 0f, avgWay = 0f; int winCount = 0;
                int start = Mathf.Max(0, _scores.Count - n);
                for (int i = start; i < _scores.Count; i++) { avgScore += _scores[i]; avgWay += _waypoints[i]; }
                int wstart = Mathf.Max(0, _wins.Count - n);
                for (int i = wstart; i < _wins.Count; i++) { if (_wins[i]) winCount++; }
                if (n > 0) { avgScore /= n; avgWay /= n; }
                float winRate = n > 0 ? (float)winCount / n : 0f;

                string genInfo = EvolutionaryEnabled && _current != null ? $"G={_currentGen} C={_current.index} EpCand={_current.episodesRun}/{EpisodesPerCandidate}" : "";
                Debug.Log($"Episode {_episodeCount}: {genInfo}, score={score}, waypoints={wps}, win={(win ? 1 : 0)}, MA(n={n}) score={avgScore:F2} way={avgWay:F2} winRate={winRate:P1}");

                if (winRate > _bestWinRate + 1e-4f || (Mathf.Approximately(winRate, _bestWinRate) && avgScore > _bestAvgScore))
                {
                    _bestWinRate = winRate;
                    if (avgScore > _bestAvgScore) _bestAvgScore = avgScore;
                    SaveModelWithMetrics(SaveFileName, winRate, avgScore, avgWay, _episodeCount);
                }

                if (EvolutionaryEnabled) HandleEvolutionProgression();

                if (AutoRestartOnEnd && controller.TrainingMode)
                    SceneManager.LoadScene(SceneManager.GetActiveScene().name);
                else
                    Time.timeScale = 1.0f;
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
            Debug.Log($"[BEST] Saved {modelFile} -> WR={winRate:P1} Score={avgScore:F2} WP={avgWay:F2}");
        }

        private void BuildInitialPopulation()
        {
            _population = new List<Candidate>();
            _currentGen = 0; _currentIdx = 0; _bestEver = null;
            for (int i = 0; i < PopulationSize; i++)
            {
                Candidate c = new Candidate
                {
                    generation = 0, index = i,
                    alpha = Random.Range(AlphaRange.x, AlphaRange.y),
                    gamma = Random.Range(GammaRange.x, GammaRange.y),
                    epsStart = Random.Range(EpsilonStartRange.x, EpsilonStartRange.y),
                    epsDecay = Random.Range(EpsilonDecayRange.x, EpsilonDecayRange.y),
                    minEps = Random.Range(MinEpsilonRange.x, MinEpsilonRange.y),
                    rewardPerWaypoint = Random.Range(RewardPerWaypointRange.x, RewardPerWaypointRange.y),
                    rewardPerScore = Random.Range(RewardPerScoreRange.x, RewardPerScoreRange.y),
                    rewardForHit = Random.Range(RewardForHitRange.x, RewardForHitRange.y),
                    rewardForShockwave = Random.Range(RewardForShockwaveRange.x, RewardForShockwaveRange.y),
                    penaltyOnHit = Random.Range(PenaltyOnHitRange.x, PenaltyOnHitRange.y),
                    livingPenalty = Random.Range(LivingPenaltyRange.x, LivingPenaltyRange.y),
                    terminalWinReward = Random.Range(TerminalWinRewardRange.x, TerminalWinRewardRange.y),
                    terminalLossPenalty = Random.Range(TerminalLossPenaltyRange.x, TerminalLossPenaltyRange.y),
                    fileName = $"qtable_g0_c{i}.json"
                };
                _population.Add(c);
            }
            _current = _population[0];
            Debug.Log($"[EVOL] Initial population Gen=0 Size={_population.Count}");
        }

        private void BeginOrResumeCurrentCandidate()
        {
            if (!EvolutionaryEnabled || _population == null || _population.Count == 0) return;
            _currentIdx = Mathf.Clamp(_currentIdx, 0, _population.Count - 1);
            _current = _population[_currentIdx];
            if (controller == null) return;

            controller.SaveFileName = _current.fileName;
            controller.TrainingMode = true;

            // ========== FIX CRITIQUE ==========
            // NE JAMAIS reset la Q-table entre les épisodes du même candidat !
            // L'agent DOIT garder sa mémoire pour apprendre
            bool reset = false;

            // Seulement au tout premier épisode du candidat, charger ou créer la Q-table
            if (_current.episodesRun == 0)
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, _current.fileName);
                if (System.IO.File.Exists(path))
                {
                    // Charger la Q-table existante (si on reprend l'entraînement)
                    Debug.Log($"[EVOL] Loading existing Q-table for G{_currentGen} C{_current.index}");
                    controller.LoadAgent();
                    reset = false; // Ne pas reset, continuer l'apprentissage
                }
                else
                {
                    // Créer une nouvelle Q-table vide
                    Debug.Log($"[EVOL] Creating new Q-table for G{_currentGen} C{_current.index}");
                    reset = true; // Seulement si le fichier n'existe pas
                }
            }
            else
            {
                // Episodes 1-199 : continuer avec la même Q-table, NE PAS RESET !
                Debug.Log($"[EVOL] Continuing learning for G{_currentGen} C{_current.index} (Episode {_current.episodesRun})");
                reset = false;
            }
            // ==================================

            controller.ApplyHyperParamsAndReset(_current.alpha, _current.gamma, _current.epsStart, _current.epsDecay, _current.minEps, reset);

            if (EvolveRewards)
            {
                controller.RewardPerWaypoint = _current.rewardPerWaypoint;
                controller.RewardPerScore = _current.rewardPerScore;
                controller.RewardForHit = _current.rewardForHit;
                controller.RewardForShockwave = _current.rewardForShockwave;
                controller.PenaltyOnHit = _current.penaltyOnHit;
                controller.LivingPenalty = _current.livingPenalty;
                controller.TerminalWinReward = _current.terminalWinReward;
                controller.TerminalLossPenalty = _current.terminalLossPenalty;
            }

            Debug.Log($"[EVOL] G{_currentGen} C{_current.index}: α={_current.alpha:F3} γ={_current.gamma:F4} ε={_current.epsStart:F3} RwdHit={_current.rewardForHit:F2} TermWin={_current.terminalWinReward:F2} Episodes={_current.episodesRun}/200 Reset={reset}");
        }

        private void HandleEvolutionProgression()
        {
            if (_current == null) return;
            if (_bestEver == null || IsBetter(_current, _bestEver))
            {
                _bestEver = _current;
                Debug.Log($"[EVOL] New BEST -> Gen {_current.generation} Cand {_current.index} WR={_current.WinRate:P1} Avg={_current.AvgScore:F2}");
            }

            if (_current.episodesRun >= EpisodesPerCandidate)
            {
                _currentIdx++;
                if (_currentIdx >= _population.Count)
                {
                    if (MaxGenerations > 0 && _currentGen + 1 >= MaxGenerations)
                    {
                        Debug.Log("[EVOL] MaxGenerations atteint.");
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
            prev.Sort((a, b) =>
            {
                int c = b.WinRate.CompareTo(a.WinRate);
                if (c != 0) return c;
                c = b.AvgScore.CompareTo(a.AvgScore);
                if (c != 0) return c;
                return b.AvgWaypoints.CompareTo(a.AvgWaypoints);
            });

            List<Candidate> next = new List<Candidate>();
            int elites = Mathf.Clamp(ElitesToKeep, 0, Mathf.Min(prev.Count, PopulationSize));
            for (int i = 0; i < elites; i++)
            {
                Candidate e = prev[i].Clone(newGen, next.Count);
                e.episodesRun = 0; e.wins = 0; e.games = 0; e.cumulativeScore = 0; e.cumulativeWaypoints = 0;
                next.Add(e);
            }

            int parentPool = Mathf.Max(1, prev.Count / 2);
            System.Random rnd = new System.Random();
            while (next.Count < PopulationSize)
            {
                Candidate p = prev[rnd.Next(parentPool)];
                Candidate child = p.Clone(newGen, next.Count);
                Mutate(child);
                child.episodesRun = 0; child.wins = 0; child.games = 0; child.cumulativeScore = 0; child.cumulativeWaypoints = 0;
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
            c.rewardPerWaypoint = Jitter(c.rewardPerWaypoint, RewardPerWaypointRange, MutationSigma);
            c.rewardPerScore = Jitter(c.rewardPerScore, RewardPerScoreRange, MutationSigma);
            c.rewardForHit = Jitter(c.rewardForHit, RewardForHitRange, MutationSigma);
            c.rewardForShockwave = Jitter(c.rewardForShockwave, RewardForShockwaveRange, MutationSigma);
            c.penaltyOnHit = Jitter(c.penaltyOnHit, PenaltyOnHitRange, MutationSigma);
            c.livingPenalty = Jitter(c.livingPenalty, LivingPenaltyRange, MutationSigma);
            c.terminalWinReward = Jitter(c.terminalWinReward, TerminalWinRewardRange, MutationSigma);
            c.terminalLossPenalty = Jitter(c.terminalLossPenalty, TerminalLossPenaltyRange, MutationSigma);
        }

        private bool IsBetter(Candidate a, Candidate b)
        {
            if (b == null) return true;
            if (!Mathf.Approximately(a.WinRate, b.WinRate)) return a.WinRate > b.WinRate;
            return a.AvgScore > b.AvgScore;
        }
    }
}

