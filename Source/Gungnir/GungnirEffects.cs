using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Gungnir
{
    /// <summary>
    /// Gungnir's golden streaks: a trail behind it while it flies, and a flash with a long streak wherever it hits something.
    /// </summary>
    public static class GungnirEffects
    {
        private const float TrailStreakLength = 1.5f;
        private const float TrailStreakWidth = 0.35f;
        private const float ImpactStreakLength = 6f;
        private const float ImpactStreakWidth = 0.8f;
        private const float ImpactBurstSize = 2.5f;

        /// <summary>
        /// One streak ending at the head position and stretching back along the direction of travel.
        /// The texture's head points east (90°), so the fleck turns by the travel angle minus 90.
        /// </summary>
        public static void ThrowStreak(Vector3 headPosition, float travelAngle, Map map, float length, float width)
        {
            if (map == null || !headPosition.ShouldSpawnMotesAt(map)) return;

            Vector3 streakCenter = headPosition - Quaternion.AngleAxis(travelAngle, Vector3.up) * Vector3.forward * (length / 2f);
            FleckCreationData streakData = FleckMaker.GetDataStatic(streakCenter, map, GungnirDefOf.Gungnir_FleckStreak);
            streakData.rotation = travelAngle - 90f;
            streakData.exactScale = new Vector3(length, 1f, width);
            map.flecks.CreateFleck(streakData);
        }

        /// <summary>
        /// The short streak dropped behind Gungnir every tick in flight; they overlap into one continuous trail.
        /// </summary>
        public static void ThrowTrail(Vector3 headPosition, float travelAngle, Map map) => ThrowStreak(headPosition, travelAngle, map, TrailStreakLength, TrailStreakWidth);

        /// <summary>
        /// A burst of light plus a long streak along the flight line. Scale below 1 for lighter hits, like passing through a door.
        /// </summary>
        public static void ThrowImpact(Vector3 position, float travelAngle, Map map, float scale = 1f)
        {
            if (map == null || !position.ShouldSpawnMotesAt(map)) return;

            FleckCreationData burstData = FleckMaker.GetDataStatic(position, map, GungnirDefOf.Gungnir_FleckBurst, ImpactBurstSize * scale);
            burstData.rotation = Rand.Range(-15f, 15f);
            map.flecks.CreateFleck(burstData);

            ThrowStreak(position, travelAngle, map, ImpactStreakLength * scale, ImpactStreakWidth * scale);
        }
    }
}
