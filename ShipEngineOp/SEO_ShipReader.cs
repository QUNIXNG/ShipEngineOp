using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;

namespace ShipEngineOptimization
{
    /// <summary>
    /// Reads the craft currently open in the editor so the stage window can be filled in
    /// automatically instead of asking the player to type every number.
    /// </summary>
    public static class SEO_ShipReader
    {
        /// <summary>Turn on to dump per-part staging details into KSP.log.</summary>
        public static bool debugLog;   // set true (or via debugger) to print reader diagnostics

        public class StageSnapshot
        {
            public int Index;
            public string EngineName = string.Empty;
            public int EngineCount;
            public float Dv;
            public float Twr;
            public float Mass;
            public float PropellantMass;
        /// <summary>Mass of everything that rides above this stage (payload for this stage).</summary>
            public float DvAsl;
            public float TwrAsl;
        }

        public static bool TryRead(List<StageSnapshot> snapshots, out float payloadMass, out float totalMass)
        {
            snapshots.Clear();
            payloadMass = 0f;
            totalMass = 0f;

            if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return false;

            List<Part> parts = EditorLogic.fetch.ship.parts;
            if (parts == null || parts.Count == 0) return false;

            // Parts with a negative inverseStage never belong to a stage; they are pure payload.
            float payloadDryMass = 0f;
            float payloadResourceMass = 0f;

            // Group by firing order: the highest inverseStage lights first.
            List<IGrouping<int, Part>> groups = parts
                .Where(p => p != null && p.inverseStage >= 0)
                .GroupBy(p => p.inverseStage)
                .OrderByDescending(g => g.Key)
                .ToList();

            float surfaceGravity = (float)FlightGlobals.getGeeForceAtPosition(Vector3d.zero).magnitude;
            float remainingMass = 0f;
            float engineDryMass = 0f;
            float propellantMass = 0f;
            float carriedFuel = 0f;
            List<StageSnapshot> ordered = new List<StageSnapshot>();

            // Walk from the last stage (inverseStage 0) upwards so "remaining mass" at each stage
            // start includes everything that has not been dropped yet.
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                IGrouping<int, Part> group = groups[i];
                float groupDry = 0f;
                float groupFuel = 0f;
                float vacuumIsp = 0f;
                float thrust = 0f;
                int engineCount = 0;
                string engineName = string.Empty;

                foreach (Part part in group)
                {
                    groupDry += part.mass;

                    foreach (PartResource resource in part.Resources)
                    {
                        if (resource.info == null) continue;
                        groupFuel += (float)resource.amount * resource.info.density;
                    }

                    List<ModuleEngines> partEngines = part.Modules.OfType<ModuleEngines>()
                        .Where(e => e != null).ToList();
                    if (partEngines.Count > 0)
                    {
                        engineCount += partEngines.Count;
                        engineDryMass += part.mass;
                        if (string.IsNullOrEmpty(engineName)) engineName = part.partInfo.title;
                        foreach (ModuleEngines engine in partEngines)
                        {
                            vacuumIsp += engine.atmosphereCurve.Evaluate(0f) * engine.maxThrust;
                            thrust += engine.maxThrust;
                            if (debugLog)
                            {
                                Debug.Log(string.Format("[ShipEngineOp] part={0} stage={1} engine={2} thrust={3:F1} isp={4:F1} enabled={5} active={6}",
                                    part.partInfo.title, part.inverseStage, engine.engineID,
                                    engine.maxThrust, engine.atmosphereCurve.Evaluate(0f),
                                    engine.isEnabled, part.isActiveAndEnabled));
                            }
                        }
                    }
                }

                // Fuel usually sits one stage below the engine that burns it, so hand tanks that have
                // no engine of their own to the next engine stage up.
                float effectiveFuel = groupFuel + carriedFuel;
                if (engineCount > 0) carriedFuel = 0f;
                else { carriedFuel = effectiveFuel; effectiveFuel = 0f; }

                remainingMass += groupDry + groupFuel;

                StageSnapshot snapshot = new StageSnapshot();
                snapshot.Mass = remainingMass;
                snapshot.PropellantMass = effectiveFuel;
                snapshot.EngineCount = engineCount;
                snapshot.EngineName = engineName;

                if (engineCount > 0 && thrust > 0f && effectiveFuel > 0f)
                {
                    vacuumIsp /= thrust;
                    float dryMass = Mathf.Max(0.01f, remainingMass - effectiveFuel);
                    snapshot.Dv = vacuumIsp * 9.80665f * Mathf.Log(remainingMass / dryMass);
                    snapshot.Twr = thrust / (remainingMass * surfaceGravity);
                }

                if (debugLog)
                {
                    Debug.Log(string.Format("[ShipEngineOp] group stage={0} parts={1} dry={2:F3} fuel={3:F3} usable={4:F3} engines={5} thrust={6:F1} dv={7:F1} twr={8:F3} mass={9:F3}",
                        group.Key, group.Count(), groupDry, groupFuel, effectiveFuel, engineCount, thrust, snapshot.Dv, snapshot.Twr, snapshot.Mass));
                }

                float aboveMass = 0f;
                foreach (Part other in parts)
                {
                    if (other == null || other.inverseStage >= group.Key) continue;
                    aboveMass += other.mass;
                    foreach (PartResource resource in other.Resources)
                    {
                        if (resource.info != null) aboveMass += (float)resource.amount * resource.info.density;
                    }
                }

                ordered.Add(snapshot);
            }

            foreach (Part part in parts)
            {
                if (part == null || part.inverseStage >= 0) continue;
                payloadDryMass += part.mass;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource.info != null) payloadResourceMass += (float)resource.amount * resource.info.density;
                }
            }

            propellantMass = 0f;
            foreach (Part part in parts)
            {
                if (part == null) continue;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource.info != null) propellantMass += (float)resource.amount * resource.info.density;
                }
            }

            ordered.Reverse();

            // Only stages that actually have engines count as stages; payload-only groups are dropped.
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].EngineCount == 0) continue;
                ordered[i].Index = snapshots.Count;
                snapshots.Add(ordered[i]);
            }

            totalMass = EditorLogic.fetch.ship.GetTotalMass();
            payloadMass = MeasurePayloadMass(EditorLogic.fetch.ship);

            // Prefer the game's own staging simulation; fall back to the mass-ratio estimate below.
            if (TryReadGameDeltaV(out List<StageSnapshot> gameStages))
            {
                // Discard the estimate above; the game's numbers replace it wholesale.
                snapshots.Clear();
                snapshots.AddRange(gameStages);
                for (int i = 0; i < snapshots.Count; i++) snapshots[i].Index = i;
                engineDryMass = 0f;
                propellantMass = 0f;
                foreach (Part part in parts)
                {
                    if (part == null) continue;
                    if (part.Modules.OfType<ModuleEngines>().Any()) engineDryMass += part.mass;
                    foreach (PartResource resource in part.Resources)
                    {
                        if (resource.info != null) propellantMass += (float)resource.amount * resource.info.density;
                    }
                }
                // payloadMass was measured above; this figure is only informational.
                if (debugLog)
                {
                    Debug.Log(string.Format("[ShipEngineOp] game deltaV used: stages={0} total={1:F3} propellant={2:F3} payload={3:F3}",
                        snapshots.Count, totalMass, propellantMass, payloadMass));
                }
                return snapshots.Count > 0;
            }

            // payloadMass was measured above (mass above the topmost engine stage).

            if (debugLog)
            {
                Debug.Log(string.Format("[ShipEngineOp] craft total={0:F3} enginesDry={1:F3} propellant={2:F3} payload={3:F3} stages={4} negStageParts={5}",
                    totalMass, engineDryMass, propellantMass, payloadMass, snapshots.Count, parts.Count(p => p != null && p.inverseStage < 0)));
            }

            return snapshots.Count > 0;
        }

        /// <summary>
        /// Asks KSP for its own per-stage delta-v (the same numbers the staging panel shows).
        /// Reflection keeps the mod compiling without a hard dependency on these internals.
        /// </summary>
        private static bool TryReadGameDeltaV(out List<StageSnapshot> stages)
        {
            stages = new List<StageSnapshot>();
            try
            {
                Assembly assembly = typeof(EditorLogic).Assembly;
                Type vesselDeltaVType = assembly.GetType("VesselDeltaV");
                if (vesselDeltaVType == null)
                {
                    if (debugLog) Debug.LogWarning("[ShipEngineOp] VesselDeltaV type not found");
                    return false;
                }

                ShipConstruct ship = EditorLogic.fetch.ship;
                currentEngineTitles = DescribeEnginesByStage(ship);

                // The editor already keeps a VesselDeltaV for the staging readout; reuse it when possible,
                // because a freshly created instance has no simulation results until it has ticked.
                object instance = FindGameVesselDeltaV(vesselDeltaVType, ship);
                bool reused = instance != null;
                if (instance == null) instance = CreateVesselDeltaV(vesselDeltaVType, ship);
                if (instance == null)
                {
                    if (debugLog) Debug.LogWarning("[ShipEngineOp] VesselDeltaV.Create failed");
                    return false;
                }

                InvokeIfPresent(instance, "CheckDirtyAndRun");

                if (debugLog)
                {
                    Debug.Log(string.Format("[ShipEngineOp] deltaV source={0} ready={1} totalVac={2:F1}",
                        reused ? "existing" : "created",
                        GetMember(instance, "IsReady"),
                        ToFloat(GetMember(instance, "TotalDeltaVVac"))));
                }

                float totalVac = ToFloat(GetMember(instance, "TotalDeltaVVac"));
                float carryLimit = totalVac > 1f ? totalVac * 1.05f : float.MaxValue;

                object infoList = GetMember(instance, "WorkingStageInfo");
                IList list = infoList as IList;
                if (list == null)
                {
                    if (debugLog) Debug.LogWarning("[ShipEngineOp] WorkingStageInfo missing");
                    return false;
                }

                List<StageSnapshot> found = new List<StageSnapshot>();
                int totalEntries = 0, skippedNoEngine = 0, skippedZeroDv = 0;
                foreach (object info in list)
                {
                    if (info == null) continue;
                    int stageIndex = Convert.ToInt32(GetMember(info, "stage") ?? 0);
                    float dv = ToFloat(GetMember(info, "deltaVinVac"));
                    float dvAsl = ToFloat(GetMember(info, "deltaVatASL"));
                    float twr = ToFloat(GetMember(info, "TWRActual"));
                    float startMass = ToFloat(GetMember(info, "startMass"));
                    float fuelMass = ToFloat(GetMember(info, "fuelMass"));
                    object engines = GetMember(info, "enginesInStage");
                    int engineCount = engines is ICollection collection ? collection.Count : 0;

                    if (debugLog)
                    {
                        Debug.Log(string.Format("[ShipEngineOp] dvStage={0} dvVac={1:F1} dvASL={2:F1} twr={3:F3} startMass={4:F3} fuel={5:F3} engines={6}",
                            stageIndex, dv, dvAsl, twr, startMass, fuelMass, engineCount));
                    }

                    totalEntries++;
                    if (engineCount <= 0) { skippedNoEngine++; continue; }
                    if (solidBoosterStages.Contains(stageIndex))
                    {
                        skippedNoEngine++;
                        if (debugLog) Debug.Log(string.Format("[ShipEngineOp] skipped solid booster stage={0}", stageIndex));
                        continue;
                    }
                    // A cheap engine on big tanks can be reported with delta-v far above the vessel
                    // total; that means downstream propellant was counted twice for this stage.
                    if (dv > carryLimit)
                    {
                        skippedZeroDv++;
                        if (debugLog)
                        {
                            Debug.Log(string.Format("[ShipEngineOp] dropped stage={0} dvVac={1:F1} above vessel total {2:F1}",
                                stageIndex, dv, totalVac));
                        }
                        continue;
                    }
                    // The game keeps stage entries that burn nothing (already separated, no propellant left).
                    if (dv <= 0.5f && dvAsl <= 0.5f) { skippedZeroDv++; continue; }

                    StageSnapshot snapshot = new StageSnapshot();
                    snapshot.Index = found.Count;
                    snapshot.Dv = dv > 0f ? dv : dvAsl;
                    snapshot.DvAsl = dvAsl;
                    snapshot.Twr = twr;
                    snapshot.TwrAsl = ToFloat(GetMember(info, "TWRASL"));
                    snapshot.Mass = startMass;
                    snapshot.PropellantMass = fuelMass;
                    snapshot.EngineCount = engineCount;
                    float stagePayload;
                    string stageEngine;
                    if (currentEngineTitles.TryGetValue(stageIndex, out stageEngine)) snapshot.EngineName = stageEngine;
                    found.Add(snapshot);
                }

                if (debugLog)
                {
                    Debug.Log(string.Format("[ShipEngineOp] stages kept={0} total={1} noEngine={2} zeroDv={3}",
                        found.Count, totalEntries, skippedNoEngine, skippedZeroDv));
                }

                if (found.Count == 0)
                {
                    if (debugLog) Debug.LogWarning("[ShipEngineOp] no simulated stages yet");
                    return false;
                }
                stages = found;
                return true;
            }
            catch (Exception e)
            {
                if (debugLog) Debug.LogWarning("[ShipEngineOp] game deltaV failed: " + e);
                return false;
            }
        }

        private static Dictionary<int, string> currentEngineTitles = new Dictionary<int, string>();
        private static HashSet<int> solidBoosterStages = new HashSet<int>();

        /// <summary>
        /// Payload for a stage = everything that rides above it. Measured as a mass difference
        /// against the whole craft, which avoids depending on per-part propellant bookkeeping.
        /// In KSP a higher inverseStage fires earlier, i.e. sits lower in the stack.
        /// </summary>
        /// <summary>
        /// The payload this mod works with: everything that rides above the topmost engine stage.
        /// Engines and propellant below that point are what the design has to lift, not cargo.
        /// </summary>
        public static float MeasurePayloadMass(ShipConstruct ship)
        {
            if (ship == null || ship.parts == null) return 0f;

            int topStage = int.MaxValue;
            foreach (Part part in ship.parts)
            {
                if (part == null) continue;
                if (part.Modules.OfType<ModuleEngines>().Any() && part.inverseStage < topStage)
                    topStage = part.inverseStage;
            }
            if (topStage == int.MaxValue) return ship.GetTotalMass();

            // KSP usually puts a stage's tanks one stage below its engine, so the payload line has to
            // sit above the fuel of the topmost engine, not at the engine itself.
            // Only look one stage down, and only at real stage propellant. MonoPropellant lives in
            // the capsule and must not move the line below the payload.
            int fuelStage = topStage;
            foreach (Part part in ship.parts)
            {
                if (part == null || part.inverseStage != topStage - 1) continue;
                foreach (PartResource resource in part.Resources)
                {
                    string name = resource.resourceName;
                    if (name == "LiquidFuel" || name == "Oxidizer" ||
                        name == "SolidFuel" || name.StartsWith("Lqd"))
                    {
                        fuelStage = topStage - 1;
                        break;
                    }
                }
                if (fuelStage != topStage) break;
            }

            float payload = 0f;
            foreach (Part part in ship.parts)
            {
                if (part == null || part.inverseStage >= fuelStage) continue;
                payload += part.mass;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource.info != null) payload += (float)resource.amount * resource.info.density;
                }
            }
            if (debugLog)
            {
                Debug.Log(string.Format("[ShipEngineOp] payload measure topStage={0} fuelStage={1} craft={2:F3} counted={3:F3}",
                    topStage, fuelStage, ship.GetTotalMass(), payload));
                foreach (Part part in ship.parts)
                {
                    if (part == null) continue;
                    bool isPayload = part.inverseStage < fuelStage;
                    if (!isPayload) continue;
                    Debug.Log(string.Format("[ShipEngineOp]   payload part={0} stage={1} mass={2:F4}",
                        part.partInfo != null ? part.partInfo.title : part.name, part.inverseStage, part.mass));
                }
            }
            return Mathf.Max(0.01f, payload);
        }

        private static Dictionary<int, float> DescribePayloadByStage(ShipConstruct ship)
        {
            Dictionary<int, float> payloads = new Dictionary<int, float>();
            if (ship == null || ship.parts == null) return payloads;

            float total = 0f;
            foreach (Part part in ship.parts)
            {
                if (part == null) continue;
                total += part.mass;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource.info != null) total += (float)resource.amount * resource.info.density;
                }
            }

            List<int> stages = new List<int>();
            foreach (Part part in ship.parts)
            {
                if (part == null) continue;
                if (part.Modules.OfType<ModuleEngines>().Any() && !stages.Contains(part.inverseStage))
                    stages.Add(part.inverseStage);
            }

            foreach (int stage in stages)
            {
                float belowOrEqual = 0f;
                foreach (Part part in ship.parts)
                {
                    if (part == null || part.inverseStage < stage) continue;
                    belowOrEqual += part.mass;
                    foreach (PartResource resource in part.Resources)
                    {
                        if (resource.info != null) belowOrEqual += (float)resource.amount * resource.info.density;
                    }
                }
                payloads[stage] = Mathf.Max(0f, total - belowOrEqual);
            }
            return payloads;
        }

        private static Dictionary<int, string> DescribeEnginesByStage(ShipConstruct ship)
        {
            Dictionary<int, string> titles = new Dictionary<int, string>();
            solidBoosterStages.Clear();
            if (ship == null || ship.parts == null) return titles;

            Dictionary<int, bool> anyLiquidByStage = new Dictionary<int, bool>();
            foreach (Part part in ship.parts)
            {
                if (part == null || part.partInfo == null) continue;
                List<ModuleEngines> engines = part.Modules.OfType<ModuleEngines>().ToList();
                if (engines.Count == 0) continue;

                bool liquid = false;
                foreach (ModuleEngines engine in engines)
                {
                    if (engine.propellants == null) continue;
                    foreach (Propellant propellant in engine.propellants)
                    {
                        if (propellant == null || string.IsNullOrEmpty(propellant.name)) continue;
                        if (propellant.name == "SolidFuel") continue;
                        if (propellant.name == "ElectricCharge") continue;
                        liquid = true;
                        break;
                    }
                    if (liquid) break;
                }

                // Only liquid engines are worth naming here; a solid booster sharing the stage
                // must not be reported as "the engine this stage uses".
                if (liquid && !titles.ContainsKey(part.inverseStage)) titles[part.inverseStage] = part.partInfo.title;

                bool seen;
                anyLiquidByStage.TryGetValue(part.inverseStage, out seen);
                anyLiquidByStage[part.inverseStage] = seen || liquid;
            }

            // A stage that only ever burns solid fuel is a booster: it is jettisoned as a whole,
            // so it must not be offered as an optimisable stage.
            foreach (KeyValuePair<int, bool> entry in anyLiquidByStage)
            {
                if (!entry.Value) solidBoosterStages.Add(entry.Key);
            }
            return titles;
        }

        /// <summary>Engine title that the craft currently has on the given stage.</summary>
        public static string CurrentEngineName(int stageIndex)
        {
            string title;
            return currentEngineTitles.TryGetValue(stageIndex, out title) ? title : string.Empty;
        }

        private static object FindGameVesselDeltaV(Type type, ShipConstruct ship)
        {
            try
            {
                UnityEngine.Object[] existing = Resources.FindObjectsOfTypeAll(type);
                if (existing == null) return null;
                object firstWithShip = null;
                foreach (UnityEngine.Object candidate in existing)
                {
                    if (candidate == null) continue;
                    object candidateShip = GetMember(candidate, "Ship");
                    if (firstWithShip == null && candidateShip != null) firstWithShip = candidate;
                    if (ReferenceEquals(candidateShip, ship)) return candidate;
                }
                return firstWithShip;
            }
            catch (Exception e)
            {
                if (debugLog) Debug.LogWarning("[ShipEngineOp] FindGameVesselDeltaV failed: " + e.Message);
                return null;
            }
        }

        private static object CreateVesselDeltaV(Type type, ShipConstruct ship)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "Create") continue;
                ParameterInfo[] parameters = method.GetParameters();
                object[] arguments = new object[parameters.Length];
                bool usable = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type p = parameters[i].ParameterType;
                    if (typeof(ShipConstruct).IsAssignableFrom(p)) arguments[i] = ship;
                    else if (typeof(Vessel).IsAssignableFrom(p)) { usable = false; break; }
                    else if (p.IsEnum) arguments[i] = Enum.ToObject(p, 0);
                    else if (p == typeof(bool)) arguments[i] = false;
                    else if (p.IsValueType) arguments[i] = Activator.CreateInstance(p);
                    else arguments[i] = null;
                }
                if (!usable) continue;
                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result != null) return result;
                }
                catch (Exception e)
                {
                    if (debugLog) Debug.LogWarning("[ShipEngineOp] Create overload failed: " + e.Message);
                }
            }
            return null;
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(target, null);
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);
            return null;
        }

        private static void InvokeIfPresent(object target, string methodName)
        {
            if (target == null) return;
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) return;
            try { method.Invoke(target, null); } catch { }
        }

        private static float ToFloat(object value)
        {
            if (value == null) return 0f;
            try { return Convert.ToSingle(value); } catch { return 0f; }
        }

        /// <summary>Rebuilds the stage list from the craft so the optimizer starts from real numbers.</summary>
        public static bool ApplyToStageList(List<StageSnapshot> snapshots, float payloadMass)
        {
            if (snapshots.Count == 0) return false;

            string planetName = Planetarium.fetch.Home.bodyName;
            if (SEO_StageWindow.stageList.Count > 0 && !string.IsNullOrEmpty(SEO_StageWindow.stageList[0].planetNameStr))
            {
                planetName = SEO_StageWindow.stageList[0].planetNameStr;
            }
            float altitude = SEO_StageWindow.stageList.Count > 0
                ? SEO_StageWindow.stageList[0].altitude
                : (float)Planetarium.fetch.Home.atmosphereDepth;

            // Single source of truth for the top stage payload: measured from the craft itself.
            float measuredPayload = EditorLogic.fetch != null && EditorLogic.fetch.ship != null
                ? MeasurePayloadMass(EditorLogic.fetch.ship)
                : payloadMass;
            SEO_Main.payload = measuredPayload;

            List<StageConfig> rebuilt = new List<StageConfig>();
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (snapshots[i].Dv < 1f) continue;
                StageConfig stage = new StageConfig();
                stage.SetPlanet(planetName);
                stage.altitude = altitude;
                stage.constrainOp = 0;
                stage.targetDv = Mathf.Max(1f, Mathf.Round(snapshots[i].Dv));
                stage.targetTwr = Mathf.Max(0.01f, Mathf.Round(snapshots[i].Twr * 100f) / 100f);
                stage.includeInOp = true;
                stage.manualMode = false;
                stage.keepEngine = false;
                stage.additionalMass = 0f;
                // Each stage only lifts what is above it; the reader measures that per stage.
                // Top stage carries the measured payload; lower stages get theirs from the chain.
                stage.payloadMass = i == snapshots.Count - 1 ? measuredPayload : 0f;
                if (stage.payloadMass <= 0f && i == 0) stage.payloadMass = measuredPayload;
                stage.ownPayloadMass = stage.payloadMass;
                rebuilt.Add(stage);
            }

            SEO_StageWindow.stageList = rebuilt;
            return true;
        }
    }
}