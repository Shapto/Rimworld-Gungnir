using RimWorld;
using SingularityFramework.Geometry;
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
        private const float CellsPerTick = 1.5f;
        private const int RepathIntervalTicks = 10;
        private const int StuckCheckIntervalTicks = 15;
        private const int MaximumPiercesPerAttempt = 10;
        private const float ThrowDamage = 48f;
        private const float ThrowArmorPenetration = 1.6f;
        private const int ThrowStunTicks = 120;

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

        public GungnirFlight()
        {
            carriedGungnir = new ThingOwner<Thing>(this, true);
        }

        public Thing CarriedGungnir => carriedGungnir.Count > 0 ? carriedGungnir[0] : null;

        public override Vector3 DrawPos => exactPosition;

        /// <summary>
        /// True while the destination pawn can still be flown to: alive, spawned and on this map.
        /// </summary>
        private bool DestinationIsValid => destinationPawn != null && !destinationPawn.Dead && destinationPawn.Spawned && destinationPawn.Map == Map;

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

            if (!flight.TryRepath()) flight.PierceOrGetStuck();
            return flight;
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
                DropGungnirHere();
                return;
            }

            ticksUntilRepath--;
            if (ticksUntilRepath <= 0 || destinationPawn.Position != lastDestinationCell)
            {
                if (!TryRepath())
                {
                    PierceOrGetStuck();
                    return;
                }
            }

            if (MoveAlongPath()) ImpactTarget();
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
            bool isStartCell = true;
            while (path.NodesLeftCount > 0)
            {
                IntVec3 pathCell = path.ConsumeNextNode();
                if (isStartCell)
                {
                    isStartCell = false;
                    continue;
                }
                pathCells.Add(pathCell);
            }

            path.ReleaseToPool();
            return true;
        }

        /// <summary>
        /// Moves along the path this tick. Returns true when the destination's cell has been reached.
        /// </summary>
        private bool MoveAlongPath()
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

            return pathCells.Count == 0 && Position.InHorDistOf(destinationPawn.Position, 1.5f);
        }

        /// <summary>
        /// No route exists: fly straight at the target and hit the first wall in the way.
        /// If the cell behind it is open, punch through and keep hunting; if it's another wall, get stuck in this one.
        /// </summary>
        private void PierceOrGetStuck()
        {
            for (int pierceCount = 0; pierceCount < MaximumPiercesPerAttempt; pierceCount++)
            {
                List<IntVec3> lineCells = LineStrike.CellsOnLine(Position, destinationPawn.Position);
                int wallIndex = lineCells.FindIndex(cell => LineStrike.GetBlocker(cell, Map) != null);
                if (wallIndex < 0)
                {
                    ImpactTarget();
                    return;
                }

                IntVec3 wallCell = lineCells[wallIndex];
                travelAngle = (wallCell - Position).AngleFlat;
                LineStrike.GetBlocker(wallCell, Map).TakeDamage(MakeGungnirDamage(travelAngle));

                bool hasCellBehindWall = wallIndex + 1 < lineCells.Count;
                IntVec3 cellBehindWall = hasCellBehindWall ? lineCells[wallIndex + 1] : IntVec3.Invalid;
                bool wallWasDestroyed = LineStrike.GetBlocker(wallCell, Map) == null;
                bool cellBehindIsOpen = hasCellBehindWall && LineStrike.GetBlocker(cellBehindWall, Map) == null && cellBehindWall.Walkable(Map);

                if (!wallWasDestroyed && !cellBehindIsOpen)
                {
                    stuckWallCell = wallCell;
                    MoveTo(wallIndex > 0 ? lineCells[wallIndex - 1] : Position);
                    mode = GungnirFlightMode.Stuck;
                    return;
                }

                MoveTo(cellBehindIsOpen ? cellBehindWall : wallCell);
                if (TryRepath()) return;
            }

            DropGungnirHere();
        }

        /// <summary>
        /// While stuck: once the wall is gone, carry on hunting the target.
        /// </summary>
        private void StuckTick()
        {
            if (!this.IsHashIntervalTick(StuckCheckIntervalTicks)) return;

            if (!DestinationIsValid)
            {
                DropGungnirHere();
                return;
            }

            if (LineStrike.GetBlocker(stuckWallCell, Map) != null) return;

            mode = GungnirFlightMode.Throw;
            if (!TryRepath()) PierceOrGetStuck();
        }

        /// <summary>
        /// The throw reached its target: damage, stun, and lodge in the part that was hit.
        /// </summary>
        private void ImpactTarget()
        {
            DamageWorker.DamageResult damageResult = destinationPawn.TakeDamage(MakeGungnirDamage(travelAngle));
            BodyPartRecord hitPart = damageResult.LastHitPart;

            if (destinationPawn.Dead || hitPart == null || destinationPawn.health.hediffSet.PartIsMissing(hitPart))
            {
                DropGungnirHere();
                return;
            }

            destinationPawn.stances?.stunner.StunFor(ThrowStunTicks, thrower);

            Hediff_GungnirDroning droning = (Hediff_GungnirDroning)HediffMaker.MakeHediff(GungnirDefOf.Gungnir_Droning, destinationPawn, hitPart);
            destinationPawn.health.AddHediff(droning, hitPart);
            droning.LodgeGungnir(CarriedGungnir, thrower);
            droning.Severity = 0.01f;

            Destroy();
        }

        /// <summary>
        /// Ends the flight with Gungnir lying on the ground where it is. Temporary stand-in for Return mode.
        /// </summary>
        private void DropGungnirHere()
        {
            Thing gungnir = CarriedGungnir;
            if (gungnir != null) carriedGungnir.TryDrop(gungnir, Position, Map, ThingPlaceMode.Near, out _);
            Destroy();
        }

        /// <summary>
        /// Gungnir's own hit: a stab from the speartip, credited to the thrower.
        /// </summary>
        private DamageInfo MakeGungnirDamage(float hitAngle)
        {
            return new DamageInfo(DamageDefOf.Stab, ThrowDamage, ThrowArmorPenetration, hitAngle, thrower, null, GungnirDefOf.Gungnir);
        }

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
            if (Scribe.mode == LoadSaveMode.PostLoadInit && pathCells == null) pathCells = new List<IntVec3>();
        }
    }
}
