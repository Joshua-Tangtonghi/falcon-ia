using UnityEngine;
using DoNotModify;
using System;
using System.Text;

namespace AI
{
    public class QLearningController : BaseSpaceShipController
    {
        [Header("Agent parameters")]
        [SerializeField] private float _alpha = 0.5f;
        [SerializeField] private float _gamma = 0.99f;
        [SerializeField] private float _epsilon = 0.2f;
        [SerializeField] private float _epsilonDecay = 0.9995f;
        [SerializeField] private float _minEpsilon = 0.01f;

        [Header("Rewards & penalties")]
        [SerializeField] private float rewardPerWaypoint = 1.0f;
        [SerializeField] private float rewardPerScore = 1.0f; // augmenter l'incitation au score
        [SerializeField] private float livingPenalty = -0.01f;
        [SerializeField] private float rewardForHit = 2.0f; // augmenter la récompense quand on touche
        [SerializeField] private float penaltyOnHit = -0.5f; // our hit count increases
        [SerializeField] private float rewardForShockwave = 0.05f; // réduire l'incitation à la shockwave

        [Header("Discretization / behaviour")]
        [SerializeField] private float _nearFactor = 1.5f;
        [SerializeField] private float _midFactor = 4.0f;
        [SerializeField] private float _avoidAheadDistance = 4.0f;
        [SerializeField] private float _avoidConeAngle = 60.0f;

        [Header("Action mapping")]
        [SerializeField] private float[] thrustLevels = new float[] { 0f, 0.5f, 1f };
        [SerializeField] private float[] steerAngles = new float[] { -30f, 0f, 30f };

        [Header("Runtime")]
        public bool TrainingMode = true;
        public string SaveFileName = "qtable.json";

        [Header("Energy management")]
        [SerializeField] private bool useEnergyPenalty = true;
        [SerializeField, Range(0f, 1f)] private float energyHighThreshold = 0.8f;
        [SerializeField, Range(0f, 1f)] private float energyLowThreshold = 0.15f;
        [SerializeField] private float penaltyHighEnergy = -0.02f;
        [SerializeField] private float penaltyLowEnergy = -0.05f;

        [Header("Sensors / advanced inputs")]
        [SerializeField] private int fanRays = 3;
        [SerializeField] private float fanAngle = 60f;
        [SerializeField] private float fanRange = 8f;
        [SerializeField] private bool useCircleCast = true;
        [SerializeField] private float circleRadius = 0.3f;

        [Header("Aiming Helpers")]
        [SerializeField] private float steeringOvershoot = 1.2f;
        [SerializeField] private float shootHitTimeTolerance = 0.5f; // tolérance augmentée pour favoriser le tir

        [Header("Terminal rewards")]
        [SerializeField] private float terminalWinReward = 1.0f;
        [SerializeField] private float terminalLossPenalty = -1.5f;

        [Header("Debug")]
        [SerializeField] private bool debugActions = false;
        [Header("Debug avancé")]
        [SerializeField] private bool debugVerbose = false;
        [SerializeField] private float debugVerboseInterval = 1.0f; // secondes
        private float _debugTimer;

        private QLearningAgent _agent;

        // prev trackers
        private string _prevState;
        private int _prevAction = -1;
        private int _prevWaypointScore;
        private int _prevScore;
        private int _prevHitScore;
        private int _prevHitCount;

        private SpaceShipView _shipView;
        private GameData _data;

        private struct FanHit { public bool hit; public float dist; public float normalVelAngle; }

        private struct RewardDetails
        {
            public float total;
            public int wpDiff;
            public int scoreDiff;
            public int hitScDiff;
            public int gotHitDiff;
            public float energyHighPenalty;
            public float energyLowPenalty;
            public float living;
            public float shockwave; // récompense shockwave
        }
        private RewardDetails _lastReward;

        // Exposed properties
        public float Alpha { get => _alpha; set { _alpha = value; if (_agent != null) _agent.Alpha = value; } }
        public float Gamma { get => _gamma; set { _gamma = value; if (_agent != null) _agent.Gamma = value; } }
        public float Epsilon { get => _epsilon; set { _epsilon = value; if (_agent != null) _agent.Epsilon = value; } }
        public float EpsilonDecay { get => _epsilonDecay; set { _epsilonDecay = value; if (_agent != null) _agent.EpsilonDecay = value; } }
        public float MinEpsilon { get => _minEpsilon; set { _minEpsilon = value; if (_agent != null) _agent.MinEpsilon = value; } }

        // Exposed reward properties
        public float RewardPerWaypoint { get => rewardPerWaypoint; set => rewardPerWaypoint = value; }
        public float RewardPerScore { get => rewardPerScore; set => rewardPerScore = value; }
        public float RewardForHit { get => rewardForHit; set => rewardForHit = value; }
        public float RewardForShockwave { get => rewardForShockwave; set => rewardForShockwave = value; }
        public float PenaltyOnHit { get => penaltyOnHit; set => penaltyOnHit = value; }
        public float LivingPenalty { get => livingPenalty; set => livingPenalty = value; }
        public float TerminalWinReward { get => terminalWinReward; set => terminalWinReward = value; }
        public float TerminalLossPenalty { get => terminalLossPenalty; set => terminalLossPenalty = value; }

        public override void Initialize(SpaceShipView spaceship, GameData data)
        {
            _shipView = spaceship;
            _data = data;

            _agent = new QLearningAgent();
            int actionCount = 3 * 3 * 4; // thrust x steer x weapon
            _agent.Initialize(actionCount, _alpha, _gamma, _epsilon);
            _agent.EpsilonDecay = _epsilonDecay;
            _agent.MinEpsilon = _minEpsilon;

            _prevState = null;
            _prevAction = -1;
            _prevWaypointScore = spaceship.WaypointScore;
            _prevScore = spaceship.Score;
            _prevHitScore = spaceship.HitScore;
            _prevHitCount = spaceship.HitCount;
        }

        // --- Sensors ---
        private FanHit[] SampleFan(SpaceShipView ship)
        {
            FanHit[] res = new FanHit[Mathf.Max(1, fanRays)];
            float half = fanAngle * 0.5f;
            for (int i = 0; i < res.Length; i++)
            {
                float t = res.Length == 1 ? 0f : (float)i / (res.Length - 1);
                float ang = Mathf.Lerp(-half, half, t);
                Vector2 dir = Quaternion.Euler(0, 0, ship.Orientation + ang) * Vector2.right;
                RaycastHit2D hit = useCircleCast
                    ? Physics2D.CircleCast(ship.Position, circleRadius, dir, fanRange)
                    : Physics2D.Raycast(ship.Position, dir, fanRange);
                FanHit fh = new FanHit();
                if (hit.collider != null)
                {
                    fh.hit = true;
                    fh.dist = hit.distance;
                    Vector2 normal = hit.normal;
                    Vector2 invNormal = -normal;
                    Vector2 vel = ship.Velocity;
                    fh.normalVelAngle = (vel.sqrMagnitude < 1e-6f) ? 180f : Mathf.Abs(Vector2.SignedAngle(invNormal, vel));
                }
                else
                {
                    fh.hit = false; fh.dist = fanRange; fh.normalVelAngle = 180f;
                }
                res[i] = fh;
            }
            return res;
        }

        private (float dist, bool willHit) NearestBulletThreat(SpaceShipView ship, GameData data)
        {
            if (data == null || data.Bullets == null || data.Bullets.Count == 0) return (float.MaxValue, false);
            float bestD = float.MaxValue; bool will = false;
            foreach (var b in data.Bullets)
            {
                if (b == null) continue;
                Vector2 to = ship.Position - b.Position;
                float d = to.magnitude;
                if (d < bestD) bestD = d;
                Vector2 bv = b.Velocity; if (bv.sqrMagnitude < 1e-6f) continue;
                // Lateral distance from bullet line
                Vector2 perpDir = new Vector2(-bv.y, bv.x).normalized;
                float perp = Mathf.Abs(Vector2.Dot(perpDir, to));
                float along = Vector2.Dot(bv.normalized, to);
                // incoming if along>0 and lateral miss smaller than radius
                if (perp <= ship.Radius * 1.2f && along > 0 && along <= bv.magnitude * 2.0f) will = true;
            }
            return (bestD, will);
        }

        private (float dist, bool willTrigger) NearestMineThreat(SpaceShipView ship, GameData data)
        {
            if (data == null || data.Mines == null || data.Mines.Count == 0) return (float.MaxValue, false);
            float bestD = float.MaxValue; bool will = false;
            foreach (var m in data.Mines)
            {
                if (m == null) continue;
                float d = (m.Position - ship.Position).magnitude;
                if (d < bestD) bestD = d;
                if (d <= ship.Radius * 2.0f && m.IsActive) will = true;
            }
            return (bestD, will);
        }

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

        private bool EnemyInLineOfSight(SpaceShipView ship, GameData data)
        {
            if (data == null || data.SpaceShips == null) return false;
            foreach (var sv in data.SpaceShips)
            {
                if (sv == null || sv.Owner == ship.Owner) continue;
                Vector2 to = sv.Position - ship.Position;
                float ang = Mathf.Abs(Vector2.SignedAngle(ship.LookAt, to));
                if (ang <= 20f)
                {
                    RaycastHit2D h = Physics2D.Raycast(ship.Position, to.normalized, to.magnitude);
                    if (h.collider == null) return true;
                }
            }
            return false;
        }

        private string EncodeState(SpaceShipView ship, GameData data)
        {
            WayPointView target = FindNextTarget(ship, data);
            string distBucket = "noTarget";
            string angleBucket = "na";
            if (target != null)
            {
                float d = (target.Position - ship.Position).magnitude;
                if (d <= target.Radius * _nearFactor) distBucket = "near";
                else if (d <= target.Radius * _midFactor) distBucket = "mid";
                else distBucket = "far";
                float desired = Mathf.Atan2((target.Position - ship.Position).y, (target.Position - ship.Position).x) * Mathf.Rad2Deg;
                float angDiff = Mathf.DeltaAngle(ship.Orientation, desired);
                if (Mathf.Abs(angDiff) <= 20f) angleBucket = "front"; else if (angDiff > 0) angleBucket = "left"; else angleBucket = "right";
            }
            var fan = SampleFan(ship);
            StringBuilder fanKey = new StringBuilder();
            for (int i = 0; i < fan.Length; i++)
            {
                string hit = fan[i].hit ? "1" : "0";
                string distb = fan[i].dist <= fanRange * 0.33f ? "near" : (fan[i].dist <= fanRange * 0.66f ? "mid" : "far");
                string angb = fan[i].normalVelAngle <= 30f ? "aligned" : (fan[i].normalVelAngle <= 80f ? "partial" : "away");
                fanKey.Append($"f{i}:{hit}:{distb}:{angb}|");
            }
            string bestCpKey = "none";
            if (target != null)
            {
                float cpd = (target.Position - ship.Position).magnitude;
                string cpdb = cpd <= target.Radius * _nearFactor ? "near" : (cpd <= target.Radius * _midFactor ? "mid" : "far");
                float desired = Mathf.Atan2((target.Position - ship.Position).y, (target.Position - ship.Position).x) * Mathf.Rad2Deg;
                float angDiff = Mathf.DeltaAngle(ship.Orientation, desired);
                string cpang = Mathf.Abs(angDiff) <= 20f ? "front" : (angDiff > 0 ? "left" : "right");
                float heur = (1f / (1f + cpd)) + (target.Owner == ship.Owner ? -0.5f : 1f);
                string heurB = heur >= 1f ? "high" : (heur >= 0f ? "mid" : "low");
                bestCpKey = $"cp:{cpdb}:{heurB}:{cpang}";
            }
            var bulletThreat = NearestBulletThreat(ship, data);
            var mineThreat = NearestMineThreat(ship, data);
            string bulletKey = $"b:{(bulletThreat.dist < 1e19f ? (bulletThreat.dist <= ship.Radius * 3 ? "near" : "far") : "none")}:{(bulletThreat.willHit ? "will" : "nowill")}";
            string mineKey = $"m:{(mineThreat.dist < 1e19f ? (mineThreat.dist <= ship.Radius * 3 ? "near" : "far") : "none")}:{(mineThreat.willTrigger ? "will" : "nowill")}";
            string energySelf = "mid"; float e = Mathf.Clamp01(ship.Energy);
            if (e >= energyHighThreshold) energySelf = "high"; else if (e <= energyLowThreshold) energySelf = "low";
            string enemyEnergy = "none", enemyDist = "none", enemyAngle = "na", enemyHasShot = "no";
            if (data != null && data.SpaceShips != null)
            {
                SpaceShipView ne = null; float bd = float.MaxValue;
                foreach (var sv in data.SpaceShips)
                { if (sv == null || sv.Owner == ship.Owner) continue; float d = (sv.Position - ship.Position).magnitude; if (d < bd) { bd = d; ne = sv; } }
                if (ne != null)
                {
                    float r = Mathf.Max(0.001f, ship.Radius);
                    if (bd <= r * 3f) enemyDist = "near"; else if (bd <= r * 6f) enemyDist = "mid"; else enemyDist = "far";
                    float desiredE = Mathf.Atan2((ne.Position - ship.Position).y, (ne.Position - ship.Position).x) * Mathf.Rad2Deg;
                    float angDiffE = Mathf.DeltaAngle(ship.Orientation, desiredE);
                    if (Mathf.Abs(angDiffE) <= 20f) enemyAngle = "front"; else if (angDiffE > 0) enemyAngle = "left"; else enemyAngle = "right";
                    float ee = Mathf.Clamp01(ne.Energy); enemyEnergy = ee >= energyHighThreshold ? "high" : (ee <= energyLowThreshold ? "low" : "mid");
                    enemyHasShot = ne.HasShot ? "yes" : "no";
                }
            }
            int myScore = ship.Score, myHitScore = ship.HitScore, myWpScore = ship.WaypointScore;
            int oppScore = 0, oppHitScore = 0, oppWpScore = 0;
            if (data != null && data.SpaceShips != null)
            { foreach (var sv in data.SpaceShips) { if (sv == null || sv.Owner == ship.Owner) continue; oppScore = sv.Score; oppHitScore = sv.HitScore; oppWpScore = sv.WaypointScore; break; } }
            string scoreDiff = (myScore - oppScore) >= 5 ? "ahead" : ((myScore - oppScore) <= -5 ? "behind" : "close");
            string hitDiff = (myHitScore - oppHitScore) >= 3 ? "ahead" : ((myHitScore - oppHitScore) <= -3 ? "behind" : "close");
            string wpDiff = (myWpScore - oppWpScore) >= 3 ? "ahead" : ((myWpScore - oppWpScore) <= -3 ? "behind" : "close");
            string timeKey = "mid"; if (data != null) { float t = data.timeLeft; if (t >= 40f) timeKey = "early"; else if (t <= 20f) timeKey = "late"; }
            string enemyLos = EnemyInLineOfSight(ship, data) ? "yes" : "no";
            return string.Join("|", distBucket, angleBucket, enemyDist, enemyAngle, enemyHasShot, fanKey.ToString(), bestCpKey, bulletKey, mineKey,
                                energySelf, enemyEnergy, enemyLos, scoreDiff, hitDiff, wpDiff, timeKey);
        }

        private InputData ActionToInput(int action, SpaceShipView ship, GameData data)
        {
            int ai = Mathf.Clamp(action, 0, 3 * 3 * 4 - 1);
            int ti = ai / (3 * 4); int rem = ai % (3 * 4); int si = rem / 4; int wi = rem % 4;
            float thrust = thrustLevels[Mathf.Clamp(ti, 0, thrustLevels.Length - 1)];
            float steer = steerAngles[Mathf.Clamp(si, 0, steerAngles.Length - 1)];
            SpaceShipView nearestEnemy = null; float bd = float.MaxValue;
            if (data != null && data.SpaceShips != null)
                foreach (var sv in data.SpaceShips) { if (sv == null || sv.Owner == ship.Owner) continue; float d = (sv.Position - ship.Position).sqrMagnitude; if (d < bd) { bd = d; nearestEnemy = sv; } }
            WayPointView target = FindNextTarget(ship, data);
            float desiredOrient = ship.Orientation;
            if (nearestEnemy != null && wi == 1) desiredOrient = AimingHelpers.ComputeSteeringOrient(ship, nearestEnemy.Position, steeringOvershoot);
            else if (target != null) desiredOrient = AimingHelpers.ComputeSteeringOrient(ship, target.Position, steeringOvershoot);
            desiredOrient += steer;
            // assistance waypoint
            if (target != null)
            {
                float dwp = (target.Position - ship.Position).magnitude;
                if (dwp <= target.Radius * 1.25f)
                {
                    float desiredWp = Mathf.Atan2((target.Position - ship.Position).y, (target.Position - ship.Position).x) * Mathf.Rad2Deg;
                    float angErrWp = Mathf.Abs(Mathf.DeltaAngle(ship.Orientation, desiredWp));
                    if (angErrWp > 30f) thrust = Mathf.Min(thrust, 0.2f);
                    if (dwp <= target.Radius * 0.6f) thrust = Mathf.Min(thrust, 0.35f);
                }
            }
            float energy = ship.Energy;
            bool canShootEnergy = energy >= ship.ShootEnergyCost;
            bool canDrop = energy >= ship.MineEnergyCost;
            bool canShock = energy >= ship.ShockwaveEnergyCost;
            bool shoot = false, dropMine = false, fireShockwave = false;
            // Tir: autoriser le tir si l'agent peut potentiellement toucher ou si l'ennemi est en vue
            if (wi == 1 && nearestEnemy != null && canShootEnergy)
            {
                bool canHit = AimingHelpers.CanHit(ship, nearestEnemy.Position, nearestEnemy.Velocity, shootHitTimeTolerance);
                float angToEnemy = Mathf.Abs(Mathf.DeltaAngle(ship.Orientation, Mathf.Atan2((nearestEnemy.Position - ship.Position).y, (nearestEnemy.Position - ship.Position).x) * Mathf.Rad2Deg));
                bool enemyLos = EnemyInLineOfSight(ship, data);
                // tirer si on peut frapper, si l'ennemi est en ligne de vue, ou si l'angle est faible (visée approximative)
                if (canHit || enemyLos || angToEnemy <= 15f)
                {
                    shoot = true;
                }
                else
                {
                    // orienter vers l'ennemi pour préparer le tir
                    desiredOrient = AimingHelpers.ComputeSteeringOrient(ship, nearestEnemy.Position, steeringOvershoot);
                    shoot = false;
                }
                if (debugActions) Debug.Log($"[QL] ShootDecision canHit={canHit} los={enemyLos} ang={angToEnemy:F1} shoot={shoot}");
            }
            else if (wi == 2 && canDrop) dropMine = true;
            else if (wi == 3 && canShock) fireShockwave = true;
            // évitement mine
            var mt = NearestMineThreat(ship, data);
            if (mt.dist < ship.Radius * 4f && data != null && data.Mines != null)
            {
                MineView closest = null; float bdM = float.MaxValue;
                foreach (var m in data.Mines)
                { if (m == null) continue; float d = (m.Position - ship.Position).magnitude; if (d < bdM) { bdM = d; closest = m; } }
                if (closest != null)
                {
                    Vector2 away = (ship.Position - closest.Position).normalized; float awayAng = Mathf.Atan2(away.y, away.x) * Mathf.Rad2Deg;
                    desiredOrient = Mathf.LerpAngle(desiredOrient, awayAng, 0.6f); thrust = Mathf.Min(thrust, 0.5f);
                    if (mt.willTrigger || mt.dist < ship.Radius * 2.2f)
                    { thrust = Mathf.Min(thrust, 0.2f); if (ship.Energy < ship.ShockwaveEnergyCost) { shoot = false; dropMine = false; fireShockwave = false; } }
                }
            }
            // shockwave proximité
            if (nearestEnemy != null && ship.Energy >= ship.ShockwaveEnergyCost)
            {
                float ed = (nearestEnemy.Position - ship.Position).magnitude;
                float trigger = ship.Radius + nearestEnemy.Radius;
                if (ed <= trigger * 1.05f)
                { fireShockwave = true; shoot = false; dropMine = false; thrust = 0f; float toE = Mathf.Atan2((nearestEnemy.Position - ship.Position).y, (nearestEnemy.Position - ship.Position).x) * Mathf.Rad2Deg; desiredOrient = Mathf.LerpAngle(desiredOrient, toE, 0.5f); }
            }
            if (wi == 1 && !shoot) thrust = Mathf.Max(thrust * 0.5f, 0f);
            var bt = NearestBulletThreat(ship, data);
            if (bt.willHit && ship.Energy >= ship.MineEnergyCost)
            { dropMine = true; shoot = false; fireShockwave = false; thrust = Mathf.Min(thrust, 0.1f); if (debugActions) Debug.Log($"[QL] Defensive drop-mine due to bullet threat dist={bt.dist:F2}"); }
            int justHit = ship.HitCount - _prevHitCount;
            if (justHit > 0 && ship.Energy >= ship.ShockwaveEnergyCost)
            { fireShockwave = true; shoot = false; dropMine = false; thrust = 0f; if (debugActions) Debug.Log("[QL] Auto shockwave triggered after receiving hit"); }
            return new InputData(thrust, desiredOrient, shoot, dropMine, fireShockwave);
        }
        public override InputData UpdateInput(SpaceShipView ship, GameData data)
        {
            if (_agent == null) Initialize(ship, data);
            _shipView = ship; _data = data;
            string state = EncodeState(ship, data);
            int action = _agent.ChooseAction(state, greedy: !TrainingMode);
            RewardDetails rd = new RewardDetails(); float reward = 0f;
            if (_prevAction >= 0 && _prevState != null)
            {
                int wpDiff = ship.WaypointScore - _prevWaypointScore; rd.wpDiff = wpDiff;
                int scoreDiff = ship.Score - _prevScore; rd.scoreDiff = scoreDiff;
                int hitScDiff = ship.HitScore - _prevHitScore; rd.hitScDiff = hitScDiff;
                int gotHitDiff = ship.HitCount - _prevHitCount; rd.gotHitDiff = gotHitDiff;
                if (wpDiff > 0) reward += rewardPerWaypoint * wpDiff;
                if (scoreDiff > 0) reward += rewardPerScore * scoreDiff;
                if (hitScDiff > 0) reward += rewardForHit * hitScDiff;
                if (gotHitDiff > 0) reward += penaltyOnHit * gotHitDiff;
                int pai = _prevAction; int pti = pai / (3 * 4); int prem = pai % (3 * 4); int psi = prem / 4; int pwi = prem % 4;
                if (pwi == 3) { reward += rewardForShockwave; rd.shockwave = rewardForShockwave; }
                float highPen = 0f, lowPen = 0f;
                if (useEnergyPenalty)
                {
                    float en = Mathf.Clamp01(ship.Energy);
                    if (en > energyHighThreshold)
                    { float factor = (en - energyHighThreshold) / Mathf.Max(1e-6f, 1f - energyHighThreshold); highPen = penaltyHighEnergy * factor; reward += highPen; }
                    if (en < energyLowThreshold)
                    { float factor = (energyLowThreshold - en) / Mathf.Max(1e-6f, energyLowThreshold); lowPen = penaltyLowEnergy * factor; reward += lowPen; }
                }
                reward += livingPenalty; rd.living = livingPenalty; rd.energyHighPenalty = highPen; rd.energyLowPenalty = lowPen; rd.total = reward;
                _agent.Learn(_prevState, _prevAction, reward, state, false);
            }
            _lastReward = rd;
            InputData input = ActionToInput(action, ship, data);
            if (debugVerbose)
            {
                _debugTimer += Time.deltaTime;
                if (_debugTimer >= debugVerboseInterval)
                {
                    _debugTimer = 0f;
                    var sb = new StringBuilder(); sb.Append("[QL][STEP] "); sb.AppendFormat("StateHash={0} Len={1} ", state.GetHashCode(), state.Length);
                    int ai = action; int ti = ai / (3 * 4); int rem = ai % (3 * 4); int si = rem / 4; int wi = rem % 4;
                    sb.AppendFormat("Action={0} (thrustIdx={1} steerIdx={2} weaponIdx={3}) ", action, ti, si, wi);
                    sb.AppendFormat("Reward={0:F3} [wp={1} sc={2} hit={3} gotHit={4} eHi={5:F3} eLo={6:F3} liv={7:F3} shkw={8:F3}] ", _lastReward.total, _lastReward.wpDiff, _lastReward.scoreDiff, _lastReward.hitScDiff, _lastReward.gotHitDiff, _lastReward.energyHighPenalty, _lastReward.energyLowPenalty, _lastReward.living, _lastReward.shockwave);
                    sb.AppendFormat("Eps={0:F3} Alpha={1:F2} Gamma={2:F3}", _agent.Epsilon, _agent.Alpha, _agent.Gamma);
                    Debug.Log(sb.ToString());
                }
            }
            _prevState = state; _prevAction = action; _prevWaypointScore = ship.WaypointScore; _prevScore = ship.Score; _prevHitScore = ship.HitScore; _prevHitCount = ship.HitCount;
            return input;
        }
        public void SaveAgent() { if (_agent == null) return; _agent.Save(SaveFileName); }
        public void LoadAgent() { if (_agent == null) _agent = new QLearningAgent(); _agent.Load(SaveFileName); }
        public void ApplyHyperParamsAndReset(float alpha, float gamma, float epsilonStart, float epsilonDecay, float minEpsilon, bool resetTable)
        {
            if (_agent == null || resetTable) _agent = new QLearningAgent();
            _alpha = alpha; _gamma = gamma; _epsilon = epsilonStart; _epsilonDecay = epsilonDecay; _minEpsilon = minEpsilon;
            _agent.Initialize(3 * 3 * 4, _alpha, _gamma, _epsilon); _agent.EpsilonDecay = _epsilonDecay; _agent.MinEpsilon = _minEpsilon;
        }
        public void ApplyTerminalReward(float reward)
        { if (_agent == null) return; if (_prevState != null && _prevAction >= 0) _agent.Learn(_prevState, _prevAction, reward, string.Empty, true); }
        public void ApplyTerminalResult(bool win) { float r = win ? terminalWinReward : terminalLossPenalty; ApplyTerminalReward(r); }
    }
}

