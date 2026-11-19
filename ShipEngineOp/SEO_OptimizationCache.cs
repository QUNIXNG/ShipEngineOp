using System.Collections.Generic;
using UnityEngine;

namespace ShipEngineOptimization
{
    /// <summary>
    /// Per-optimization caches for values that are expensive to discover repeatedly.
    /// The engine cache is keyed by atmosphere, so changing planet or altitude is safe.
    /// </summary>
    public static class SEO_OptimizationCache
    {
        private static readonly Dictionary<string, AvailablePart> parts =
            new Dictionary<string, AvailablePart>();
        private static readonly Dictionary<string, float> atmValues =
            new Dictionary<string, float>();
        private static readonly Dictionary<string, EnginePerformance> enginePerformance =
            new Dictionary<string, EnginePerformance>();

        public struct EnginePerformance
        {
            public float Isp;
            public float Thrust;
        }

        public static AvailablePart GetPart(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return null;

            AvailablePart part;
            if (parts.TryGetValue(internalName, out part)) return part;

            foreach (AvailablePart candidate in PartLoader.LoadedPartsList)
            {
                if (candidate != null && candidate.name == internalName)
                {
                    parts[internalName] = candidate;
                    return candidate;
                }
            }

            parts[internalName] = null;
            return null;
        }

        public static float GetAtmosphere(string planetName, float altitude)
        {
            if (string.IsNullOrEmpty(planetName)) return 1f;

            string key = planetName + '|' + altitude.ToString("R");
            float atm;
            if (atmValues.TryGetValue(key, out atm)) return atm;

            CelestialBody planet = null;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body != null && body.bodyName == planetName)
                {
                    planet = body;
                    break;
                }
            }

            if (planet == null || SEO_Main.standardAtm == 0f)
            {
                atmValues[key] = 0f;
                return 0f;
            }

            atm = (float)FlightGlobals.getStaticPressure(altitude, planet) / SEO_Main.standardAtm;
            atmValues[key] = atm;
            return atm;
        }

        public static bool TryGetEngine(string engineName, string internalName, float atm, int modeIdx,
            out EnginePerformance performance)
        {
            string key = EngineKey(engineName, internalName, atm, modeIdx);
            return enginePerformance.TryGetValue(key, out performance);
        }

        public static void StoreEngine(string engineName, string internalName, float atm, int modeIdx,
            float isp, float thrust)
        {
            enginePerformance[EngineKey(engineName, internalName, atm, modeIdx)] =
                new EnginePerformance { Isp = isp, Thrust = thrust };
        }

        public static void ClearParts()
        {
            parts.Clear();
        }

        public static void ClearEngineCache()
        {
            enginePerformance.Clear();
            atmValues.Clear();
        }

        private static string EngineKey(string engineName, string internalName, float atm, int modeIdx)
        {
            return engineName + '|' + internalName + '|' + atm.ToString("R") + '|' + modeIdx;
        }
    }
}
