using UnityEngine;
using DoNotModify;

namespace AI
{
    // Contrôleur modulaire utilisant QLearningAgent
    public class QLearningController : BaseSpaceShipController
    {
        [Header("Agent parameters")]
        [SerializeField] private float _alpha = 0.5f;
        [SerializeField] private float _gamma = 0.99f;
        [SerializeField] private float _epsilon = 0.2f;
        [SerializeField] private float _epsilonDecay = 0.9995f;
        [SerializeField] private float _minEpsilon = 0.01f;

        [Header("Rewards & penalties")]
        [Tooltip("Reward added when a waypoint is captured (per waypoint).")]
        [SerializeField] private float rewardPerWaypoint = 1.0f;
        [Tooltip("Reward added when score increases (per score point).")]
        [SerializeField] private float rewardPerScore = 0.5f;
        [Tooltip("Small living penalty applied every step to encourage faster behaviour (can be negative).")]
        [SerializeField] private float livingPenalty = -0.01f;
        [Tooltip("Exploration bonus given when a shot was fired in the previous step.")]
        [SerializeField] private float rewardForShot = 0.2f;
        [Tooltip("Exploration bonus given when a mine was dropped in the previous step.")]
        [SerializeField] private float rewardForDropMine = 0.25f;
        [Tooltip("Exploration bonus given when a shockwave was fired in the previous step.")]
        [SerializeField] private float rewardForShockwave = 0.35f;
        [Tooltip("Penalty applied when the ship receives a hit (per hit). Use negative value for punishment).")]
        [SerializeField] private float penaltyOnHit = -0.5f;

        [Header("Discretization / behaviour")]
        [SerializeField] private float _nearFactor = 1.5f;
        [SerializeField] private float _midFactor = 4.0f;
        [SerializeField] private float _avoidAheadDistance = 4.0f;
        [SerializeField] private float _avoidConeAngle = 60.0f;

        [Header("Action mapping")]
        [SerializeField] private float[] thrustLevels = new float[3] { 0f, 0.5f, 1f };
        [SerializeField] private float[] steerAngles = new float[3] { -30f, 0f, 30f };

        [Header("Runtime")]
        public bool TrainingMode = true;
        public string SaveFileName = "qtable.json";
        [SerializeField] private bool _loadOnStart = false;

        // Exposed properties pour modularité
        public float Alpha { get => _alpha; set { _alpha = value; if (agent != null) agent.Alpha = value; } }
        public float Gamma { get => _gamma; set { _gamma = value; if (agent != null) agent.Gamma = value; } }
        public float Epsilon { get => _epsilon; set { _epsilon = value; if (agent != null) agent.Epsilon = value; } }
        public float EpsilonDecay { get => _epsilonDecay; set { _epsilonDecay = value; if (agent != null) agent.EpsilonDecay = value; } }
        public float MinEpsilon { get => _minEpsilon; set { _minEpsilon = value; if (agent != null) agent.MinEpsilon = value; } }
        public bool LoadOnStart { get => _loadOnStart; set { _loadOnStart = value; } }

        private QLearningAgent agent;

        [Header("Debug")]
        [SerializeField] private bool DebugActions = false;
        private int _weaponActionCount = 0;
        private int _totalActionCount = 0;
        private float _logInterval = 5.0f;
        private float _logTimer = 0f;

        // previous step for learning
        private string prevState = null;
        private int prevAction = -1;
        private int prevWaypoints = 0;
        private int prevScore = 0;
        private int prevHitCount = 0;

        // helper
        private SpaceShipView _shipView;
        private GameData _data;

        public override void Initialize(SpaceShipView spaceship, GameData data)
        {
            _shipView = spaceship;
            _data = data;

            agent = new QLearningAgent();
            // expand action space: 3 thrust levels × 3 steer options × 4 weapon choices (none, shoot, dropMine, fireShockwave)
            int actionCount = 3 * 3 * 4; // 36 actions
            agent.Initialize(actionCount, _alpha, _gamma, _epsilon);
            agent.EpsilonDecay = _epsilonDecay;
            agent.MinEpsilon = _minEpsilon;
            if (_loadOnStart)
            {
                agent.Load(SaveFileName);
            }

            prevState = null;
            prevAction = -1;
            prevWaypoints = spaceship.WaypointScore;
            prevScore = spaceship.Score;
            prevHitCount = spaceship.HitCount;
        }

        // simple threat detection (adaptée de ExampleController)
        private AsteroidView DetectThreat(SpaceShipView ship, GameData data)
        {
            if (data == null || data.Asteroids == null) return null;
            AsteroidView nearest = null; float best = float.MaxValue;
            foreach (AsteroidView a in data.Asteroids)
            {
                if (a == null) continue;
                Vector2 to = a.Position - ship.Position; float d = to.magnitude;
                if (d > _avoidAheadDistance) continue;
                float ang = Mathf.Abs(Vector2.SignedAngle(ship.LookAt, to));
                if (ang > _avoidConeAngle * 0.5f) continue;
                if (d < best) { best = d; nearest = a; }
            }
            return nearest;
        }

        // find nearest waypoint not owned
        private WayPointView FindNextTarget(SpaceShipView ship, GameData data)
        {
            if (data == null || data.WayPoints == null || data.WayPoints.Count == 0) return null;
            WayPointView best = null; float bd = float.MaxValue;
            foreach (WayPointView w in data.WayPoints)
            {
                if (w.Owner == ship.Owner) continue;
                float d = (w.Position - ship.Position).sqrMagnitude;
                if (d < bd) { bd = d; best = w; }
            }
            return best;
        }

        // discretize the state to a string key: distBucket_angleBucket_threat
        private string EncodeState(SpaceShipView ship, GameData data)
        {
            WayPointView target = FindNextTarget(ship, data);
            string distBucket = "noTarget";
            string angleBucket = "na";
            string threat = "none";

            if (target != null)
            {
                float d = (target.Position - ship.Position).magnitude;
                if (d <= target.Radius * _nearFactor) distBucket = "near";
                else if (d <= target.Radius * _midFactor) distBucket = "mid";
                else distBucket = "far";

                float desired = Mathf.Atan2((target.Position - ship.Position).y, (target.Position - ship.Position).x) * Mathf.Rad2Deg;
                float angDiff = Mathf.DeltaAngle(ship.Orientation, desired);
                if (Mathf.Abs(angDiff) <= 20f) angleBucket = "front";
                else if (angDiff > 0) angleBucket = "left"; else angleBucket = "right";
            }

            AsteroidView a = DetectThreat(ship, data);
            if (a != null) threat = "threat";

            // --- ENEMY: nearest opposing spaceship ---
            string enemyDist = "none";
            string enemyAngle = "na";
            string enemyHasShot = "no";
            if (data != null && data.SpaceShips != null && data.SpaceShips.Count > 0)
            {
                SpaceShipView nearestEnemy = null; float best = float.MaxValue;
                foreach (SpaceShipView sv in data.SpaceShips)
                {
                    if (sv == null) continue;
                    if (sv.Owner == ship.Owner) continue; // skip self / team
                    float d = (sv.Position - ship.Position).magnitude;
                    if (d < best) { best = d; nearestEnemy = sv; }
                }
                if (nearestEnemy != null)
                {
                    float r = Mathf.Max(0.001f, ship.Radius);
                    if (best <= r * 3f) enemyDist = "near";
                    else if (best <= r * 6f) enemyDist = "mid";
                    else enemyDist = "far";

                    float desiredE = Mathf.Atan2((nearestEnemy.Position - ship.Position).y, (nearestEnemy.Position - ship.Position).x) * Mathf.Rad2Deg;
                    float angDiffE = Mathf.DeltaAngle(ship.Orientation, desiredE);
                    if (Mathf.Abs(angDiffE) <= 20f) enemyAngle = "front";
                    else if (angDiffE > 0) enemyAngle = "left"; else enemyAngle = "right";

                    enemyHasShot = nearestEnemy.HasShot ? "yes" : "no";
                }
            }

            // --- MINE: nearest mine ---
            string mineDist = "none";
            string mineActive = "na";
            if (data != null && data.Mines != null && data.Mines.Count > 0)
            {
                MineView nearestMine = null; float bestM = float.MaxValue;
                foreach (MineView mv in data.Mines)
                {
                    if (mv == null) continue;
                    float d = (mv.Position - ship.Position).magnitude;
                    if (d < bestM) { bestM = d; nearestMine = mv; }
                }
                if (nearestMine != null)
                {
                    float r = Mathf.Max(0.001f, ship.Radius);
                    if (bestM <= r * 3f) mineDist = "near";
                    else if (bestM <= r * 6f) mineDist = "mid";
                    else mineDist = "far";

                    mineActive = nearestMine.IsActive ? "active" : "inactive";
                }
            }

            // assemble full key: waypoint|angle|threat|enemyDist|enemyAngle|enemyShot|mineDist|mineActive
            return string.Join("|", distBucket, angleBucket, threat, enemyDist, enemyAngle, enemyHasShot, mineDist, mineActive);
        }

        // Map action index (0..35) to InputData (thrust, orientation, weapon)
        // encoding: index = thrustIndex * (3*4) + steerIndex * 4 + weaponIndex
        // thrustIndex in [0..2], steerIndex in [0..2], weaponIndex in [0..3]
        // weaponIndex: 0 = none, 1 = shoot, 2 = dropMine, 3 = fireShockwave
        private InputData ActionToInput(int action, SpaceShipView ship, GameData data)
        {
            int ai = Mathf.Clamp(action, 0, Mathf.Max(0, 3 * 3 * 4 - 1));
            int ti = ai / (3 * 4); // thrust index (0..2)
            int rem = ai % (3 * 4);
            int si = rem / 4; // steer index (0..2)
            int wi = rem % 4; // weapon index (0..3)
            float thrust = thrustLevels[Mathf.Clamp(ti, 0, thrustLevels.Length - 1)];

            // compute base orientation towards target or keep current
            WayPointView target = FindNextTarget(ship, data);
            float baseOrient = ship.Orientation;
            if (target != null)
            {
                baseOrient = Mathf.Atan2((target.Position - ship.Position).y, (target.Position - ship.Position).x) * Mathf.Rad2Deg;
            }

            float desiredOrient = baseOrient + steerAngles[Mathf.Clamp(si, 0, steerAngles.Length - 1)];

            // if threat present, override to avoid
            AsteroidView ast = DetectThreat(ship, data);
            if (ast != null)
            {
                Vector2 toAst = ast.Position - ship.Position;
                float side = Mathf.Sign(Vector2.SignedAngle(ship.LookAt, toAst));
                Vector2 avoidDir = Quaternion.Euler(0, 0, -side * 90f) * ship.LookAt;
                desiredOrient = Mathf.Atan2(avoidDir.y, avoidDir.x) * Mathf.Rad2Deg;
                thrust = Mathf.Min(thrust, 0.5f); // slow down a bit when avoiding
            }

            // if action includes a weapon, reduce thrust to conserve energy so the ship can actually fire
            if (wi != 0)
            {
                thrust = Mathf.Min(thrust, 0.1f);
            }

            // ensure the ship actually has energy for the selected weapon; if not, fallback to none
            float energy = ship.Energy;
            bool canShoot = energy >= ship.ShootEnergyCost;
            bool canDrop = energy >= ship.MineEnergyCost;
            bool canShock = energy >= ship.ShockwaveEnergyCost;
            if (wi == 1 && !canShoot) { if (DebugActions) Debug.Log($"QLearner attempted SHOOT but energy={energy:F2} < cost={ship.ShootEnergyCost:F2}"); wi = 0; }
            if (wi == 2 && !canDrop) { if (DebugActions) Debug.Log($"QLearner attempted DROP MINE but energy={energy:F2} < cost={ship.MineEnergyCost:F2}"); wi = 0; }
            if (wi == 3 && !canShock) { if (DebugActions) Debug.Log($"QLearner attempted SHOCKWAVE but energy={energy:F2} < cost={ship.ShockwaveEnergyCost:F2}"); wi = 0; }

            // weapon flags
            bool shoot = false, dropMine = false, fireShockwave = false;
            switch (wi)
            {
                case 1: shoot = true; break;
                case 2: dropMine = true; break;
                case 3: fireShockwave = true; break;
                default: break; // 0 = none
            }

            return new InputData(thrust, desiredOrient, shoot, dropMine, fireShockwave);
        }

        public override InputData UpdateInput(SpaceShipView ship, GameData data)
        {
            if (data == null) return new InputData(0f, ship.Orientation, false, false, false);

            string state = EncodeState(ship, data);
            int action = agent.ChooseAction(state, greedy: !TrainingMode);

            // perform learning from previous step
            if (TrainingMode && prevState != null && prevAction >= 0)
            {
                // reward: change in waypoint count + change in score
                int wayDiff = ship.WaypointScore - prevWaypoints;
                int scoreDiff = ship.Score - prevScore;
                int hitDiff = ship.HitCount - prevHitCount;

                float reward = wayDiff * rewardPerWaypoint + scoreDiff * rewardPerScore;

                // small living penalty to encourage speed
                reward += livingPenalty;

                // exploration bonuses if previous action triggered weapon usage
                if (ship.HasShot) reward += rewardForShot;
                if (ship.HasDroppedMine) reward += rewardForDropMine;
                if (ship.HasFiredShockwave) reward += rewardForShockwave;

                // penalty for being hit (can be negative)
                if (hitDiff > 0) reward += hitDiff * penaltyOnHit;

                agent.Learn(prevState, prevAction, reward, state, false);
            }

            InputData input = ActionToInput(action, ship, data);

            // debug counting
            if (DebugActions)
            {
                _totalActionCount++;
                if (input.shoot || input.dropMine || input.fireShockwave) _weaponActionCount++;
                _logTimer += Time.deltaTime;
                if (_logTimer >= _logInterval)
                {
                    Debug.Log($"QLearningController actions: total={_totalActionCount}, weapon={_weaponActionCount}");
                    _logTimer = 0f;
                }
            }

            // update previous trackers
            prevState = state;
            prevAction = action;
            prevWaypoints = ship.WaypointScore;
            prevScore = ship.Score;
            prevHitCount = ship.HitCount;

            return input;
        }

        // API utilities
        public void SaveAgent()
        {
            if (agent == null) return;
            agent.Save(SaveFileName);
        }

        public void LoadAgent()
        {
            if (agent == null) agent = new QLearningAgent();
            agent.Load(SaveFileName);
        }
    }
}
