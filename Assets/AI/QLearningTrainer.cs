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

        // internal stats
        private int _episodeCount = 0;
        private List<int> _scoresHistory = new List<int>();
        private List<int> _waypointsHistory = new List<int>();
        private static QLearningTrainer _instance = null;

        private GameManager _gm;
        private float _prevTimeScale = 1.0f;
        private bool _handledGameOver = false;

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
            // K -> save
            // L -> load
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
    }
}
