using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShipEngineOptimization
{
    /// <summary>
    /// Caches for values that are expensive to discover repeatedly during an optimization run.
    /// All hot-path keys are numeric so that repeated lookups allocate nothing.
    /// </summary>
    public static class SEO_OptimizationCache
    {
        public struct EnginePerformance
        {
            public float Isp;
            public float Thrust;
        }

        public struct Selection
        {
            public string Name;
            public float Weight;
            public float Wdr;
            public float WeightTotal;
            public float Twr;
            public float Dv;
            public float FuelMass;
            public int QtyEng;
        }

        private struct SelectionKey : IEquatable<SelectionKey>
        {
            public int Version;
            public int Atm;
            public int Payload;
            public int Dv;
            public int Twr;
            public int Constrain;
            public int FilterQty;
            public bool FilterOn;

            public bool Equals(SelectionKey other)
            {
                return Version == other.Version
                    && Atm == other.Atm
                    && Payload == other.Payload
                    && Dv == other.Dv
                    && Twr == other.Twr
                    && Constrain == other.Constrain
                    && FilterQty == other.FilterQty
                    && FilterOn == other.FilterOn;
            }

            public override bool Equals(object obj)
            {
                return obj is SelectionKey && Equals((SelectionKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Version;
                    hash = (hash * 397) ^ Atm;
                    hash = (hash * 397) ^ Payload;
                    hash = (hash * 397) ^ Dv;
                    hash = (hash * 397) ^ Twr;
                    hash = (hash * 397) ^ Constrain;
                    hash = (hash * 397) ^ FilterQty;
                    hash = (hash * 397) ^ (FilterOn ? 1 : 0);
                    return hash;
                }
            }
        }

        /// <summary>Quantization used by the selection cache. Coarse enough to hit often, fine enough to not change results noticeably.</summary>
        private const float PayloadQuantization = 0.01f;
        private const float AtmQuantization = 1000f;
        private const float TwrQuantization = 100f;

        private static readonly Dictionary<string, AvailablePart> parts =
            new Dictionary<string, AvailablePart>(StringComparer.Ordinal);

        private static readonly Dictionary<string, int> planetIds =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static readonly Dictionary<long, float> atmValues =
            new Dictionary<long, float>();

        private static readonly Dictionary<long, EnginePerformance> enginePerformance =
            new Dictionary<long, EnginePerformance>();

        private static readonly Dictionary<string, int> engineIds =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static readonly Dictionary<SelectionKey, Selection> selections =
            new Dictionary<SelectionKey, Selection>();

        private static int nextPlanetId = 1;
        private static int nextEngineId = 1;

        /// <summary>Bumped whenever inputs that affect engine ranking change (fuel filter, blacklist, extra mass, planet).</summary>
        public static int Version { get; private set; }

        public static int SelectionCount { get { return selections.Count; } }

        public static void InvalidateSelections()
        {
            Version++;
            selections.Clear();
        }

        public static void ClearEngineCache()
        {            enginePerformance.Clear();
            atmValues.Clear();
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

        public static void ClearParts()
        {
            parts.Clear();
        }

        public static int GetEngineId(string engineName)
        {
            if (string.IsNullOrEmpty(engineName)) return 0;

            int id;
            if (engineIds.TryGetValue(engineName, out id)) return id;

            id = nextEngineId++;
            engineIds[engineName] = id;
            return id;
        }

        public static int QuantizeAtmosphere(float atm)
        {
            return (int)(atm * AtmQuantization + 0.5f);
        }

        public static float GetAtmosphere(string planetName, float altitude)
        {
            if (string.IsNullOrEmpty(planetName)) return 1f;

            int planetId;
            if (!planetIds.TryGetValue(planetName, out planetId))
            {
                planetId = nextPlanetId++;
                planetIds[planetName] = planetId;
            }

            int altitudeBucket = (int)(altitude * 10f + 0.5f);
            long key = ((long)planetId << 40) | (uint)altitudeBucket;

            float atm;
            if (atmValues.TryGetValue(key, out atm)) return atm;

            CelestialBody planet = null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
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

        public static bool TryGetEngine(int engineId, int atmBucket, int modeIdx, out EnginePerformance performance)
        {
            return enginePerformance.TryGetValue(EngineKey(engineId, atmBucket, modeIdx), out performance);
        }

        public static void StoreEngine(int engineId, int atmBucket, int modeIdx, float isp, float thrust)
        {
            EnginePerformance performance;
            performance.Isp = isp;
            performance.Thrust = thrust;
            enginePerformance[EngineKey(engineId, atmBucket, modeIdx)] = performance;
        }

        public static bool TryGetSelection(
            float atm, float payload, float dv, float twr, int constrainOp, bool filterOn, int filterQty,
            out Selection selection)
        {
            SelectionKey key = MakeSelectionKeyVersioned(atm, payload, dv, twr, constrainOp, filterOn, filterQty);
            return selections.TryGetValue(key, out selection);
        }

        public static void StoreSelection(
            float atm, float payload, float dv, float twr, int constrainOp, bool filterOn, int filterQty,
            Selection selection)
        {
            SelectionKey key = MakeSelectionKeyVersioned(atm, payload, dv, twr, constrainOp, filterOn, filterQty);
            selections[key] = selection;
        }

        private static SelectionKey MakeSelectionKeyVersioned(
            float atm, float payload, float dv, float twr, int constrainOp, bool filterOn, int filterQty)
        {
            SelectionKey key;
            key.Version = Version;
            key.Atm = QuantizeAtmosphere(atm);
            key.Payload = (int)(payload / PayloadQuantization + 0.5f);
            key.Dv = (int)(dv + 0.5f);
            key.Twr = (int)(twr * TwrQuantization + 0.5f);
            key.Constrain = constrainOp;
            key.FilterOn = filterOn;
            key.FilterQty = filterOn ? filterQty : 0;
            return key;
        }

        private static long EngineKey(int engineId, int atmBucket, int modeIdx)
        {
            return ((long)engineId << 32) | ((uint)atmBucket << 4) | (uint)(modeIdx & 0xF);
        }
    }
}