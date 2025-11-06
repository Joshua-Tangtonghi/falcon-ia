using DoNotModify;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Teams.ExampleTeam
{
    public class FalconIADom : BaseSpaceShipController
    {
        public override InputData UpdateInput(SpaceShipView spaceShip, GameData gameData)
        {
            return GoToPosition(spaceShip, FindNearestFreeWaypointPosition(spaceShip, gameData), 1f);
        }
        private float ComputeOrientationTowards(Vector2 fromPos, Vector2 toPos)
        {
            Vector2 dir = toPos - fromPos;
            return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        }
        private InputData GoToPosition(SpaceShipView ship, Vector2 targetPosition, float thrust)
        {
            // --- ANTICIPATION : prédit la position future de la cible en fonction de la vitesse actuelle du vaisseau ---
            float anticipationFactor = 1f; // entre 0.3 et 1.0 selon la réactivité souhaitée
            Vector2 predictedTarget = targetPosition + ship.Velocity * anticipationFactor;

            // --- Orientation (conserve ta LookAt existante) ---
            InputData data = LookAt(ship, predictedTarget);

            // --- Calcul de l'angle de différence entre le vaisseau et la cible ---
            float angleDiff = Mathf.Abs(Mathf.DeltaAngle(ship.Orientation, data.targetOrientation));

            // --- Distance à la cible ---
            float distance = Vector2.Distance(ship.Position, targetPosition);

            // --- Ajustement dynamique du thrust ---
            // 1. On pousse plus fort quand on est bien orienté et loin
            // 2. On réduit le thrust quand on est désaligné ou proche
            float orientationFactor = Mathf.Clamp01(1f - (angleDiff / 180f)); // 1 = bien aligné, 0 = mal aligné
            float distanceFactor = Mathf.Clamp01(distance/100); // moins de poussée à courte distance

            data.thrust = thrust * orientationFactor * distanceFactor;

            return data;
        }
        private InputData EnemyNextPosition(SpaceShipView ship, SpaceShipView enemyShip)
        {
            InputData data = LookAt(ship, enemyShip.Position);
            return data;
        }

        private InputData LookAt(SpaceShipView ship,Vector2 targetPosition)
        {
            return new InputData(0f, ComputeOrientationTowards(ship.Position, targetPosition), false, false, false);
        }
        private InputData Shoot()
        {
            return new InputData(0f, 0f, true, false, false);
        }
        private InputData ShootInDirection(SpaceShipView ship, Vector2 targetPosition)
        {
            InputData data = Shoot();
            data.targetOrientation = ComputeOrientationTowards(ship.Position, targetPosition);
            return data;
        }
        private InputData LayMine()
        {
            return new InputData(0f, 0f, false, true, false);

        }
        //Pose une mine dans la direction opposer, exemple bullet
        private InputData LayMineInDirection(Vector2 targetPosition, SpaceShipView ship)
        {
            InputData data = LayMine();
            data.targetOrientation = ComputeOrientationTowards(targetPosition, ship.Position);
            data.thrust = 1f;
            return data;
        }
        private InputData Shockwave()
        {
            return new InputData(0f, 0f, false, false, true);
        }
        private Vector2 FindNearestFreeWaypointPosition(SpaceShipView ship, GameData gameData)
        {
            WayPointView closestFreeWaypoint = null; float closestFreeWaypointDistance = float.PositiveInfinity;
            foreach (WayPointView waypoint in gameData.WayPoints)
            {
                float distanceBetweenWaypointShip = Vector2.Distance(waypoint.Position, ship.Position);
                if (distanceBetweenWaypointShip < closestFreeWaypointDistance && waypoint.Owner != ship.Owner)
                {
                    closestFreeWaypoint = waypoint;
                    closestFreeWaypointDistance = distanceBetweenWaypointShip;
                }
            }
            return closestFreeWaypoint.Position;
        }
        //private Vector2 FindNearestEnemyWaypointPosition(SpaceShipView ship, GameData gameData)

    }

}
