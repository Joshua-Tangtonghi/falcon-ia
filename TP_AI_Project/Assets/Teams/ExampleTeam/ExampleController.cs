using UnityEngine;
using DoNotModify;

namespace Teams.ExampleTeam
{

	public class ExampleController : BaseSpaceShipController
	{
		[Header("Movement")]
		[SerializeField] private float fullThrust = 1.0f;
		[SerializeField] private float approachThrust = 0.4f;
		[SerializeField] private float slowThrust = 0.2f;
		[SerializeField] private float approachFactor = 1.2f;

		[Header("Avoidance")]
		[SerializeField] private float avoidThrust = 0.5f;
		[SerializeField] private float avoidAheadDistance = 4.0f;
		[SerializeField] private float avoidConeAngle = 60.0f;

		[Header("Combat")]
		[SerializeField] private float attackRange = 6.0f;
		[SerializeField] private float attackAngleThreshold = 15.0f;
		[SerializeField] private float shootCooldown = 1.0f;

		// Properties pour modularité
		public float FullThrust { get => fullThrust; set => fullThrust = value; }
		public float ApproachThrust { get => approachThrust; set => approachThrust = value; }
		public float SlowThrust { get => slowThrust; set => slowThrust = value; }
		public float ApproachFactor { get => approachFactor; set => approachFactor = value; }
		public float AvoidThrust { get => avoidThrust; set => avoidThrust = value; }
		public float AvoidAheadDistance { get => avoidAheadDistance; set => avoidAheadDistance = value; }
		public float AvoidConeAngle { get => avoidConeAngle; set => avoidConeAngle = value; }
		public float AttackRange { get => attackRange; set => attackRange = value; }
		public float AttackAngleThreshold { get => attackAngleThreshold; set => attackAngleThreshold = value; }
		public float ShootCooldown { get => shootCooldown; set => shootCooldown = value; }

		// internal
		private float _shootTimer;

		public override void Initialize(SpaceShipView spaceship, GameData data)
		{
			_shootTimer = 0f;
		}

		private WayPointView FindNearestEnemyWaypoint(SpaceShipView ship, GameData data)
		{
			if (data == null || data.WayPoints == null || data.WayPoints.Count == 0) return null;
			WayPointView best = null; float bd = float.MaxValue;
			foreach (var w in data.WayPoints)
			{
				if (w.Owner == ship.Owner) continue;
				float d = (w.Position - ship.Position).sqrMagnitude;
				if (d < bd) { bd = d; best = w; }
			}
			return best;
		}

		private AsteroidView DetectThreat(SpaceShipView ship, GameData data)
		{
			if (data == null || data.Asteroids == null) return null;
			AsteroidView nearest = null; float best = float.MaxValue;
			foreach (AsteroidView a in data.Asteroids)
			{
				if (a == null) continue;
				Vector2 to = a.Position - ship.Position; float d = to.magnitude;
                if (d > avoidAheadDistance) continue;
                float ang = Mathf.Abs(Vector2.SignedAngle(ship.LookAt, to));
                if (ang > avoidConeAngle * 0.5f) continue;
				if (d < best) { best = d; nearest = a; }
			}
			return nearest;
		}

		private float ComputeOrientationTowards(Vector2 fromPos, Vector2 toPos)
		{
			Vector2 dir = toPos - fromPos;
			return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
		}

		public override InputData UpdateInput(SpaceShipView ship, GameData data)
		{
			// safety
			if (ship == null || data == null) return new InputData(0f, ship != null ? ship.Orientation : 0f, false, false, false);

			// update timers
			if (_shootTimer > 0f) _shootTimer -= Time.deltaTime;

			// detect threat (asteroid)
			AsteroidView threat = DetectThreat(ship, data);
			if (threat != null)
			{
				// Evade behaviour
				Vector2 toAst = threat.Position - ship.Position;
				float side = Mathf.Sign(Vector2.SignedAngle(ship.LookAt, toAst));
				Vector2 avoidDir = Quaternion.Euler(0, 0, -side * 90f) * ship.LookAt;
				float desiredOrientAvoid = Mathf.Atan2(avoidDir.y, avoidDir.x) * Mathf.Rad2Deg;
				return new InputData(AvoidThrust, desiredOrientAvoid, false, false, false);
			}

			// detect enemy ship
			SpaceShipView enemy = data.GetSpaceShipForOwner(1 - ship.Owner);
			if (enemy != null)
			{
				float dist = (enemy.Position - ship.Position).magnitude;
				float desiredOrientToEnemy = ComputeOrientationTowards(ship.Position, enemy.Position);
				float angDiff = Mathf.Abs(Mathf.DeltaAngle(ship.Orientation, desiredOrientToEnemy));
				if (dist <= AttackRange)
				{
					// Attack behaviour
					bool canShoot = (_shootTimer <= 0f) && (ship.Energy > ship.ShootEnergyCost) && (angDiff <= AttackAngleThreshold);
					if (canShoot)
					{
						_shootTimer = ShootCooldown;
						return new InputData(0.3f, desiredOrientToEnemy, true, false, false);
					}
					// else approach/keep facing
					float thrust = dist > ship.Radius * ApproachFactor ? FullThrust : ApproachThrust;
					return new InputData(thrust, desiredOrientToEnemy, false, false, false);
				}
			}

			// otherwise seek nearest enemy waypoint
			WayPointView target = FindNearestEnemyWaypoint(ship, data);
			if (target == null)
			{
				return new InputData(0f, ship.Orientation, false, false, false);
			}

			// if waypoint owned by us (maybe changed) find next
			if (target.Owner == ship.Owner)
			{
				return new InputData(0f, ship.Orientation, false, false, false);
			}

			float distance = (target.Position - ship.Position).magnitude;
			float desiredOrientWaypoint = ComputeOrientationTowards(ship.Position, target.Position);
			float thrustToUse = distance > target.Radius * ApproachFactor ? FullThrust : ApproachThrust;
			// slow down a bit when very close
			if (distance <= target.Radius * 0.5f) thrustToUse = SlowThrust;
			return new InputData(thrustToUse, desiredOrientWaypoint, false, false, false);
		}
	}
}