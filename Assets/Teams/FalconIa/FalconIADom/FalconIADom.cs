using DoNotModify;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Teams.ExampleTeam
{
    public class FalconIADom : BaseSpaceShipController
    {
        private enum ShipState
        {
            CaptureState,
            CombatState,
            Shockaka
        }
        private ShipState currentState = ShipState.CombatState;
        public SpaceShipView _SpaceShip { get; private set; }
        public GameData _GameData { get; private set; }

        public override void Initialize(SpaceShipView ship, GameData gameData)
        {
            _SpaceShip = ship;
            _GameData = gameData;
        }
        public override InputData UpdateInput(SpaceShipView ship, GameData gameData)
        {
            if (gameData.timeLeft < 20f)
                currentState = ShipState.CaptureState;
            else currentState = ShipState.CombatState;

            switch (currentState)
            {
                case ShipState.CaptureState:
                    return CaptureStateUpdate();

                case ShipState.CombatState:
                    return CombatStateUpdate();

                case ShipState.Shockaka:
                    return ShockakaUpdate();

                default:
                    return new InputData();
            }
        }
        InputData ShockakaUpdate()
        {
            return new InputData(0f, 0f, false, false, true);
        }
        int lastWaypointCount = 0;
        bool shock = true;
        InputData CaptureStateUpdate()
        {
            if (shock)
            {
                shock = false;
                return new InputData(0f,0f, false, false, true);
            }
            // Cherche le waypoint le plus proche
            Vector2 currentTarget = FindNearestFreeWaypointPosition();

            // Anticipe le prochain
            Vector2? nextTarget = GetNextWaypointPosition(currentTarget);

            InputData input = GoToPosition(currentTarget, nextTarget);

            if (_SpaceShip.WaypointScore > lastWaypointCount)
            {
                input.dropMine = true;
                lastWaypointCount = _SpaceShip.WaypointScore;
                return input;
            }
            lastWaypointCount = _SpaceShip.WaypointScore;
            input.dropMine = false;
            return input;
        }
        private SpaceShipView currentTarget; // l’ennemi que l’on suit
        private float lastCloseShootTime = 0f;
        private float closeShootInterval = 0.5f; // tir toutes les 0.5s quand proche

        InputData CombatStateUpdate()
        {
            if (currentTarget == null)
            {
                // Choisir un nouvel ennemi proche
                currentTarget = _GameData.SpaceShips
                    .Where(s => s.Owner != _SpaceShip.Owner)
                    .OrderBy(s => Vector2.Distance(_SpaceShip.Position, s.Position))
                    .FirstOrDefault();

                if (currentTarget == null)
                    return new InputData(); // pas d'ennemi
            }

            // 🚀 Foncer sur l'ennemi
            InputData input = GoToEnemyPosition(currentTarget);

            // 🔫 Tir prédictif
            float distanceToEnemy = Vector2.Distance(_SpaceShip.Position, currentTarget.Position);
            float minDistanceToShoot = 7.5f; // distance minimale pour tirer
            float closeDistance = 3f;        // distance rapprochée pour tir rapide

            float bulletSpeed = 5f;
            Vector2 toEnemy = currentTarget.Position - _SpaceShip.Position;
            Vector2 enemyVel = currentTarget.Velocity;
            float travelTime = toEnemy.magnitude / bulletSpeed;
            Vector2 predictedPos = currentTarget.Position + enemyVel * travelTime;

            input.targetOrientation = Mathf.Atan2(predictedPos.y - _SpaceShip.Position.y,
                                                   predictedPos.x - _SpaceShip.Position.x) * Mathf.Rad2Deg;

            // Tir logique
            if (distanceToEnemy <= closeDistance)
            {
                // Tir rapide uniquement si HitPenaltyCountdown == 0
                if (currentTarget.HitPenaltyCountdown == 0f)
                {
                    if (Time.time - lastCloseShootTime >= closeShootInterval)
                    {
                        input.shoot = true;
                        lastCloseShootTime = Time.time;
                    }
                    else
                    {
                        input.shoot = false;
                    }
                }
                else
                {
                    input.shoot = false; // pas de tir si HitPenaltyCountdown > 0
                }
            }
            else if (currentTarget.HitPenaltyCountdown == 0 && distanceToEnemy <= minDistanceToShoot)
            {
                // Tir normal basé sur HitPenaltyCountdown
                input.shoot = true;
            }
            else
            {
                input.shoot = false;
            }

            return input;
        }
        float asteroidAvoidDistance = 1f;
        float MineAvoidDistance = 2.2f;
        float bulletAvoidDistance = 7.5f;
        float enemyShipAvoidDistance = 7.5f;

        private Vector2 previousAvoidVectorBullet = Vector2.zero;
        private Vector2 previousAvoidVectorEnemy = Vector2.zero;
        public InputData GoToPosition(Vector2 targetPosition, Vector2? nextTarget = null)
        {
            // 🧭 Direction vers la cible
            Vector2 toTarget = (targetPosition - _SpaceShip.Position).normalized;

            // Anticipation du prochain waypoint
            if (nextTarget.HasValue)
            {
                Vector2 toNext = (nextTarget.Value - targetPosition).normalized;
                toTarget = Vector2.Lerp(toTarget, toNext, 0.2f).normalized;
            }

            // ⚠️ Évitement des obstacles (astéroïdes et mines)
            Vector2 avoidVector = Vector2.zero;

            // Astéroïdes
            foreach (var asteroid in _GameData.Asteroids)
            {
                Vector2 toAsteroid = asteroid.Position - _SpaceShip.Position;
                float distance = toAsteroid.magnitude;
                if (distance < asteroid.Radius + asteroidAvoidDistance)
                {
                    avoidVector -= (toAsteroid / distance);
                }
            }

            // Mines
            foreach (var mine in _GameData.Mines)
            {
                if (mine.IsActive)
                {
                    Vector2 toMine = mine.Position - _SpaceShip.Position;
                    float distance = toMine.magnitude;
                    if (distance < MineAvoidDistance)
                    {
                        avoidVector -= (toMine / distance);
                    }
                }
            }

            // Combine direction + évitement
            Vector2 finalDir = (toTarget + avoidVector).normalized;

            // Orientation cible
            float targetOrientation = Mathf.Atan2(finalDir.y, finalDir.x) * Mathf.Rad2Deg;

            // Alignement avec la vélocité
            float velocityAlignment = 0f;
            if (_SpaceShip.Velocity.sqrMagnitude > 0.01f)
            {
                Vector2 velDir = _SpaceShip.Velocity.normalized;
                velocityAlignment = Vector2.Dot(velDir, toTarget);
            }

            // Calcul du thrust
            float distanceToTarget = Vector2.Distance(_SpaceShip.Position, targetPosition);
            float thrust = 1f;
            if (distanceToTarget < 0.7f)
                thrust = 0f;
            else if (velocityAlignment > 0.9f && Mathf.Abs(Mathf.DeltaAngle(_SpaceShip.Orientation, targetOrientation)) < 10f)
                thrust = 0.5f;
            else if (Mathf.Abs(Mathf.DeltaAngle(_SpaceShip.Orientation, targetOrientation)) > 40f)
                thrust = 0f;

            return new InputData(thrust, targetOrientation, false, false, false);
        }
        public InputData GoToEnemyPosition(SpaceShipView targetEnemy)
        {
            if (targetEnemy == null)
                return new InputData();

            // 🧭 Direction vers l'ennemi
            Vector2 toTarget = (targetEnemy.Position - _SpaceShip.Position).normalized;

            // ⚠️ Évitement des astéroïdes uniquement
            Vector2 avoidVector = Vector2.zero;

            foreach (var asteroid in _GameData.Asteroids)
            {
                Vector2 toAsteroid = asteroid.Position - _SpaceShip.Position;
                float distance = toAsteroid.magnitude;

                if (distance < asteroid.Radius + asteroidAvoidDistance)
                {
                    // Repoussement proportionnel à la proximité
                    avoidVector -= (toAsteroid / distance);
                }
            }

            // Combine direction vers l'ennemi + évitement astéroïdes
            Vector2 finalDir = (toTarget + avoidVector).normalized;

            // 🎯 Orientation cible
            float targetOrientation = Mathf.Atan2(finalDir.y, finalDir.x) * Mathf.Rad2Deg;
            float angleDiff = Mathf.Abs(Mathf.DeltaAngle(_SpaceShip.Orientation, targetOrientation));

            // 💨 Alignement avec la vélocité
            float velocityAlignment = 0f;
            if (_SpaceShip.Velocity.sqrMagnitude > 0.01f)
            {
                Vector2 velDir = _SpaceShip.Velocity.normalized;
                velocityAlignment = Vector2.Dot(velDir, finalDir);
            }

            // ⚙️ Calcul du thrust (toujours max, fonceur)
            float thrust = 1f;

            // Réduction du thrust uniquement si très proche de l'ennemi
            float distanceToEnemy = Vector2.Distance(_SpaceShip.Position, targetEnemy.Position);
            if (distanceToEnemy < 0.5f)
                thrust = 0f;

            return new InputData(thrust, targetOrientation, false, false, false);
        }
        public InputData EnemyNextPosition(SpaceShipView ship, SpaceShipView enemyShip)
        {
            InputData data = LookAt(enemyShip.Position);
            return data;
        }
        private float ComputeOrientationTowards(Vector2 fromPos, Vector2 toPos)
        {
            Vector2 dir = toPos - fromPos;
            return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        }
        public InputData LookAt(Vector2 targetPosition)
        {
            float targetOrientation = ComputeOrientationTowards(_SpaceShip.Position, targetPosition);

            InputData data = new InputData();
            data.targetOrientation = targetOrientation;

            return data;
        }
        public InputData Shoot()
        {
            InputData data = new InputData();
            data.shoot = true;
            return data;
        }
        public InputData ShootInDirection(Vector2 targetPosition)
        {
            InputData data = LookAt(targetPosition);
            data.shoot = true;
            return data;
        }
        public InputData DropMine()
        {
            InputData data = new InputData();
            data.dropMine = true;
            return data;
        }
        //Pose une mine dans la direction opposer, exemple bullet
        public InputData LayMineInDirection(Vector2 targetPosition)
        {
            InputData data = LookAt(-targetPosition);
            data.dropMine = true;
            data.thrust = 1f;
            return data;
        }
        public InputData Shockwave()
        {
            return new InputData(0f, 0f, false, false, true);
        }
        public Vector2 FindNearestFreeWaypointPosition()
        {
            WayPointView closestFreeWaypoint = null; float closestFreeWaypointDistance = float.PositiveInfinity;
            foreach (WayPointView waypoint in _GameData.WayPoints)
            {
                float distanceBetweenWaypointShip = Vector2.Distance(waypoint.Position, _SpaceShip.Position);
                if (distanceBetweenWaypointShip < closestFreeWaypointDistance && waypoint.Owner != _SpaceShip.Owner)
                {
                    closestFreeWaypoint = waypoint;
                    closestFreeWaypointDistance = distanceBetweenWaypointShip;
                }
            }
            if (closestFreeWaypoint != null)
            {
                return closestFreeWaypoint.Position;
            }
            return Vector2.zero;
        }
        public Vector2? GetNextWaypointPosition(Vector2 currentTarget)
        {
            // Récupère tous les waypoints
            var waypoints = _GameData.WayPoints;
            if (waypoints == null || waypoints.Count == 0)
                return null;

            // Trouve l’index du waypoint actuel
            int currentIndex = waypoints.FindIndex(w => (Vector2)w.Position == currentTarget);
            if (currentIndex == -1)
                return null;

            // 🔁 Si on veut faire une boucle :
            if (currentIndex < waypoints.Count - 1)
                return waypoints[currentIndex + 1].Position;
            else
                return waypoints[0].Position; // boucle au début
        }
        //private Vector2 FindNearestEnemyWaypointPosition(SpaceShipView ship, GameData gameData)

    }

}
