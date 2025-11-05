using DoNotModify;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;


namespace AI
{
    // Simple trainer pour faciliter l'entraînement en Play Mode.
    // Attacher à un GameObject dans la scène et assigner le contrôleur QLearning dans l'inspector (ou laisser vide pour auto-find).
    public class QLearningTrainer : MonoBehaviour
    {
        public QLearningController controller; // assignable
        public bool AutoLoad = false;
        public bool AutoSaveOnEnd = true;
        public string SaveFileName = "qtable.json";
        public bool AutoRestartOnEnd = true; // relance la scène pour épisodes consécutifs
        [Tooltip("Mettre >1 pour accélérer le training (Time.timeScale)")]
        public float TrainingTimeScale = 1.0f;

        [Header("Episode logging & autosave")]
        [Tooltip("Sauvegarder automatiquement tous les N épisodes (0 = désactivé)")]
        public int AutoSaveEveryNEpisodes = 0;
        [Tooltip("Fenêtre (nombre d'épisodes) pour la moyenne mobile, 0 = tous les épisodes")]
        public int MovingAverageWindow = 20;
        [Tooltip("Si vrai, le trainer persiste entre les reloads de la scène pour accumuler les stats.")]
        public bool PersistAcrossEpisodes = true;

        [Header("TimeScale on reload")]
        [Tooltip("Si true, conserve TrainingTimeScale après un reload de scène. Sinon réinitialise à ResetTimeScaleOnReload.")]
        public bool PreserveTimeScaleAcrossReloads = false;
        [Tooltip("Valeur à appliquer à Time.timeScale si PreserveTimeScaleAcrossReloads == false (par défaut 1).")]
        public float ResetTimeScaleOnReload = 1.0f;

        [Header("Evolutionary Q-Learning")]
        [Tooltip("Active l'entraînement évolutif (sélection + mutation) des hyperparamètres Q-Learning.")]
        public bool EvolutionaryEnabled = true;
        [Tooltip("Taille de la population par génération.")]
        public int PopulationSize = 6;
        [Tooltip("Nombre d'épisodes évalués par candidat (le Q-table s'accumule sur ces épisodes).")]
        public int EpisodesPerCandidate = 3;
        [Tooltip("Nombre de générations à dérouler (0 = infini jusqu'à arrêt manuel).")]
        public int MaxGenerations = 0;
        [Tooltip("Nombre d'élites conservées inchangées d'une génération à la suivante.")]
        public int ElitesToKeep = 1;
        [Tooltip("Taux de mutation (écart type relatif) appliqué aux hyperparamètres")]
        [Range(0.0f, 1.0f)] public float MutationSigma = 0.15f;
        [Tooltip("Borne min/max pour alpha, gamma, epsilonStart, epsilonDecay, minEpsilon")]
        public Vector2 AlphaRange = new Vector2(0.05f, 1.0f);
        public Vector2 GammaRange = new Vector2(0.5f, 0.9999f);
        public Vector2 EpsilonStartRange = new Vector2(0.01f, 1.0f);
        public Vector2 EpsilonDecayRange = new Vector2(0.9f, 0.99999f);
        public Vector2 MinEpsilonRange = new Vector2(0.0f, 0.2f);

        // internal stats
        private int _episodeCount = 0;
        private List<int> _scoresHistory = new List<int>();
        private List<int> _waypointsHistory = new List<int>();
        private static QLearningTrainer _instance = null;

        private GameManager _gm;
        private float _prevTimeScale = 1.0f;
        private bool _handledGameOver = false;

        // Evolutionary state
        private class Candidate
        {
            public int generation;
            public int index;
            public float alpha, gamma, epsStart, epsDecay, minEps;
            public int episodesRun = 0;
            public int wins = 0;
            public int games = 0;
            public float cumulativeScore = 0f;
            public float bestScore = float.MinValue;
            public string fileName; // dedicated qtable file

            public float WinRate => games > 0 ? (float)wins / games : 0f;
            public float AvgScore => games > 0 ? cumulativeScore / games : 0f;

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

        private List<Candidate> _population = new List<Candidate>();
        private int _currentGen = 0;
        private int _currentIdx = 0;
        private Candidate _current;
        private Candidate _bestEver;

        void Awake()
        {
            // singleton to avoid duplicates when persisting across scene loads
            if (PersistAcrossEpisodes)
            {
                if (_instance != null && _instance != this)
                {
                    Destroy(this.gameObject);
                    return;
                }
                _instance = this;
                DontDestroyOnLoad(this.gameObject);
            }
        }

        void Start()
        {
            _gm = GameManager.Instance;
            if (controller == null)
            {
                // find any in scene
                controller = Object.FindAnyObjectByType<QLearningController>();
            }

            if (controller != null)
            {
                controller.SaveFileName = SaveFileName;
                if (AutoLoad)
                {
                    controller.LoadAgent();
                    Debug.Log("QLearningTrainer: loaded agent on start");
                }
            }

            // If a desired TimeScale was saved before a reload (by previous trainer instance), apply it now
            if (PlayerPrefs.HasKey("QL_DesiredTimeScale"))
            {
                float desired = PlayerPrefs.GetFloat("QL_DesiredTimeScale", TrainingTimeScale);
                int flag = PlayerPrefs.GetInt("QL_PreserveTimeScaleFlag", PreserveTimeScaleAcrossReloads ? 1 : 0);
                TrainingTimeScale = desired;
                PreserveTimeScaleAcrossReloads = (flag == 1);
                // consume keys so they don't persist indefinitely
                PlayerPrefs.DeleteKey("QL_DesiredTimeScale");
                PlayerPrefs.DeleteKey("QL_PreserveTimeScaleFlag");
                PlayerPrefs.Save();
                Debug.Log($"QLearningTrainer: applied desired TimeScale={TrainingTimeScale} from PlayerPrefs (preserve={PreserveTimeScaleAcrossReloads})");
            }

            _prevTimeScale = Time.timeScale;
            if (TrainingTimeScale > 0)
                StartCoroutine(ApplyDesiredTimeScaleCoroutine(TrainingTimeScale, PreserveTimeScaleAcrossReloads));

            // subscribe to sceneLoaded so we can rebind controller after reloads
            if (PersistAcrossEpisodes)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
            }

            // Initialize evolutionary process
            if (EvolutionaryEnabled)
            {
                if (_population.Count == 0)
                {
                    BuildInitialPopulation();
                }
                BeginOrResumeCurrentCandidate();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // try to find a controller instance in the new scene
            if (controller == null)
            {
                controller = Object.FindAnyObjectByType<QLearningController>();
                if (controller != null)
                {
                    controller.SaveFileName = SaveFileName;
                    Debug.Log("QLearningTrainer: re-bound controller after scene load");
                }
            }
            // refresh GameManager reference
            _gm = GameManager.Instance;

            // Apply time scale after a short delay to avoid execution-order race conditions
            float desired = PreserveTimeScaleAcrossReloads ? TrainingTimeScale : ResetTimeScaleOnReload;
            StartCoroutine(ApplyDesiredTimeScaleCoroutine(desired, PreserveTimeScaleAcrossReloads));

            // reset handled flag when a new scene is loaded
            _handledGameOver = false;

            // Re-apply current evolutionary candidate after reload
            if (EvolutionaryEnabled)
            {
                BeginOrResumeCurrentCandidate();
            }
        }

        private System.Collections.IEnumerator ApplyDesiredTimeScaleCoroutine(float desired, bool preserve)
        {
            // wait one frame to ensure other Awake/Start code (GameManager) finished
            yield return null;
            Time.timeScale = desired > 0 ? desired : 1.0f;
            if (preserve)
                Debug.Log($"QLearningTrainer: applied desired TrainingTimeScale = {desired} after delay");
            else
                Debug.Log($"QLearningTrainer: reset Time.timeScale to {desired} after delay");
        }

        void OnDestroy()
        {
            if (PersistAcrossEpisodes)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (_instance == this) _instance = null;
            }
            Time.timeScale = _prevTimeScale;
        }

        void Update()
        {
            if (controller == null) return;

            // shortcuts:
            // K -> save agent table
            // L -> load agent table
            // P -> toggle training mode
            // R -> restart scene
            if (Input.GetKeyDown(KeyCode.K))
            {
                controller.SaveAgent();
                Debug.Log("QLearningTrainer: saved agent (K)");
            }
            if (Input.GetKeyDown(KeyCode.L))
            {
                controller.LoadAgent();
                Debug.Log("QLearningTrainer: loaded agent (L)");
            }
            if (Input.GetKeyDown(KeyCode.P))
            {
                controller.TrainingMode = !controller.TrainingMode;
                Debug.Log("QLearningTrainer: TrainingMode = " + controller.TrainingMode);
            }
            if (Input.GetKeyDown(KeyCode.R))
            {
                SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            }

            if (_gm != null)
            {
                bool gameFinished = _gm.IsGameFinished();
                if (gameFinished)
                {
                    // Only handle once per game-over transition
                    if (_handledGameOver) return;

                    // Only treat game-over as an episode end when training is enabled.
                    if (controller == null || !controller.TrainingMode)
                    {
                        // restore timeScale in case the trainer previously changed it
                        Time.timeScale = _prevTimeScale;
                        _handledGameOver = true;
                        return;
                    }

                    // mark handled to avoid repeated increments
                    _handledGameOver = true;

                    // log episode stats for the player controlled by the trainer's controller
                    int owner = -1;
                    var ship = _gm.GetSpaceShipForController(controller);
                    if (ship != null) owner = ship.Owner;
                    else owner = 0; // fallback

                    int score = _gm.GetScoreForPlayer(owner);
                    int waypoints = _gm.GetWayPointScoreForPlayer(owner);
                    _episodeCount++;
                    _scoresHistory.Add(score);
                    _waypointsHistory.Add(waypoints);

                    // compute moving averages
                    int countToUse = MovingAverageWindow > 0 ? Mathf.Min(MovingAverageWindow, _scoresHistory.Count) : _scoresHistory.Count;
                    float avgScore = 0f; float avgWay = 0f;
                    for (int i = _scoresHistory.Count - countToUse; i < _scoresHistory.Count; i++)
                    {
                        if (i >= 0)
                        {
                            avgScore += _scoresHistory[i];
                            avgWay += _waypointsHistory[i];
                        }
                    }
                    if (countToUse > 0) { avgScore /= countToUse; avgWay /= countToUse; }

                    Debug.Log($"Episode {_episodeCount}: score={score}, waypoints={waypoints}, avgScore({countToUse})={avgScore:F2}, avgWaypoints({countToUse})={avgWay:F2}");

                    // evolutionary episode accounting
                    if (EvolutionaryEnabled && _current != null)
                    {
                        int opponentId = owner == 0 ? 1 : 0;
                        int oppScore = _gm.GetScoreForPlayer(opponentId);
                        bool win = score > oppScore;
                        _current.games++;
                        if (win) _current.wins++;
                        _current.cumulativeScore += score;
                        if (score > _current.bestScore) _current.bestScore = score;

                        Debug.Log($"[EVOL] Gen {_current.generation} Cand {_current.index} Episode {_current.episodesRun + 1}/{EpisodesPerCandidate} -> Score={score} vs Opp={oppScore} => {(win ? "WIN" : "LOSS")} | WR={_current.WinRate:P1} Avg={_current.AvgScore:F2}");
                        _current.episodesRun++;
                    }

                    // autosave every N episodes
                    if (AutoSaveEveryNEpisodes > 0 && (_episodeCount % AutoSaveEveryNEpisodes) == 0)
                    {
                        controller.SaveAgent();
                        Debug.Log($"QLearningTrainer: Auto-saved agent at episode {_episodeCount}");
                    }

                    if (AutoSaveOnEnd)
                    {
                        controller.SaveAgent();
                        Debug.Log("QLearningTrainer: saved agent at end of episode");
                    }

                    // Evolutionary flow control
                    if (EvolutionaryEnabled)
                    {
                        HandleEvolutionProgression();
                    }

                    if (AutoRestartOnEnd)
                    {
                        // persist desired Time.timeScale across reload via PlayerPrefs so new scene/trainer can reapply it
                        PlayerPrefs.SetFloat("QL_DesiredTimeScale", TrainingTimeScale);
                        PlayerPrefs.SetInt("QL_PreserveTimeScaleFlag", PreserveTimeScaleAcrossReloads ? 1 : 0);
                        PlayerPrefs.Save();

                        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
                    }
                    else
                    {
                        // restore timeScale
                        Time.timeScale = _prevTimeScale;
                    }
                }
                else
                {
                    // game running -> reset handled flag so next game-over will be processed
                    _handledGameOver = false;
                }
            }
        }

        // ---- Evolutionary helpers ----
        private void BuildInitialPopulation()
        {
            _population.Clear();
            _currentGen = 0;
            for (int i = 0; i < Mathf.Max(1, PopulationSize); i++)
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
            _currentIdx = 0;
            _current = _population[_currentIdx];
            _bestEver = null;
            Debug.Log($"[EVOL] Initial population created (Gen {_currentGen}, Size={_population.Count})");
        }

        private void BeginOrResumeCurrentCandidate()
        {
            if (!EvolutionaryEnabled) return;
            if (_population == null || _population.Count == 0)
            {
                BuildInitialPopulation();
            }
            if (_current == null)
            {
                _currentIdx = Mathf.Clamp(_currentIdx, 0, _population.Count - 1);
                _current = _population[_currentIdx];
            }

            // apply hyperparams and set candidate-specific save file
            if (controller != null && _current != null)
            {
                controller.SaveFileName = _current.fileName;
                controller.TrainingMode = true;
                bool resetTable = _current.episodesRun == 0; // fresh candidate => reset
                controller.ApplyHyperParamsAndReset(_current.alpha, _current.gamma, _current.epsStart, _current.epsDecay, _current.minEps, resetTable);
                if (!resetTable)
                {
                    controller.LoadAgent(); // continue from previous episodes' Q-table
                }
                Debug.Log($"[EVOL] Starting Gen {_current.generation} Cand {_current.index} (epsiode {_current.episodesRun + 1}/{EpisodesPerCandidate}) α={_current.alpha:F3} γ={_current.gamma:F4} ε0={_current.epsStart:F2} decay={_current.epsDecay:F5} εmin={_current.minEps:F2} -> file={_current.fileName}");
            }
        }

        private void HandleEvolutionProgression()
        {
            if (_current == null) return;

            // update global best if improved by winrate then avg score
            if (_bestEver == null || IsBetter(_current, _bestEver))
            {
                _bestEver = _current;
                // Copy current candidate's table to public SaveFileName for convenience
                TryCopyCandidateToPublic(_current.fileName, SaveFileName);
                Debug.Log($"[EVOL] New BEST so far -> Gen {_current.generation} Cand {_current.index} WR={_current.WinRate:P1} Avg={_current.AvgScore:F2} (α={_current.alpha:F3}, γ={_current.gamma:F4}, ε0={_current.epsStart:F2}, decay={_current.epsDecay:F5}, εmin={_current.minEps:F2})");
            }

            // if candidate finished its episodes -> move to next
            if (_current.episodesRun >= Mathf.Max(1, EpisodesPerCandidate))
            {
                Debug.Log($"[EVOL] Candidate over -> Gen {_current.generation} Cand {_current.index} Summary: WR={_current.WinRate:P1} Avg={_current.AvgScore:F2} BestScore={_current.bestScore:F1}");
                _currentIdx++;
                if (_currentIdx >= _population.Count)
                {
                    // Generation end -> spawn next generation
                    if (MaxGenerations > 0 && _currentGen + 1 >= MaxGenerations)
                    {
                        Debug.Log("[EVOL] Reached MaxGenerations. Stopping evolution loop.");
                        EvolutionaryEnabled = false;
                        return;
                    }

                    List<Candidate> next = BreedNextGeneration(_population, _currentGen + 1);
                    _population = next;
                    _currentGen++;
                    _currentIdx = 0;
                    Debug.Log($"[EVOL] New Generation {_currentGen} created. Size={_population.Count}");
                }
                _current = _population[_currentIdx];
            }
        }

        private List<Candidate> BreedNextGeneration(List<Candidate> prev, int newGen)
        {
            // rank by winrate then avg score
            prev.Sort((a, b) =>
            {
                int cmp = b.WinRate.CompareTo(a.WinRate);
                if (cmp != 0) return cmp;
                return b.AvgScore.CompareTo(a.AvgScore);
            });

            List<Candidate> next = new List<Candidate>();
            int elites = Mathf.Clamp(ElitesToKeep, 0, Mathf.Min(prev.Count, PopulationSize));
            for (int i = 0; i < elites; i++)
            {
                Candidate elite = prev[i].Clone(newGen, next.Count);
                // reset stats for new generation; keep table file name new
                elite.episodesRun = 0; elite.wins = 0; elite.games = 0; elite.cumulativeScore = 0; elite.bestScore = float.MinValue;
                next.Add(elite);
                Debug.Log($"[EVOL] Elite carried -> from Gen {prev[i].generation} Cand {prev[i].index} WR={prev[i].WinRate:P1} Avg={prev[i].AvgScore:F2}");
            }

            // parent pool (top half)
            int parentPool = Mathf.Max(1, prev.Count / 2);
            System.Random rnd = new System.Random();
            while (next.Count < Mathf.Max(1, PopulationSize))
            {
                Candidate p = prev[rnd.Next(parentPool)];
                Candidate child = p.Clone(newGen, next.Count);
                Mutate(child);
                child.episodesRun = 0; child.wins = 0; child.games = 0; child.cumulativeScore = 0; child.bestScore = float.MinValue;
                next.Add(child);
            }

            return next;
        }

        private void Mutate(Candidate c)
        {
            // gaussian-like noise via Box-Muller on [-sigma, +sigma] approx using UnityEngine.Random
            float Jitter(float value, Vector2 range, float sigma)
            {
                // sample from normal(0,1) approx
                float u1 = Mathf.Clamp01(Random.value);
                float u2 = Mathf.Clamp01(Random.value);
                float z = Mathf.Sqrt(-2.0f * Mathf.Log(Mathf.Max(1e-6f, u1))) * Mathf.Cos(2.0f * Mathf.PI * u2);
                float nv = value * (1.0f + z * sigma);
                return Mathf.Clamp(nv, range.x, range.y);
            }

            c.alpha = Jitter(c.alpha, AlphaRange, MutationSigma);
            c.gamma = Jitter(c.gamma, GammaRange, MutationSigma);
            c.epsStart = Jitter(c.epsStart, EpsilonStartRange, MutationSigma);
            c.epsDecay = Jitter(c.epsDecay, EpsilonDecayRange, MutationSigma);
            c.minEps = Jitter(c.minEps, MinEpsilonRange, MutationSigma);
        }

        private bool IsBetter(Candidate a, Candidate b)
        {
            if (a == null) return false;
            if (b == null) return true;
            if (Mathf.Approximately(a.WinRate, b.WinRate))
                return a.AvgScore > b.AvgScore;
            return a.WinRate > b.WinRate;
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
            catch (System.SystemException e)
            {
                Debug.LogWarning($"[EVOL] Failed to copy best candidate file: {e.Message}");
            }
        }

        private float RandomInRange(Vector2 r)
        {
            return Random.Range(r.x, r.y);
        }

    }
}
