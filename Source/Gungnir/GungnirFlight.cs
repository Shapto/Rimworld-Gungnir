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
using Verse.AI;

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
    /// Throw mode hits only its target; Return mode (step 4) comes home through everything.
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

        /// <summary>
        /// True while flying a straight line because no route exists, either at a wall to hit or straight at the target.
        /// </summary>
        private bool isFlyingStraight;

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

        public override Vector3 DrawPos => exactPosition;

        /// <summary>
        /// True while the destination pawn can still be flown to: alive, spawned and on this map.
        /// </summary>
        private bool DestinationIsValid => destinationPawn != null && !destinationPawn.Dead && destinationPawn.Spawned && destinationPawn.Map == Map && !(IsReturning && destinationPawn.Downed);

        /// <summary>
        /// Things the return flight has already hit, so each takes Gungnir's hit only once per trip.
        /// </summary>
        private HashSet<Thing> alreadyHitThings = new HashSet<Thing>();

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

            bool wielderCanCatch = wielder != null && !wielder.Dead && !wielder.Downed && wielder.Spawned && wielder.Map == map;
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
            bool throwerCanCatch = thrower != null && !thrower.Dead && !thrower.Downed && thrower.Spawned && thrower.Map == Map;
            if (throwerCanCatch) BeginReturn(thrower);
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
        /// Recomputes the path to the destination. Throw: first with closed doors as walls, then with doors passable.
        /// Returns false if there's no route at all.
        /// </summary>
        private bool TryRepath()
        {
            lastDestinationCell = destinationPawn.Position;
            ticksUntilRepath = RepathIntervalTicks;

            if (mode == GungnirFlightMode.Return) return TryFindPath(TraverseMode.PassDoors);
            return TryFindPath(TraverseMode.NoPassClosedDoors) || TryFindPath(TraverseMode.PassDoors);
        }

        /// <summary>
        /// Asks the pathfinder for one route and copies its cells into pathCells. Returns false if none was found.
        /// </summary>
        private bool TryFindPath(TraverseMode traverseMode)
        {
            PawnPath path = Map.pathFinder.FindPathNow(Position, destinationPawn, TraverseParms.For(traverseMode), null, PathEndMode.OnCell);
            if (!path.Found)
            {
                path.ReleaseToPool();
                return false;
            }

            pathCells.Clear();
            for (int nodesAhead = 1; nodesAhead < path.NodesLeftCount; nodesAhead++)
            {
                pathCells.Add(path.Peek(nodesAhead));
            }

            path.ReleaseToPool();
            return true;
        }

        /// <summary>
        /// Moves along the path this tick. Returns true when the destination's cell has been reached.
        /// </summary>
        private void MoveAlongPath()
        {
            float movementLeft = CellsPerTick;
            while (movementLeft > 0f && pathCells.Count > 0)
            {
                Vector3 nextCellCenter = pathCells[0].ToVector3Shifted();
                nextCellCenter.y = exactPosition.y;

                Vector3 towardNextCell = nextCellCenter - exactPosition;
                float distanceToNextCell = towardNextCell.magnitude;
                if (distanceToNextCell > 0.001f) travelAngle = towardNextCell.AngleFlat();

                if (distanceToNextCell <= movementLeft)
                {
                    exactPosition = nextCellCenter;
                    movementLeft -= distanceToNextCell;
                    OnEnteredCell(pathCells[0]);
                    if (Destroyed) return;
                    pathCells.RemoveAt(0);
                }
                else
                {
                    exactPosition += towardNextCell / distanceToNextCell * movementLeft;
                    movementLeft = 0f;
                }
            }

            IntVec3 currentCell = exactPosition.ToIntVec3();
            if (currentCell != Position) Position = currentCell;
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
                if (!TryRepath()) StartStraightFlight();
                return;
            }

            if (TryFindCellBehindWall(wallCell, out IntVec3 cellBehindWall))
            {
                MoveTo(cellBehindWall);
                if (!TryRepath()) StartStraightFlight();
                return;
            }

            stuckWallCell = wallCell;
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
                if (IsReturning) DropGungnirHere();
                else ReturnOrDrop();
                return;
            }

            if (LineStrike.GetBlocker(stuckWallCell, Map) != null) return;

            mode = GungnirFlightMode.Throw;
            if (!TryRepath()) StartStraightFlight();
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
        /// Ends the flight with Gungnir lying on the ground where it is. Temporary stand-in for Return mode.
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

        protected override void DrawAt(Vector3 drawLocation, bool flip = false)
        {
            Thing gungnir = CarriedGungnir;
            if (gungnir == null) return;

            Vector2 drawSize = gungnir.def.graphicData.drawSize;
            Quaternion rotation = Quaternion.AngleAxis(travelAngle - SpriteTipAngle, Vector3.up);
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
            if (Scribe.mode == LoadSaveMode.PostLoadInit && alreadyHitThings == null) alreadyHitThings = new HashSet<Thing>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && pathCells == null) pathCells = new List<IntVec3>();
        }
    }
}
