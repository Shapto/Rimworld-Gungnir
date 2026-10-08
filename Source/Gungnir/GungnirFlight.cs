using RimWorld;
using SingularityFramework.Geometry;
using SingularityFramework.Relics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Gungnir
{
    public enum GungnirFlightMode
    {
        Throw,
        Return,
        Stuck
    }

    /// <summary>
    /// Gungnir in the air. Carries the weapon itself, follows a path to its destination pawn and re-paths when they move.
    /// </summary>
    public class GungnirFlight : Thing, IThingHolder
    {
        private const float CellsPerTick = 0.5f;
        private const int RepathIntervalTicks = 10;
        private const int StuckCheckIntervalTicks = 15;
        private const int ThrowStunTicks = 120;
        private const int PartialCatchStunTicks = 60;

        /// <summary>
        /// Which way the spearhead points in the texture, in degrees clockwise from straight up. Tweak until the tip leads in flight.
        /// </summary>
        private const float SpriteTipAngle = 45f;

        private ThingOwner<Thing> carriedGungnir;
        private Pawn thrower;
        private Pawn destinationPawn;
        private GungnirFlightMode mode;

        private List<IntVec3> pathCells = new List<IntVec3>();
        private Vector3 exactPosition;
        private float travelAngle;
        private IntVec3 lastDestinationCell;
        private int ticksUntilRepath;
        private IntVec3 stuckWallCell;

        private const float MaximumTurnDegreesPerTick = 40f;

        /// <summary>
        /// The angle the spear is drawn at. Follows travelAngle at a capped turn speed, so sharp turns swing instead of snapping.
        /// </summary>
        private float drawAngle;
        private bool hasDrawAngle;

        /// <summary>
        /// True while flying a straight line because no route exists, either at a wall to hit or straight at the target.
        /// </summary>
        private bool isFlyingStraight;

        private GungnirFlightMode modeBeforeStuck;

        /// <summary>
        /// True for a flight heading home, including one that's stuck in a wall on its way.
        /// </summary>
        private bool IsReturnFlight => mode == GungnirFlightMode.Return || (mode == GungnirFlightMode.Stuck && modeBeforeStuck == GungnirFlightMode.Return);

        /// <summary>
        /// The wall the straight flight is charging at, or invalid if the line to the target is clear.
        /// </summary>
        private IntVec3 chargeWallCell = IntVec3.Invalid;

        public GungnirFlight()
        {
            carriedGungnir = new ThingOwner<Thing>(this, true);
        }

        public Thing CarriedGungnir => carriedGungnir.Count > 0 ? carriedGungnir[0] : null;
        public bool IsReturning => mode == GungnirFlightMode.Return;
        public bool IsStuck => mode == GungnirFlightMode.Stuck;

        public IntVec3 StuckWallCell => stuckWallCell;

        public override Vector3 DrawPos => exactPosition;

        private const float MaximumStepLength = 0.5f;
        private const float CornerRoundingDistance = 0.8f;

        /// <summary>
        /// Whether the current route is allowed through closed doors, so the steering safety check uses the same rule as the route.
        /// </summary>
        private bool pathAllowsDoors;

        /// <summary>
        /// True while the destination pawn can still be flown to: alive, spawned and on this map.
        /// </summary>
        private bool DestinationIsValid => destinationPawn != null && !destinationPawn.Dead && destinationPawn.Spawned && destinationPawn.Map == Map && !(IsReturnFlight && destinationPawn.Downed);

        /// <summary>
        /// Things the return flight has already hit, so each takes Gungnir's hit only once per trip.
        /// </summary>
        private HashSet<Thing> alreadyHitThings = new HashSet<Thing>();

        /// <summary>
        /// The 8 directions Gungnir can fly from a cell. Straight ones first, so ties prefer straight lines.
        /// </summary>
        private static readonly IntVec3[] FlightDirections =
        {
            new IntVec3(0, 0, 1),
            new IntVec3(1, 0, 0),
            new IntVec3(0, 0, -1),
            new IntVec3(-1, 0, 0),
            new IntVec3(1, 0, 1),
            new IntVec3(1, 0, -1),
            new IntVec3(-1, 0, -1),
            new IntVec3(-1, 0, 1)
        };


        /// <summary>
        /// Spreads out from the start, cell by cell, until it reaches the goal, then traces the route back.
        /// Returns the cells to fly through (start not included), or null if the goal can't be reached.
        /// Diagonal steps can't squeeze between two blocked cells.
        /// </summary>
        private List<IntVec3> FindFlightCells(IntVec3 startCell, IntVec3 goalCell, bool doorsAllowed)
        {
            if (startCell == goalCell) return new List<IntVec3>();

            Dictionary<IntVec3, IntVec3> cameFrom = new Dictionary<IntVec3, IntVec3> { [startCell] = startCell };
            Queue<IntVec3> frontier = new Queue<IntVec3>();
            frontier.Enqueue(startCell);

            while (frontier.Count > 0)
            {
                IntVec3 currentCell = frontier.Dequeue();
                foreach (IntVec3 direction in FlightDirections)
                {
                    IntVec3 nextCell = currentCell + direction;
                    if (cameFrom.ContainsKey(nextCell)) continue;

                    bool isGoal = nextCell == goalCell;
                    if (!isGoal && !CanFlyThrough(nextCell, doorsAllowed)) continue;

                    bool isDiagonal = direction.x != 0 && direction.z != 0;
                    if (isDiagonal && (!CanFlyThrough(currentCell + new IntVec3(direction.x, 0, 0), doorsAllowed) || !CanFlyThrough(currentCell + new IntVec3(0, 0, direction.z), doorsAllowed))) continue;

                    cameFrom[nextCell] = currentCell;
                    if (isGoal) return TraceFlightCells(cameFrom, startCell, goalCell);
                    frontier.Enqueue(nextCell);
                }
            }
            return null;
        }

        /// <summary>
        /// Turns a cell-by-cell route into as few straight legs as possible: from each point, jump to the farthest
        /// route cell that can be reached in one clear straight line. Returns the corner cells to fly between, ending at the goal.
        /// </summary>
        private List<IntVec3> SmoothFlightCells(IntVec3 startCell, List<IntVec3> flightCells, bool doorsAllowed)
        {
            List<IntVec3> waypoints = new List<IntVec3>();
            IntVec3 legStart = startCell;
            int nextIndex = 0;

            while (nextIndex < flightCells.Count)
            {
                int farthestVisibleIndex = nextIndex;
                for (int candidateIndex = flightCells.Count - 1; candidateIndex > nextIndex; candidateIndex--)
                {
                    if (!IsFlightLineClear(legStart, flightCells[candidateIndex], doorsAllowed)) continue;
                    farthestVisibleIndex = candidateIndex;
                    break;
                }

                waypoints.Add(flightCells[farthestVisibleIndex]);
                legStart = flightCells[farthestVisibleIndex];
                nextIndex = farthestVisibleIndex + 1;
            }
            return waypoints;
        }

        /// <summary>
        /// True if Gungnir can fly in one straight line between two cells: every cell on the line is flyable,
        /// and no diagonal step squeezes between two blocked cells.
        /// </summary>
        private bool IsFlightLineClear(IntVec3 fromCell, IntVec3 toCell, bool doorsAllowed)
        {
            IntVec3 previousCell = fromCell;
            foreach (IntVec3 cell in LineStrike.CellsOnLine(fromCell, toCell))
            {
                if (!CanFlyThrough(cell, doorsAllowed)) return false;

                IntVec3 step = cell - previousCell;
                bool isDiagonalStep = step.x != 0 && step.z != 0;
                if (isDiagonalStep && (!CanFlyThrough(previousCell + new IntVec3(step.x, 0, 0), doorsAllowed) || !CanFlyThrough(previousCell + new IntVec3(0, 0, step.z), doorsAllowed))) return false;

                previousCell = cell;
            }
            return true;
        }

        /// <summary>
        /// Walks the "came from" links back from the goal to the start, then flips them into flying order.
        /// </summary>
        private static List<IntVec3> TraceFlightCells(Dictionary<IntVec3, IntVec3> cameFrom, IntVec3 startCell, IntVec3 goalCell)
        {
            List<IntVec3> flightCells = new List<IntVec3>();
            for (IntVec3 cell = goalCell; cell != startCell; cell = cameFrom[cell])
                flightCells.Add(cell);

            flightCells.Reverse();
            return flightCells;
        }

        /// <summary>
        /// Takes Gungnir out of the thrower's hands and starts a flight at the target.
        /// </summary>
        public static GungnirFlight LaunchThrow(Pawn thrower, ThingWithComps gungnir, Pawn target)
        {
            GungnirFlight flight = (GungnirFlight)ThingMaker.MakeThing(GungnirDefOf.Gungnir_Flight);
            flight.thrower = thrower;
            flight.destinationPawn = target;
            flight.mode = GungnirFlightMode.Throw;

            thrower.equipment.Remove(gungnir);
            flight.carriedGungnir.TryAdd(gungnir);

            flight.exactPosition = thrower.DrawPos;
            flight.exactPosition.y = AltitudeLayer.Projectile.AltitudeFor();
            GenSpawn.Spawn(flight, thrower.Position, thrower.Map);
            GungnirUtility.SetGungnirLocation(thrower, flight);

            if (!flight.TryRepath()) flight.StartStraightFlight();
            return flight;
        }

        /// <summary>
        /// Sends a freed Gungnir back to its wielder from the given cell. If the wielder can't catch it (gone, downed, other map), it drops there instead.
        /// </summary>
        public static void SendHome(Thing gungnir, Pawn wielder, IntVec3 startCell, Map map, Thing alreadyHit = null)
        {
            if (gungnir == null || map == null) return;

            bool wielderCanCatch = GungnirUtility.WielderCanCatch(wielder, map);
            if (!wielderCanCatch)
            {
                GenPlace.TryPlaceThing(gungnir, startCell, map, ThingPlaceMode.Near);
                GungnirUtility.RemoveOpenHand(wielder);
                return;
            }

            GungnirFlight flight = (GungnirFlight)ThingMaker.MakeThing(GungnirDefOf.Gungnir_Flight);
            flight.thrower = wielder;
            flight.carriedGungnir.TryAdd(gungnir);
            flight.exactPosition = startCell.ToVector3Shifted();
            flight.exactPosition.y = AltitudeLayer.Projectile.AltitudeFor();
            GenSpawn.Spawn(flight, startCell, map);
            if (alreadyHit != null) flight.alreadyHitThings.Add(alreadyHit);
            flight.BeginReturn(wielder);
        }

        /// <summary>
        /// Turns this flight into a return to the wielder, from wherever it is: in the air or stuck in a wall.
        /// </summary>
        public void BeginReturn(Pawn wielder)
        {
            thrower = wielder;
            destinationPawn = wielder;
            mode = GungnirFlightMode.Return;
            isFlyingStraight = false;
            chargeWallCell = IntVec3.Invalid;
            GungnirUtility.SetGungnirLocation(wielder, this);

            if (!TryRepath()) StartStraightFlight();
        }

        /// <summary>
        /// A throw that can't finish (its target died or left, or the hit destroyed the part) comes home, or drops if nobody can catch it.
        /// </summary>
        private void ReturnOrDrop()
        {
            if (GungnirUtility.WielderCanCatch(thrower, Map)) BeginReturn(thrower);
            else DropGungnirHere();
        }

        protected override void Tick()
        {
            base.Tick();
            if (Destroyed) return;

            if (mode == GungnirFlightMode.Stuck)
            {
                StuckTick();
                return;
            }

            if (!DestinationIsValid)
            {
                if (IsReturning) DropGungnirHere();
                else ReturnOrDrop();
                return;
            }

            if (isFlyingStraight)
            {
                MoveAlongPath();
                if (Destroyed || pathCells.Count > 0) return;

                if (chargeWallCell.IsValid) ResolveWallHit();
                else if (Position.InHorDistOf(destinationPawn.Position, 1.5f)) ArriveAtDestination();
                else StartStraightFlight();
                return;
            }

            ticksUntilRepath--;
            if (ticksUntilRepath <= 0 || destinationPawn.Position != lastDestinationCell)
            {
                if (!TryRepath())
                {
                    StartStraightFlight();
                    return;
                }
            }

            MoveAlongPath();
            if (Destroyed) return;
            if (pathCells.Count == 0 && Position.InHorDistOf(destinationPawn.Position, 1.5f)) ArriveAtDestination();
        }

        /// <summary>
        /// Reached the destination pawn: a throw hits its target, a return comes home to the wielder.
        /// </summary>
        private void ArriveAtDestination()
        {
            if (IsReturning) ArriveAtWielder();
            else ImpactTarget();
        }

        /// <summary>
        /// Gungnir reached its wielder, and their aptitude decides the catch.
        /// Full: a clean catch. Partial: caught, but the impact stuns them for a second and leaves an arm reverberating.
        /// Unworthy: they can't stop it, and it hits them like it would anyone else.
        /// </summary>
        private void ArriveAtWielder()
        {
            Thing gungnir = CarriedGungnir;
            CompRelicAptitude aptitudeComp = gungnir.TryGetComp<CompRelicAptitude>();
            AptitudeLevel aptitude = aptitudeComp != null ? RelicAptitude.GetAptitude(destinationPawn, aptitudeComp.Props, gungnir.def) : AptitudeLevel.Full;

            if (aptitude == AptitudeLevel.Unworthy)
            {
                SlamIntoWielder();
                return;
            }
            GungnirDefOf.Gungnir_Catch.PlayOneShot(new TargetInfo(destinationPawn.Position, Map));
            carriedGungnir.Remove(gungnir);
            GungnirUtility.RemoveOpenHand(destinationPawn);

            if (destinationPawn.equipment != null && destinationPawn.equipment.Primary == null) destinationPawn.equipment.AddEquipment((ThingWithComps)gungnir);
            else GenPlace.TryPlaceThing(gungnir, destinationPawn.Position, Map, ThingPlaceMode.Near);

            if (aptitude == AptitudeLevel.Partial)
            {
                destinationPawn.stances?.stunner.StunFor(PartialCatchStunTicks, null, false);
                GungnirUtility.ApplyReverberation(destinationPawn);
            }

            Destroy();
        }

        /// <summary>
        /// Gungnir hits this pawn, stuns them and lodges in the part it hit, with droning starting at the first stack.
        /// Returns false, without lodging, if the hit killed them or destroyed the part.
        /// </summary>
        private bool TryImpaleAndLodge(Pawn victim)
        {
            GungnirDefOf.Gungnir_Impact.PlayOneShot(new TargetInfo(victim.Position, victim.Map));
            DamageWorker.DamageResult damageResult = victim.TakeDamage(MakeGungnirDamage(travelAngle));
            BodyPartRecord hitPart = damageResult.LastHitPart;
            if (victim.Dead || hitPart == null || victim.health.hediffSet.PartIsMissing(hitPart)) return false;

            victim.stances?.stunner.StunFor(ThrowStunTicks, thrower);

            Hediff_GungnirDroning droning = (Hediff_GungnirDroning)HediffMaker.MakeHediff(GungnirDefOf.Gungnir_Droning, victim, hitPart);
            victim.health.AddHediff(droning, hitPart);
            droning.LodgeGungnir(CarriedGungnir, thrower);
            droning.Severity = 0.01f;
            GungnirUtility.SetGungnirLocation(thrower, victim);
            return true;
        }

        /// <summary>
        /// Recomputes the flight route to the destination: around everything first, through doors only if there's no other way.
        /// Returns false if there's no route at all, and the caller falls back to a straight flight at the wall.
        /// </summary>
        private bool TryRepath()
        {
            lastDestinationCell = destinationPawn.Position;
            ticksUntilRepath = RepathIntervalTicks;

            return TryFindFlightPath(false) || TryFindFlightPath(true);
        }

        /// <summary>
        /// Finds a flight route with or without passing through closed doors, and smooths it into waypoints. Returns false if none exists.
        /// </summary>
        private bool TryFindFlightPath(bool doorsAllowed)
        {
            List<IntVec3> flightCells = FindFlightCells(Position, destinationPawn.Position, doorsAllowed);
            if (flightCells == null) return false;

            pathCells = SmoothFlightCells(Position, flightCells, doorsAllowed);
            pathAllowsDoors = doorsAllowed;
            return true;
        }

        /// <summary>
        /// Flies toward the next waypoint in small steps, rounding off each corner, and tells OnEnteredCell about every cell it crosses.
        /// </summary>
        private void MoveAlongPath()
        {
            float movementLeft = CellsPerTick;
            while (movementLeft > 0f && pathCells.Count > 0)
            {
                Vector3 waypointCenter = FlatCenter(pathCells[0]);
                Vector3 towardWaypoint = waypointCenter - exactPosition;
                float distanceToWaypoint = towardWaypoint.magnitude;
                bool hasNextWaypoint = pathCells.Count > 1;

                if (distanceToWaypoint < 0.05f || (hasNextWaypoint && distanceToWaypoint < CornerRoundingDistance * 0.5f))
                {
                    pathCells.RemoveAt(0);
                    continue;
                }

                Vector3 aimPoint = waypointCenter;
                if (hasNextWaypoint && distanceToWaypoint < CornerRoundingDistance && IsGentleTurn(pathCells[0], pathCells[1]))
                {
                    float cornerBlend = 1f - distanceToWaypoint / CornerRoundingDistance;
                    aimPoint = Vector3.Lerp(waypointCenter, FlatCenter(pathCells[1]), cornerBlend * 0.5f);
                }

                float stepLength = Mathf.Min(movementLeft, MaximumStepLength);
                if (!hasNextWaypoint) stepLength = Mathf.Min(stepLength, distanceToWaypoint);

                Vector3 step = (aimPoint - exactPosition).normalized * stepLength;
                IntVec3 steppedCell = (exactPosition + step).ToIntVec3();
                if (steppedCell != Position && !CanFlyThrough(steppedCell, pathAllowsDoors)) step = towardWaypoint.normalized * Mathf.Min(stepLength, distanceToWaypoint);

                exactPosition += step;
                movementLeft -= stepLength;
                if (step.sqrMagnitude > 0.0001f) travelAngle = step.AngleFlat();

                IntVec3 currentCell = exactPosition.ToIntVec3();
                if (currentCell == Position) continue;

                Position = currentCell;
                OnEnteredCell(currentCell);
                if (Destroyed) return;
            }
            if (!hasDrawAngle)
            {
                drawAngle = travelAngle;
                hasDrawAngle = true;
            }
            drawAngle = Mathf.MoveTowardsAngle(drawAngle, travelAngle, MaximumTurnDegreesPerTick);
        }

        /// <summary>
        /// True if turning at this waypoint toward the next one is 90 degrees or less. Sharper turns aren't rounded,
        /// because the blend point would be behind Gungnir and make it double back.
        /// </summary>
        private bool IsGentleTurn(IntVec3 cornerCell, IntVec3 nextCell)
        {
            Vector3 incomingDirection = FlatCenter(cornerCell) - exactPosition;
            Vector3 outgoingDirection = FlatCenter(nextCell) - FlatCenter(cornerCell);
            return Vector3.Dot(incomingDirection, outgoingDirection) >= 0f;
        }

        /// <summary>
        /// A cell's center at Gungnir's flying height.
        /// </summary>
        private Vector3 FlatCenter(IntVec3 cell)
        {
            Vector3 center = cell.ToVector3Shifted();
            center.y = exactPosition.y;
            return center;
        }

        /// <summary>
        /// Called each time Gungnir reaches a cell on its path.
        /// </summary>
        private void OnEnteredCell(IntVec3 cell)
        {
            if (IsReturning)
            {
                HitEverythingInCell(cell);
                return;
            }

            Building_Door door = cell.GetDoor(Map);
            if (door != null && !door.Open) door.TakeDamage(MakeGungnirDamage(travelAngle));
        }

        /// <summary>
        /// The return flight tears through this cell: every pawn, building, item and plant here takes Gungnir's hit, once per trip. The wielder is spared.
        /// </summary>
        private void HitEverythingInCell(IntVec3 cell)
        {
            foreach (Thing thing in cell.GetThingList(Map).ToList())
            {
                if (thing == this || thing == destinationPawn || thing.Destroyed) continue;
                if (!(thing is Pawn) && !thing.def.useHitPoints) continue;

                ThingCategory category = thing.def.category;
                if (category == ThingCategory.Filth || category == ThingCategory.Mote || category == ThingCategory.Ethereal || category == ThingCategory.Projectile) continue;

                if (!alreadyHitThings.Add(thing)) continue;
                thing.TakeDamage(MakeGungnirDamage(travelAngle));
            }
        }

        /// <summary>
        /// No route exists: fly straight at the target. If a wall is in the way, fly up to it; ResolveWallHit decides what happens there.
        /// </summary>
        private void StartStraightFlight()
        {
            List<IntVec3> lineCells = LineStrike.CellsOnLine(Position, destinationPawn.Position);
            int wallIndex = lineCells.FindIndex(cell => LineStrike.GetBlocker(cell, Map) != null);

            isFlyingStraight = true;
            chargeWallCell = wallIndex >= 0 ? lineCells[wallIndex] : IntVec3.Invalid;
            pathCells = wallIndex >= 0 ? lineCells.GetRange(0, wallIndex) : lineCells;
        }

        /// <summary>
        /// The straight flight reached its wall: hit it, then punch through if the target's side is open, or get stuck if it's another wall.
        /// </summary>
        private void ResolveWallHit()
        {
            isFlyingStraight = false;
            IntVec3 wallCell = chargeWallCell;
            chargeWallCell = IntVec3.Invalid;

            Building wall = LineStrike.GetBlocker(wallCell, Map);
            if (wall != null)
            {
                travelAngle = (wallCell - Position).AngleFlat;
                wall.TakeDamage(MakeGungnirDamage(travelAngle));
            }

            if (wall == null || wall.Destroyed)
            {
                if (wall != null) GungnirDefOf.Gungnir_Pierce.PlayOneShot(new TargetInfo(wallCell, Map));
                if (!TryRepath()) StartStraightFlight();
                return;
            }

            if (TryFindCellBehindWall(wallCell, out IntVec3 cellBehindWall))
            {
                GungnirDefOf.Gungnir_Pierce.PlayOneShot(new TargetInfo(wallCell, Map));
                MoveTo(cellBehindWall);
                if (!TryRepath()) StartStraightFlight();
                return;
            }

            GungnirDefOf.Gungnir_Impact.PlayOneShot(new TargetInfo(wallCell, Map));
            stuckWallCell = wallCell;
            modeBeforeStuck = mode;
            mode = GungnirFlightMode.Stuck;
        }

        /// <summary>
        /// Finds the open cell on the target's side of a wall, stepping from the wall toward the target along x, z, or diagonally.
        /// Returns false when every such cell is also blocked, i.e. the wall is more than one layer thick.
        /// </summary>
        private bool TryFindCellBehindWall(IntVec3 wallCell, out IntVec3 cellBehindWall)
        {
            IntVec3 towardTarget = destinationPawn.Position - wallCell;
            int stepX = Math.Sign(towardTarget.x);
            int stepZ = Math.Sign(towardTarget.z);

            List<IntVec3> candidateCells = new List<IntVec3>();
            if (stepX != 0) candidateCells.Add(wallCell + new IntVec3(stepX, 0, 0));
            if (stepZ != 0) candidateCells.Add(wallCell + new IntVec3(0, 0, stepZ));
            if (stepX != 0 && stepZ != 0) candidateCells.Add(wallCell + new IntVec3(stepX, 0, stepZ));

            cellBehindWall = IntVec3.Invalid;
            float closestDistance = float.MaxValue;
            foreach (IntVec3 candidateCell in candidateCells)
            {
                if (!candidateCell.InBounds(Map) || LineStrike.GetBlocker(candidateCell, Map) != null || !candidateCell.Walkable(Map)) continue;

                float distanceToTarget = candidateCell.DistanceToSquared(destinationPawn.Position);
                if (distanceToTarget >= closestDistance) continue;

                closestDistance = distanceToTarget;
                cellBehindWall = candidateCell;
            }
            return cellBehindWall.IsValid;
        }

        /// <summary>
        /// While stuck: once the wall is gone, carry on hunting the target.
        /// </summary>
        private void StuckTick()
        {
            if (!this.IsHashIntervalTick(StuckCheckIntervalTicks)) return;

            if (!DestinationIsValid)
            {
                if (IsReturnFlight) DropGungnirHere();
                else ReturnOrDrop();
                return;
            }

            if (LineStrike.GetBlocker(stuckWallCell, Map) != null) return;

            mode = modeBeforeStuck;
            if (!TryRepath()) StartStraightFlight();
        }

        /// <summary>
        /// True if a flying spear can pass through this cell. Terrain doesn't matter (water, mud, soil are all the same in the air),
        /// and pawns don't block. Walls, mountains and other full buildings do. Closed doors block unless doors are allowed.
        /// </summary>
        private bool CanFlyThrough(IntVec3 cell, bool doorsAllowed)
        {
            if (!cell.InBounds(Map)) return false;

            Building blocker = LineStrike.GetBlocker(cell, Map);
            if (blocker == null) return true;
            return doorsAllowed && blocker is Building_Door;
        }

        /// <summary>
        /// The throw reached its target: damage, stun, and lodge in the part that was hit. If it can't lodge, it comes home.
        /// </summary>
        private void ImpactTarget()
        {
            if (TryImpaleAndLodge(destinationPawn)) Destroy();
            else ReturnOrDrop();
        }

        /// <summary>
        /// An unworthy wielder couldn't stop Gungnir, so it hits them like it would anyone else and lodges in them.
        /// If that hit kills them or destroys the part, it drops at their feet instead of looping back into another catch.
        /// </summary>
        private void SlamIntoWielder()
        {
            if (TryImpaleAndLodge(destinationPawn)) Destroy();
            else DropGungnirHere();
        }

        /// <summary>
        /// Ends the flight with Gungnir lying on the ground where it is.
        /// </summary>
        private void DropGungnirHere()
        {
            Thing gungnir = CarriedGungnir;
            if (gungnir != null) carriedGungnir.TryDrop(gungnir, Position, Map, ThingPlaceMode.Near, out _);
            GungnirUtility.RemoveOpenHand(thrower);
            Destroy();
        }

        /// <summary>
        /// Gungnir's own hit: a stab from the speartip, credited to the thrower.
        /// </summary>
        private DamageInfo MakeGungnirDamage(float hitAngle) => GungnirUtility.MakeGungnirDamage(thrower, hitAngle);

        /// <summary>
        /// Jumps straight to a cell, used when piercing a wall or getting stuck in front of one.
        /// </summary>
        private void MoveTo(IntVec3 cell)
        {
            exactPosition = cell.ToVector3Shifted();
            exactPosition.y = AltitudeLayer.Projectile.AltitudeFor();
            Position = cell;
            pathCells.Clear();
        }

        public Pawn Wielder => thrower;

        /// <summary>
        /// Pulls a stuck Gungnir out of its wall by hand. Returns it, held by nobody, and ends the flight.
        /// </summary>
        public Thing PullOutOfWall()
        {
            Thing gungnir = CarriedGungnir;
            if (gungnir != null) carriedGungnir.Remove(gungnir);
            GungnirUtility.RemoveOpenHand(thrower);
            Destroy();
            return gungnir;
        }

        protected override void DrawAt(Vector3 drawLocation, bool flip = false)
        {
            Thing gungnir = CarriedGungnir;
            if (gungnir == null) return;

            Vector2 drawSize = gungnir.def.graphicData.drawSize;
            Quaternion rotation = Quaternion.AngleAxis(drawAngle - SpriteTipAngle, Vector3.up);
            Matrix4x4 matrix = Matrix4x4.TRS(exactPosition, rotation, new Vector3(drawSize.x, 1f, drawSize.y));
            Graphics.DrawMesh(MeshPool.plane10, matrix, gungnir.Graphic.MatSingle, 0);
        }

        public ThingOwner GetDirectlyHeldThings() => carriedGungnir;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref carriedGungnir, "carriedGungnir", this);
            Scribe_References.Look(ref thrower, "thrower");
            Scribe_References.Look(ref destinationPawn, "destinationPawn");
            Scribe_Values.Look(ref mode, "mode");
            Scribe_Values.Look(ref exactPosition, "exactPosition");
            Scribe_Values.Look(ref travelAngle, "travelAngle");
            Scribe_Values.Look(ref lastDestinationCell, "lastDestinationCell");
            Scribe_Values.Look(ref ticksUntilRepath, "ticksUntilRepath");
            Scribe_Values.Look(ref stuckWallCell, "stuckWallCell");
            Scribe_Collections.Look(ref pathCells, "pathCells", LookMode.Value);
            Scribe_Values.Look(ref isFlyingStraight, "isFlyingStraight");
            Scribe_Values.Look(ref chargeWallCell, "chargeWallCell", IntVec3.Invalid);
            Scribe_Collections.Look(ref alreadyHitThings, "alreadyHitThings", LookMode.Reference);
            Scribe_Values.Look(ref modeBeforeStuck, "modeBeforeStuck");
            Scribe_Values.Look(ref pathAllowsDoors, "pathAllowsDoors");
            Scribe_Values.Look(ref drawAngle, "drawAngle");
            Scribe_Values.Look(ref hasDrawAngle, "hasDrawAngle");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && alreadyHitThings == null) alreadyHitThings = new HashSet<Thing>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && pathCells == null) pathCells = new List<IntVec3>();
        }
    }
}
