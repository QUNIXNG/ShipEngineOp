using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KSP.Localization;
using UnityEngine;

namespace ShipEngineOptimization
{
    public class SEO_StageWindow
    {
        public static List<StageConfig> stageList = new List<StageConfig>();
        public static Vector2 scrollPosition_fuelGlobal = Vector2.zero;
        public static Vector2 scrollPosition_eng = Vector2.zero;
        public static Vector2 scrollPosition_stages = Vector2.zero;

        public static HashSet<string> selectedFuelGlobal = new HashSet<string>();
        private static bool fuelDefaultsApplied;
        public static HashSet<string> selectedFuelStage = new HashSet<string>();

        //public static int selectedStageIndex;
        public static StageConfig selectedStageForEngine;

        // Long-running optimizer state: the UI shows progress instead of freezing mid-click.
        public static bool operationRunning;
        private static Rect operationWindowRect;
        public static float optimizationProgress;
        public static bool optimizationCancelled;
        public static string operationLabel = string.Empty;
        public static StageConfig selectedStageForPlanet;

        // Craft snapshot taken by "read current vessel", used for the before/after table.
        public static List<SEO_ShipReader.StageSnapshot> currentSnapshots = new List<SEO_ShipReader.StageSnapshot>();
        public static float currentTotalMass;
        public static bool autoOptimize;
        private static string autoBaseline = string.Empty;
        private static string autoPendingSignature = string.Empty;
        /// <summary>Total delta-v the optimizer has to distribute across the selected stages.</summary>
        public static float targetTotalDv;
        /// <summary>Keep each stage's own TWR target instead of copying the first stage's.</summary>
        public static bool perStageTwr = true;
        private static float autoPendingSince;
        //static int division = 7;

        // Label/field widths are fixed so rows line up in columns instead of drifting with text length.
        private const float labelWidth = 110f;
        private const float valueWidth = 130f;
        private const float inputLabelWidth = 150f;
        private const float inputWidth = 80f;
        private const float statLabelWidth = 96f;
        private const float statValueWidth = 62f;
        private static readonly Color warnColor = new Color(1f, 0.72f, 0.25f);

        /// <summary>Compact "before / after" table: current craft versus the optimizer result.</summary>
        private static void DrawComparisonTable()
        {
            if (currentSnapshots.Count == 0) return;

            float optimizedMass = CachedTotalMass();
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_table_stage"), GUILayout.Width(28f));
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_table_dv"), GUILayout.Width(94f));
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_table_twr"), GUILayout.Width(80f));
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_table_mass"), GUILayout.Width(86f));
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_table_engine"));
            GUILayout.EndHorizontal();

            // Topmost stage first: matches how the rocket reads from the top down.
            for (int i = currentSnapshots.Count - 1; i >= 0; i--)
            {
                SEO_ShipReader.StageSnapshot snapshot = currentSnapshots[i];
                // Only show optimised values when they still describe the current inputs.
                bool hasResult = resultValid && i < stageList.Count;
                float newDv = hasResult ? stageList[i].trueDv : 0f;
                float newTwr = hasResult ? stageList[i].trueTwr : 0f;
                float newMass = hasResult ? stageList[i].stageWeight : 0f;
                string newEngine = hasResult ? stageList[i].engineName : string.Empty;

                GUILayout.BeginHorizontal();
                GUILayout.Label(snapshot.Stage.ToString(), GUILayout.Width(28f));
                GUILayout.Label(hasResult ? FormatDelta(snapshot.Dv, newDv, "F0") : snapshot.Dv.ToString("F0"), GUILayout.Width(94f));
                GUILayout.Label(hasResult ? FormatDelta(snapshot.Twr, newTwr, "F2") : snapshot.Twr.ToString("F2"), GUILayout.Width(80f));
                GUILayout.Label(hasResult ? FormatDelta(snapshot.Mass, newMass, "G3") : snapshot.Mass.ToString("G3"), GUILayout.Width(86f));

                if (i < stageList.Count)
                {
                    StageConfig row = stageList[i];
                    // One button that shows the mode currently in force: blue = vacuum, amber = sea level.
                    GUIContent modeContent = new GUIContent(
                        Localizer.Format(row.useVacuum ? "#LOC_SHIPENGOP_vacuum" : "#LOC_SHIPENGOP_atmosphere"),
                        Localizer.Format("#LOC_SHIPENGOP_tooltips_atmosphereMode"));
                    Color previousTint = GUI.backgroundColor;
                    GUI.backgroundColor = row.useVacuum ? vacuumTint : atmosphereTint;
                    if (GUILayout.Button(modeContent, GUILayout.Width(64f)))
                    {
                        row.useVacuum = !row.useVacuum;
                        ApplyAtmosphereMode(row, snapshot);
                    }
                    GUI.backgroundColor = previousTint;

                    bool wantsTwrLock = GUILayout.Toggle(row.lockTwr,
                        new GUIContent(Localizer.Format("#LOC_SHIPENGOP_lockTwr"),
                            Localizer.Format("#LOC_SHIPENGOP_tooltips_lockTwr")), GUILayout.Width(32f));
                    if (wantsTwrLock != row.lockTwr)
                    {
                        row.lockTwr = wantsTwrLock;
                        cachedSignature = int.MinValue;
                        autoBaseline = string.Empty;
                    }
                }

                GUILayout.Label(EngineCell(snapshot.EngineName, newEngine), GUILayout.Width(150f));
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_table_total"), GUILayout.Width(160f));
            GUILayout.Label(FormatDelta(currentTotalMass, optimizedMass, "G4"));
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        /// <summary>One short line: the craft's engine, plus the recommendation only when it differs.</summary>
        private static readonly Color vacuumTint = new Color(0.42f, 0.70f, 1f);
        private static readonly Color atmosphereTint = new Color(1f, 0.76f, 0.38f);

        private static string EngineCell(string current, string recommended)
        {
            string before = string.IsNullOrEmpty(current) ? "-" : Shorten(current);
            if (string.IsNullOrEmpty(recommended) || recommended == current) return before;
            return before + " → " + Shorten(recommended);
        }

        /// <summary>Drop the quoted suffix so long part titles fit on one row.</summary>
        private static string Shorten(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            int cut = title.IndexOf('“');
            if (cut < 0) cut = title.IndexOf('"');
            string head = cut > 0 ? title.Substring(0, cut) : title;
            return head.Length > 15 ? head.Substring(0, 15) + "…" : head;
        }

        /// <summary>Re-derive a stage's numbers for vacuum or sea-level operation.</summary>
        private static void ApplyAtmosphereMode(StageConfig stage, SEO_ShipReader.StageSnapshot snapshot)
        {
            CelestialBody planet = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == stage.planetNameStr);
            if (planet != null)
            {
                stage.altitude = stage.useVacuum ? (float)planet.atmosphereDepth : 0f;
            }

            float dv = stage.useVacuum ? snapshot.Dv : (snapshot.DvAsl > 0f ? snapshot.DvAsl : snapshot.Dv);
            float twr = stage.useVacuum ? snapshot.Twr : (snapshot.TwrAsl > 0f ? snapshot.TwrAsl : snapshot.Twr);
            if (dv > 0f) stage.targetDv = Mathf.Max(1f, Mathf.Round(dv));
            if (twr > 0f) stage.targetTwr = Mathf.Max(0.01f, Mathf.Round(twr * 100f) / 100f);

            cachedSignature = int.MinValue;
            autoBaseline = string.Empty;
        }

        private static string FormatDelta(float before, float after, string format)
        {
            if (after <= 0f || float.IsInfinity(after) || after == float.MaxValue)
            {
                return before.ToString(format);
            }
            return before.ToString(format) + " → " + after.ToString(format);
        }

        /// <summary>Cheap signature of everything the optimizer reads, used to debounce auto runs.</summary>
        private static string InputSignature()
        {
            unchecked
            {
                int hash = SEO_OptimizationCache.Version;
                hash = hash * 31 + SEO_Main.payload.GetHashCode();
                for (int i = 0; i < stageList.Count; i++)
                {
                    StageConfig stage = stageList[i];
                    hash = hash * 31 + stage.targetDv.GetHashCode();
                    hash = hash * 31 + stage.targetTwr.GetHashCode();
                    hash = hash * 31 + stage.additionalMass.GetHashCode();
                    hash = hash * 31 + stage.constrainOp;
                    hash = hash * 31 + stage.qtyFilter;
                    hash = hash * 31 + i;
                    hash = hash * 31 + (stage.filterT ? 1 : 0);
                    hash = hash * 31 + (stage.manualMode ? 2 : 0);
                    hash = hash * 31 + (stage.keepEngine ? 4 : 0);
                    hash = hash * 31 + (stage.includeInOp ? 8 : 0);
                    hash = hash * 31 + (stage.planetNameStr == null ? 0 : stage.planetNameStr.GetHashCode());
                    hash = hash * 31 + stage.altitude.GetHashCode();
                }
                return hash.ToString();
            }
        }

        public static void DrawStageWindow(int windowID)
        {
            EnsureDefaultFuelSelection();
            if (GUI.Button(new Rect(SEO_Main.StageWndRect.size.x - 17, 2, 15, 15), "X", SEO_Main.smallButton))
            {
                SEO_Main.showStageWindow = false;
            }
            if (GUI.Button(new Rect(SEO_Main.StageWndRect.size.x - 32, 2, 15, 15), new GUIContent("?", Localizer.Format("#LOC_SHIPENGOP_tooltips_toggleTooltips")), SEO_Main.smallButton))
            {
                SEO_Main.showTooltip = !SEO_Main.showTooltip;
            }

            if (stageList.Count < 1)
            {
                stageList.Add(new StageConfig());
            }

            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical();
            GUILayout.Label(new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_propellantFilter"), Localizer.Format("#LOC_SHIPENGOP_tooltips_fuelFilter")));
            scrollPosition_fuelGlobal = GUILayout.BeginScrollView(scrollPosition_fuelGlobal, GUILayout.Width(300), GUILayout.Height(100));
            selectedFuelGlobal = GetFuelFilter(selectedFuelGlobal);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            //GUILayout.Space(20);
            GUILayout.Label("", GUILayout.ExpandWidth(true));
            SEO_Main.ToggleCareer();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUILayout.BeginVertical();

            SEO_Main.PayloadInput();

            GUILayout.Label(new GUIContent(Localizer.Format("#LOC_SHIPENGOP_quickStart"), Localizer.Format("#LOC_SHIPENGOP_tooltips_quickStart")), GUILayout.Width(560f));

            float totalDv = CachedTotalDv();
            float totalMass = CachedTotalMass();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_stageWindow_totalDv"), GUILayout.Width(labelWidth));
            GUILayout.Label(totalDv.ToString("F0") + " " + Localizer.Format("#LOC_SHIPENGOP_units_metersPerSecond"), GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_shipSpecs_totalMass"), GUILayout.Width(labelWidth));
            GUILayout.Label(totalMass == float.MaxValue ? Localizer.Format("#LOC_SHIPENGOP_infinity") : (totalMass.ToString("G3") + " " + Localizer.Format("#LOC_SHIPENGOP_units_tons")), GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

        //PSO staging
            if (GUILayout.Button(new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_optimizeStaging"), Localizer.Format("#LOC_SHIPENGOP_tooltips_optimizingStages"))))
            {
                StartOptimization(CoOptimizeSelectedStages());
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(Localizer.Format("#LOC_SHIPENGOP_readVessel"), Localizer.Format("#LOC_SHIPENGOP_tooltips_readVessel"))))
            {
                float payload;
                float total;
                if (SEO_ShipReader.TryRead(currentSnapshots, out payload, out total) &&
                    SEO_ShipReader.ApplyToStageList(currentSnapshots, payload))
                {
                    currentTotalMass = total;
                    targetTotalDv = stageList.Sum(stage => stage.targetDv);
                    cachedSignature = int.MinValue;
                    autoBaseline = string.Empty;
                }
            }
            autoOptimize = GUILayout.Toggle(autoOptimize, new GUIContent(Localizer.Format("#LOC_SHIPENGOP_autoOptimize"), Localizer.Format("#LOC_SHIPENGOP_tooltips_autoOptimize")));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(Localizer.Format("#LOC_SHIPENGOP_totalDvTarget"), Localizer.Format("#LOC_SHIPENGOP_tooltips_totalDvTarget")), GUILayout.Width(labelWidth));
            string dvField = GUILayout.TextField(targetTotalDv <= 0f ? string.Empty : targetTotalDv.ToString("F0"), 7, GUILayout.Width(inputWidth));
            float parsedTotalDv;
            if (float.TryParse(dvField, out parsedTotalDv) && parsedTotalDv > 0f) targetTotalDv = parsedTotalDv;
            if (GUILayout.Button(Localizer.Format("#LOC_SHIPENGOP_useCraftDv")))
            {
                targetTotalDv = stageList.Sum(stage => stage.trueDv > 0f ? stage.trueDv : stage.targetDv);
                cachedSignature = int.MinValue;
                autoBaseline = string.Empty;
            }
            bool perStageTwrNow = GUILayout.Toggle(perStageTwr,
                new GUIContent(Localizer.Format("#LOC_SHIPENGOP_perStageTwr"), Localizer.Format("#LOC_SHIPENGOP_tooltips_perStageTwr")));
            if (perStageTwrNow != perStageTwr)
            {
                perStageTwr = perStageTwrNow;
                cachedSignature = int.MinValue;
                autoBaseline = string.Empty;
            }
            GUILayout.EndHorizontal();

            DrawComparisonTable();

            if (autoOptimize && !operationRunning && stageList.Count > 1)
            {
                string now = InputSignature();
                if (autoBaseline.Length == 0)
                {
                    autoBaseline = now;
                    autoPendingSignature = string.Empty;
                }
                else if (now != autoBaseline)
                {
                    // Wait until the inputs stop changing: dragging a slider or typing a value
                    // would otherwise restart the run every frame.
                    if (now != autoPendingSignature)
                    {
                        autoPendingSignature = now;
                        autoPendingSince = Time.realtimeSinceStartup;
                    }
                    else if (Time.realtimeSinceStartup - autoPendingSince > 0.35f)
                    {
                        autoBaseline = now;
                        autoPendingSignature = string.Empty;
                        StartOptimization(CoOptimizeSelectedStages());
                    }
                }
                else
                {
                    autoPendingSignature = string.Empty;
                }
            }
            GUILayout.Box( Localizer.Format("#LOC_SHIPENGOP_stageWindow_selectedStages") + " " + CachedSelectedStageList() );

            //division = Mathf.RoundToInt(GUILayout.HorizontalSlider(division, 1, 10));

            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.BeginVertical();

        //Stage List Starts
            scrollPosition_stages = GUILayout.BeginScrollView(scrollPosition_stages, GUILayout.Width(600), GUILayout.Height(360));

            // Draw top stage first, matching the comparison table and the rocket itself.
            for (int drawIndex = stageList.Count - 1; drawIndex >= 0; drawIndex--)
            {
                StageConfig stage = stageList[drawIndex];
                GUILayout.BeginHorizontal(GUILayout.Width(580)); // Begin stage title
                if (stageList.Count > 1)
                {
                    if (GUILayout.Button("-", GUILayout.Width(20)))
                    {
                        stageList.Remove(stage);
                        break;
                    }
                }
                GUILayout.Box(Localizer.Format("#LOC_SHIPENGOP_stageWindow_stage") + stage.kspStage);
                GUILayout.EndHorizontal(); // End stage title

                stage.payloadMass = stageList.IndexOf(stage) == 0 ? SEO_Main.payload : stageList[stageList.IndexOf(stage) - 1].stageWeight;

                GUILayout.BeginHorizontal(); // Begin stage stats

                    GUILayout.BeginVertical(GUILayout.Width(235)); // Begin left side

                //stage.manualMode = GUILayout.Toggle(stage.manualMode, Localizer.Format("#LOC_SHIPENGOP_stageWindow_manualMode"));

                        GUILayout.BeginHorizontal();
                GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_stageWindow_desiredDv"), GUILayout.Width(inputLabelWidth));
                string targetDvStr = GUILayout.TextField(stage.targetDv.ToString("F0"), 6, GUILayout.Width(inputWidth));
                float.TryParse(targetDvStr, out stage.targetDv);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_stageWindow_desiredTwr"), GUILayout.Width(inputLabelWidth));
                string targetTwrStr = GUILayout.TextField(stage.targetTwr.ToString("F2"), 6, GUILayout.Width(inputWidth));
                float.TryParse(targetTwrStr, out stage.targetTwr);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_extraMass"), Localizer.Format("#LOC_SHIPENGOP_tooltips_extraMass")), GUILayout.Width(inputLabelWidth));
                string addiMassStr = GUILayout.TextField(stage.additionalMass.ToString(stage.additionalMass == 0 ? "F2" : ""), 7, GUILayout.Width(inputWidth));
                float.TryParse(addiMassStr, out stage.additionalMass);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                //stage.strictMode = GUILayout.Toggle(stage.strictMode, new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_strictMode"), Localizer.Format("#LOC_SHIPENGOP_tooltips_strictMode")));
                stage.constrainOp = SetConstrain(stage.constrainOp);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                stage.filterT = GUILayout.Toggle(stage.filterT, Localizer.Format("#LOC_SHIPENGOP_stageWindow_QtyFilter"));
                if (stage.filterT)
                {
                    stage.qtyFilter = Mathf.RoundToInt(GUILayout.HorizontalSlider(stage.qtyFilter, 1, 21, GUILayout.Width(80)));
                    GUILayout.Label(stage.qtyFilter.ToString());
                }
                        GUILayout.EndHorizontal();

                GUILayout.Space(3);
                SEO_Main.GetAtm(stage);

                    GUILayout.EndVertical(); // End left side

                GUILayout.Space(10);

                    GUILayout.BeginVertical(GUILayout.Width(320)); // Begin right side

                        GUILayout.BeginHorizontal();
                stage.manualMode = GUILayout.Toggle(stage.manualMode, Localizer.Format("#LOC_SHIPENGOP_stageWindow_manualMode"));
                stage.keepEngine = GUILayout.Toggle(stage.keepEngine, new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_keepEngine"), Localizer.Format("#LOC_SHIPENGOP_tooltips_asparagusStaging")));
                //stage.includeInOp = GUILayout.Toggle(stage.includeInOp, new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_includeInOp"), Localizer.Format("#LOC_SHIPENGOP_tooltips_includeOptimizing")));
                        GUILayout.EndHorizontal();

                if (stageList.IndexOf(stage) == 0)
                { stage.keepEngine = false; }
                else if (stage.keepEngine)
                {
                    //stage.manualMode = true;
                    stage.includeInOp = stageList[stageList.IndexOf(stage) - 1].includeInOp;
                }
                //GUILayout.EndVertical();

                //GUILayout.Label(stage.engineName);
                if (stage.manualMode)
                {
                    if (GUILayout.Button(new GUIContent(stage.engineName, Localizer.Format("#LOC_SHIPENGOP_tooltips_stageEngineManualMode")), SEO_Main.engineNameButton, GUILayout.Width(320)))
                    {
                        //selectedFuelStage = selectedFuelGlobal;
                        selectedFuelStage.Clear();
                        selectedFuelStage.UnionWith(selectedFuelGlobal);
                        selectedStageForEngine = stage;
                        //selectedStageIndex = stageList.IndexOf(stage);
                        //stage.manualSelEngine = stage.engineName;
                        SEO_Main.showStageEngWindow = true;
                    }
                }
                else { GUILayout.Box(new GUIContent(stage.engineName, Localizer.Format("#LOC_SHIPENGOP_tooltips_stageEngineAutoMode")), SEO_Main.engineNameBox, GUILayout.Width(320)); }

                        GUILayout.BeginHorizontal(); // Begin right side columns
                            GUILayout.BeginVertical(GUILayout.Width(160)); // Begin right side column 1

                stage.strictMode = GUILayout.Toggle(stage.strictMode, new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_strictMode"), Localizer.Format("#LOC_SHIPENGOP_tooltips_strictMode")));
                GUILayout.Space(3);
                if (stage.stageWeight != float.MaxValue)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_shipSpecs_engineQty"), GUILayout.Width(statLabelWidth));
                    GUILayout.Label(stage.engineQty.ToString(), GUILayout.Width(statValueWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_stageWindow_stageMass"), GUILayout.Width(statLabelWidth));
                    GUILayout.Label(stage.stageWeight.ToString("G3") + " " + Localizer.Format("#LOC_SHIPENGOP_units_tons"), GUILayout.Width(statValueWidth));
                    GUILayout.EndHorizontal();

                    if (stage.trueTwr < stage.targetTwr || stage.trueDv < stage.targetDv) { GUI.color = warnColor; }
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(stage.constrainOp == 0 ? Localizer.Format("#LOC_SHIPENGOP_shipSpecs_twr") : Localizer.Format("#LOC_SHIPENGOP_shipSpecs_dv"), GUILayout.Width(statLabelWidth));
                    GUILayout.Label(stage.constrainOp == 0
                        ? stage.trueTwr.ToString("F2")
                        : stage.trueDv.ToString("F0") + " " + Localizer.Format("#LOC_SHIPENGOP_units_metersPerSecond"), GUILayout.Width(statValueWidth));
                    GUILayout.EndHorizontal();
                    GUI.color = Color.white;
                }
                            GUILayout.EndVertical(); // End right side column 1

                            GUILayout.BeginVertical(); // Begin right side column 2
                stage.includeInOp = GUILayout.Toggle(stage.includeInOp, new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_includeInOp"), Localizer.Format("#LOC_SHIPENGOP_tooltips_includeOptimizing")));
                    
            //Split stage
                if (GUILayout.Button(new GUIContent(Localizer.Format("#LOC_SHIPENGOP_stageWindow_splitStage"), Localizer.Format("#LOC_SHIPENGOP_tooltips_automaticSplit")), GUILayout.Width(100), GUILayout.Height(60)))
                {
                    StartOptimization(CoSplitSelectedStage(stage));
                }
                            GUILayout.EndVertical(); // End right side column 2
                
                        GUILayout.EndHorizontal(); // End right side columns

                    GUILayout.EndVertical(); // End right side

                GUILayout.EndHorizontal(); // End stage stats

                GUILayout.Space(5);
            }

            if (GUILayout.Button("+"))
            {
                stageList.Add(new StageConfig());
            }


            GUILayout.EndVertical();
            GUILayout.EndScrollView();

            SEO_Main.DrawToolTip();
            GUI.DragWindow();
        }

        public static void DrawStageEngineWindow(int windowID)
        {
            if (GUI.Button(new Rect(SEO_Main.StageEngWndRect.size.x - 17, 2, 15, 15), "X", SEO_Main.smallButton))
            {
                SEO_Main.showStageEngWindow = false;
            }
            if (GUI.Button(new Rect(SEO_Main.StageEngWndRect.size.x - 32, 2, 15, 15), "?", SEO_Main.smallButton))
            {
                SEO_Main.showTooltip = !SEO_Main.showTooltip;
            }

            GUILayout.BeginVertical(GUILayout.Width(250));

            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_stageWindow_propellantFilterManualMode"));
            scrollPosition_fuelGlobal = GUILayout.BeginScrollView(scrollPosition_fuelGlobal, GUILayout.Width(300), GUILayout.Height(100));
            //SEO_StageWindow fuelGlobalRef = new SEO_StageWindow();
            //selectedFuelStage = fuelGlobalRef.GetFuelFilter(selectedFuelStage);
            selectedFuelStage = GetFuelFilter(selectedFuelStage);
            GUILayout.EndScrollView();

            GUILayout.Space(20);

            //var topEngineList = stageList[selectedStageIndex].GetTopEngineList(selectedFuelStage);
            var topEngineList = selectedStageForEngine.GetTopEngineList(selectedFuelStage);

            GUILayout.Label(Localizer.Format("#LOC_SHIPENGOP_stageWindow_engineConfig"));
            scrollPosition_eng = GUILayout.BeginScrollView(scrollPosition_eng, GUILayout.Width(300), GUILayout.Height(500));
            foreach (var engine in topEngineList)
            {
                if (GUILayout.Button(engine.Name, SEO_Main.engineNameButton))
                {
                    //stageList[selectedStageIndex].SetManualSelEngine(engine.Name);
                    selectedStageForEngine.SetManualSelEngine(engine.Name);
                    SEO_Main.showStageEngWindow = false;
                    //selectedEngineInGross = engine.Name;
                    //showAssignEngine = true;
                }

                GUILayout.BeginHorizontal();
                GUILayout.Box(Localizer.Format("#LOC_SHIPENGOP_shipSpecs_totalMass") + engine.weightTotal.ToString("G3") + " " + Localizer.Format("#LOC_SHIPENGOP_units_tons"));
                GUILayout.Box(Localizer.Format("#LOC_SHIPENGOP_shipSpecs_fuelMass") + engine.fuelMass.ToString("G3") + " " + Localizer.Format("#LOC_SHIPENGOP_units_tons"));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Box(Localizer.Format("#LOC_SHIPENGOP_shipSpecs_engineQty") + engine.qtyEng.ToString());

                //if (engine.twr < stageList[selectedStageIndex].targetTwr) { GUI.color = Color.red; }
                if (engine.twr < selectedStageForEngine.targetTwr) { GUI.color = Color.red; }
                GUILayout.Box(selectedStageForEngine.constrainOp == 0 ?
                    //GUILayout.Box(stageList[selectedStageIndex].constrainOp == 0 ?
                    (Localizer.Format("#LOC_SHIPENGOP_shipSpecs_twr") + engine.twr.ToString("F2")) :
                    (Localizer.Format("#LOC_SHIPENGOP_shipSpecs_dv") + engine.dv.ToString("F0") + " " + Localizer.Format("#LOC_SHIPENGOP_units_metersPerSecond")));
                GUI.color = Color.white;

                GUILayout.EndHorizontal();

                GUILayout.Space(8);
            }

            GUILayout.EndScrollView();

            GUILayout.EndVertical();

            GUI.DragWindow();
        }

        static int SetConstrain(int oldConstrain)
        {
            GUIContent[] constrainGrid = new GUIContent[]
            {
                new GUIContent(SEO_GraphWindow.constrainOpStr[0], Localizer.Format("#LOC_SHIPENGOP_tooltips_constrainDv")),
                new GUIContent(SEO_GraphWindow.constrainOpStr[1], Localizer.Format("#LOC_SHIPENGOP_tooltips_constrainTwr"))
            };
            //int xSelection = GUILayout.SelectionGrid(oldConstrain, SEO_GraphWindow.constrainOpStr, 2);
            int xSelection = GUILayout.SelectionGrid(oldConstrain, constrainGrid, 2);
            if (xSelection != oldConstrain)
            {
                return xSelection;
            }

            return oldConstrain;
        }

        /// <summary>
        /// Auto mode needs at least one propellant group selected. Players almost never realise that,
        /// so preselect every usable group once instead of silently returning "no engine".
        /// </summary>
        /// <summary>Engine data was rebuilt (career filter, propellant ratios): drop stale fuel picks.</summary>
        public static void NotifyEngineDataRefreshed()
        {
            if (SEO_Main.groupedEngines == null) return;

            selectedFuelGlobal.RemoveWhere(fuel => !SEO_Main.groupedEngines.ContainsKey(fuel));
            selectedFuelStage.RemoveWhere(fuel => !SEO_Main.groupedEngines.ContainsKey(fuel));

            // If the rebuilt table invalidated every pick, fall back to selecting everything again.
            if (selectedFuelGlobal.Count == 0) fuelDefaultsApplied = false;

            cachedSignature = int.MinValue;
            autoBaseline = string.Empty;
        }

        public static void EnsureDefaultFuelSelection()
        {
            if (fuelDefaultsApplied) return;
            if (SEO_Main.groupedEngines == null || SEO_Main.groupedEngines.Count == 0) return;
            if (SEO_Main.fuelTypes == null || SEO_Main.fuelTypes.Length == 0) return;

            foreach (var fuel in SEO_Main.fuelTypes)
            {
                if (!SEO_Main.groupedEngines.ContainsKey(fuel)) continue;
                string first = SEO_Main.groupedEngines[fuel].FirstOrDefault();
                if (first == null || !SEO_Main.engineData.ContainsKey(first)) continue;
                if (SEO_Main.engineData[first].wdr == 1f) continue;
                if (fuel.IndexOf("IntakeAir", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                selectedFuelGlobal.Add(fuel);
            }

            fuelDefaultsApplied = true;
        }

        public static HashSet<string> GetFuelFilter(HashSet<string> selectedFuelOld)
        {

            foreach (var fuel in SEO_Main.fuelTypes)
            {
                //if (fuel == "None") continue;
                if (!SEO_Main.groupedEngines.ContainsKey(fuel)) { continue; }
                else if (SEO_Main.engineData[SEO_Main.groupedEngines[fuel].FirstOrDefault()].wdr == 1f) { continue; }

                bool wasSelected = selectedFuelOld.Contains(fuel);
                //bool isSelectedNow = GUILayout.Toggle(wasSelected, fuel);
                string displayName = SEO_Main.propellantDisplayNames.ContainsKey(fuel) ? SEO_Main.propellantDisplayNames[fuel] : fuel;
                bool isSelectedNow = GUILayout.Toggle(wasSelected, displayName);

                if (isSelectedNow && !wasSelected)
                    selectedFuelOld.Add(fuel);
                else if (!isSelectedNow && wasSelected)
                    selectedFuelOld.Remove(fuel);
            }

            return selectedFuelOld;
        }

        //static void FindOptimalStaging(float dvSum, StageConfig stage0, StageConfig stage1, List<StageConfig> stageListInternal)
        //{
        //    List<float> domains = new List<float> { 0f };
        //    for (int i = 1; i < division; i++)
        //    {
        //        domains.Add(i * (dvSum / division));
        //    }
        //    domains.Add(dvSum - 0.1f);

        //    //List<StageConfig> stageListInternal = new List<StageConfig> { stage0, stage1 };
        //    List<(float dv, float totalMass)> minimunList = new List<(float, float)>();

        //    for (int iter = 0; iter < division; iter++)
        //    {
        //        var stageZero = GetOptimalStageZero(dvSum, domains[iter], domains[iter+1], stage0, stage1, stageListInternal);
        //        minimunList.Add(stageZero);
        //    }

        //    float optimalStageZeroDv = Mathf.Floor(minimunList.OrderBy(s => s.totalMass).First().dv);
        //    stage0.targetDv = optimalStageZeroDv;
        //    stage1.targetDv = dvSum - optimalStageZeroDv;

        //    Debug.Log("[ShipEngineOp] dv minimum: " + string.Join(", ", minimunList.Select(s => s.dv)));
        //    Debug.Log("[ShipEngineOp] mass minimum: " + string.Join(", ", minimunList.Select(s => s.totalMass)));
        //}

        //static (float, float) GetOptimalStageZero(float dvSum, float left, float right, StageConfig stage0, StageConfig stage1, List<StageConfig> mockStages)
        //{
        //    float tolerance = 0.1f;

        //    while (right - left > tolerance)
        //    {
        //        float stage0_dv0 = left + (right - left) / 3;
        //        float stage0_dv1 = right - (right - left) / 3;

        //        stage0.targetDv = stage0_dv0;
        //        stage1.targetDv = dvSum - stage0_dv0;
        //        float totalMass_0 = GetTotalWeight(mockStages);

        //        stage0.targetDv = stage0_dv1;
        //        stage1.targetDv = dvSum - stage0_dv1;
        //        float totalMass_1 = GetTotalWeight(mockStages);

        //        if (totalMass_0 < totalMass_1)
        //        {
        //            right = stage0_dv1; // Minimum is in the left third
        //        }
        //        else
        //        {
        //            left = stage0_dv0; // Minimum is in the right third
        //        }
        //    }

        //    float optimalDv = (left + right) / 2;
        //    stage0.targetDv = optimalDv;
        //    stage1.targetDv = dvSum - optimalDv;

        //    float minTotalMass = GetTotalWeight(mockStages);

        //    return (optimalDv,  minTotalMass);
        //}

        /// <summary>Runs a long operation on the addon so it spans frames instead of freezing the editor.</summary>
        public static void StartOptimization(IEnumerator routine)
        {
            if (operationRunning || routine == null) return;

            if (SEO_Main.instance == null)
            {
                Debug.LogWarning("[ShipEngineOp] No host addon available; running the optimization inline.");
                while (routine.MoveNext()) { }
                cachedSignature = int.MinValue;
                return;
            }

            operationRunning = true;
            optimizationCancelled = false;
            optimizationProgress = 0f;
            SEO_Main.instance.StartCoroutine(CoRunOperation(routine));
        }

        private static IEnumerator CoRunOperation(IEnumerator routine)
        {
            yield return SEO_Main.instance.StartCoroutine(routine);
            operationRunning = false;
            optimizationCancelled = false;
            optimizationProgress = 1f;
            operationLabel = string.Empty;
            cachedSignature = int.MinValue;
            RefreshStageSummaryCache();
            autoBaseline = InputSignature();   // the run itself changed targetDv; accept that as the new baseline
            autoPendingSignature = string.Empty;
        }

        /// <summary>Auto-optimize every stage marked "include in optimization".</summary>
        private static IEnumerator CoOptimizeSelectedStages()
        {
            operationLabel = Localizer.Format("#LOC_SHIPENGOP_stageWindow_optimizeStaging");
            yield return null;

            List<StageConfig> optimizingStages = stageList.Where(s => s.includeInOp).ToList();
            if (optimizingStages.Count <= 1) yield break;

            float totalDv = targetTotalDv > 0f ? targetTotalDv : optimizingStages.Sum(s => s.targetDv);
            Debug.Log(string.Format("[ShipEngineOp] optimize start stages={0} targetTotalDv={1:F1} requested={2:F1} currentSum={3:F1}",
                optimizingStages.Count, targetTotalDv, totalDv, optimizingStages.Sum(s => s.targetDv)));
            foreach (StageConfig stage in optimizingStages)
            {
                stage.constrainOp = 0;
                if (!perStageTwr && !stage.lockTwr && stage != optimizingStages.First())
                {
                    // Old behaviour: every stage is held to the same TWR and atmosphere as stage 0.
                    stage.targetTwr = optimizingStages.First().targetTwr;
                    stage.SetPlanet(optimizingStages.First().planetNameStr);
                    stage.altitude = optimizingStages.First().altitude;
                }
            }

            float lastTotalWeight = float.MaxValue;
            List<float> lastDvList = new List<float>();
            List<StageConfig> lastStageList = stageList.ToList();

            for (int i = 1; i <= optimizingStages.Count; i++)
            {
                if (optimizationCancelled) yield break;

                foreach (StageConfig stage in optimizingStages)
                {
                    if (stage != optimizingStages.First())
                    { stage.targetDv = 0; }
                    else
                    { stage.targetDv = totalDv; }
                }

                List<StageConfig> mockStageList = stageList.ToList();
                List<StageConfig> mockOptimizingStages = optimizingStages.ToList();

                foreach (StageConfig stage in optimizingStages)
                {
                    if (optimizingStages.IndexOf(stage) >= i)
                    {
                        mockOptimizingStages.Remove(stage);
                        mockStageList.Remove(stage);
                    }
                }

                if (mockOptimizingStages.Count > 1)
                {
                    float mockTotalDv = mockOptimizingStages.Sum(s => s.targetDv);
                    yield return CoOptimizeStages(mockOptimizingStages, mockStageList, mockTotalDv);
                    if (optimizationCancelled) yield break;
                }

                float newTotalWeight = GetTotalWeight(mockStageList);
                Debug.Log(string.Format("[ShipEngineOp] iter={0} keptStages={1} weight={2:F3} lastWeight={3:F3} dvList=[{4}]",
                    i, mockOptimizingStages.Count, newTotalWeight, lastTotalWeight,
                    string.Join(", ", mockStageList.Select(x => x.targetDv.ToString("F0")).ToArray())));

                if (newTotalWeight >= lastTotalWeight && lastTotalWeight != float.MaxValue)
                {
                    stageList = lastStageList.ToList();
                    for (i = 0; i < stageList.Count(); i++)
                    { stageList[i].targetDv = lastDvList[i]; }
                    break;
                }
                else
                {
                    lastTotalWeight = newTotalWeight;
                    lastStageList = mockStageList.ToList();
                    lastDvList = lastStageList.Select(s => s.targetDv).ToList();
                }

                yield return null;
            }
        }

        /// <summary>Split the chosen stage as often as it keeps lowering the vessel mass.</summary>
        private static IEnumerator CoSplitSelectedStage(StageConfig stage)
        {
            operationLabel = Localizer.Format("#LOC_SHIPENGOP_stageWindow_splitStage");
            yield return null;

            stage.keepEngine = false;
            stage.constrainOp = 0;
            stage.GetBestEngine();

            float startingPayload = stage.payloadMass;

            List<StageConfig> splitStageList = new List<StageConfig> { stage };
            float newTotalWeight = stage.stageWeight;
            float lastTotalWeight = float.MaxValue;
            List<StageConfig> lastStageList = splitStageList.ToList();
            List<float> lastDvList = lastStageList.Select(s => s.targetDv).ToList();

            while (newTotalWeight < lastTotalWeight || newTotalWeight == float.MaxValue)
            {
                if (optimizationCancelled) yield break;

                lastTotalWeight = newTotalWeight;
                lastStageList = splitStageList.ToList();
                lastDvList = lastStageList.Select(s => s.targetDv).ToList();

                splitStageList.Insert(0, stage.CloneStageWithPayload(startingPayload));
                float splitTotalDv = splitStageList.Sum(s => s.targetDv);
                yield return CoOptimizeStages(splitStageList, splitStageList, splitTotalDv);
                if (optimizationCancelled) yield break;

                newTotalWeight = GetTotalWeight(splitStageList);
            }

            lastStageList.First().payloadMass = startingPayload;

            if (lastStageList.Count > 1)
            {
                for (int i = 0; i < lastStageList.Count; i++)
                { lastStageList[i].targetDv = lastDvList[i]; }
                lastStageList.Remove(stage);
                stageList.InsertRange(stageList.IndexOf(stage), lastStageList);
            }
            else
            {
                stage.payloadMass = startingPayload;
                stage.targetDv = lastDvList.Last();
            }

            splitStageList.Clear();
            lastStageList.Clear();
        }

        /// <summary>Localized label with a plain-text fallback when the tag is missing.</summary>
        private static string CancelLabel()
        {
            string text = Localizer.Format("#LOC_SHIPENGOP_cancel");
            return string.IsNullOrEmpty(text) || text.StartsWith("#") ? "Cancel" : text;
        }

        /// <summary>Progress window shown while an optimization is running.</summary>
        public static void DrawOperationOverlay()
        {
            if (!operationRunning) return;

            if (operationWindowRect.width < 1f)
            {
                operationWindowRect = new Rect((Screen.width - 320f) * 0.5f, (Screen.height - 96f) * 0.5f, 320f, 96f);
            }
            operationWindowRect = GUILayout.Window(0x5E01, operationWindowRect, DrawOperationWindow,
                Localizer.Format("#LOC_SHIPENGOP_mainWindowTitle"));
        }

        private static void DrawOperationWindow(int id)
        {
            GUILayout.Space(4f);
            GUILayout.Label(string.IsNullOrEmpty(operationLabel)
                ? Localizer.Format("#LOC_SHIPENGOP_mainWindowTitle")
                : operationLabel);
            GUILayout.Label(Mathf.RoundToInt(Mathf.Clamp01(optimizationProgress) * 100f) + " %");
            GUILayout.Space(4f);
            if (GUILayout.Button(CancelLabel()))
            {
                optimizationCancelled = true;
            }
            GUI.DragWindow();
        }

        public static void PsoSelectedStages(List<StageConfig> optimizingStages, List<StageConfig> mockStageList)
        {
            float totalDv = optimizingStages.Sum(s => s.targetDv);
            if (totalDv <= 0) { return; }

            int totalVariables = optimizingStages.Count();
            int numParticles = 50;
            int maxIterations = 1000;
            float inertiaWeight = 0.729f;
            float cognitiveWeight = 1.49445f;
            float socialWeight = 1.49445f;
            float fitnessTolerance = 1e-3f;
            int consecutiveConvergedIterations = 10;

            Debug.Log("Starting deterministic stage-distribution optimization...");
            Debug.Log($"Constraint: x1 + ... + x{totalVariables} = {totalDv:F2}");
            Debug.Log("--------------------------------------------------");

            // A run starts from a clean slate: engine ranking depends on fuel filter, blacklist
            // and extra mass, which are fixed while the optimizer is running.
            SEO_OptimizationCache.InvalidateSelections();

            float[] optimizedAllocation;
            try
            {
                optimizedAllocation = AutoStageOptimizer.Optimize(
                    optimizingStages, stages => GetTotalWeight(mockStageList));
            }
            catch (Exception optimizerException)
            {
                Debug.LogWarning("[ShipEngineOp] Deterministic optimizer failed; falling back to PSO. " + optimizerException);

                PsoOptimizer psoOptimizer = new PsoOptimizer(totalVariables, numParticles, maxIterations,
                                                            inertiaWeight, cognitiveWeight, socialWeight,
                                                            totalDv, fitnessTolerance, consecutiveConvergedIterations);
                psoOptimizer.Optimize(optimizingStages, mockStageList);
                optimizedAllocation = psoOptimizer.GlobalBestPosition;
            }

            Debug.Log("Optimization complete.");
            ApplyOptimizedAllocation(optimizingStages, mockStageList, optimizedAllocation, totalDv);
        }

        /// <summary>
        /// Incremental variant used by the editor UI: the search is advanced a bit per frame so the
        /// game keeps rendering a progress bar instead of locking up.
        /// </summary>
        public static IEnumerator CoOptimizeStages(
            List<StageConfig> optimizingStages, List<StageConfig> mockStageList, float totalDv)
        {
            SEO_OptimizationCache.InvalidateSelections();

            AutoStageOptimizer.Run run = null;
            try
            {
                run = new AutoStageOptimizer.Run(optimizingStages, stages => GetTotalWeight(mockStageList));
            }
            catch (Exception optimizerException)
            {
                Debug.LogWarning("[ShipEngineOp] Optimizer setup failed; running synchronously. " + optimizerException);
            }

            if (run != null)
            {
                while (!run.Finished)
                {
                    if (optimizationCancelled) { yield break; }
                    run.Step(400);
                    optimizationProgress = run.Progress;
                    yield return null;
                }
            }

            float[] allocation = run != null ? run.Best : null;
            if (allocation == null)
            {
                PsoSelectedStages(optimizingStages, mockStageList);
                yield break;
            }

            ApplyOptimizedAllocation(optimizingStages, mockStageList, allocation, totalDv);
        }

        /// <summary>Turns a raw Δv allocation into per-stage integers and tidies up the result.</summary>
        private static void ApplyOptimizedAllocation(
            List<StageConfig> optimizingStages, List<StageConfig> mockStageList,
            float[] optimizedAllocation, float totalDv)
        {
            for (int i = 0; i < optimizedAllocation.Length; i++)
                optimizingStages[i].targetDv = optimizedAllocation[i];
            GetTotalWeight(mockStageList);

            var engineList = optimizingStages.Select(s => s.engineName).ToList();
            float sumOfIndependent = 0f;

            // Manual and onion stages keep the optimizer's rounded result. Auto stages
            // can switch engine after final rounding, so give those engines +1 m/s and
            // re-check the resulting configuration.
            for (int i = 0; i < optimizingStages.Count - 1; i++)
            {
                StageConfig stage = optimizingStages[i];
                if (stage.manualMode || stage.keepEngine)
                {
                    stage.targetDv = Mathf.Round(optimizedAllocation[i]);
                }
                else
                {
                    stage.targetDv = Mathf.Floor(optimizedAllocation[i]);
                    stage.GetBestEngine();
                    if (stage.engineName != engineList[i]) stage.targetDv += 1f;
                }
                sumOfIndependent += stage.targetDv;
            }

            StageConfig lastStage = optimizingStages.Last();
            lastStage.targetDv = Math.Max(0f, totalDv - sumOfIndependent);
            GetTotalWeight(mockStageList);

            // Rounding above can leave the allocation slightly suboptimal, and a shortfall on the
            // last stage silently trims total dv. Move whole m/s between neighbouring stages while
            // that still lowers the vessel mass, then finish by making the sum exact.
            RefineIntegerAllocation(optimizingStages, mockStageList, totalDv);

            resultValid = true;

            float sumTarget = 0f, sumTrue = 0f;
            for (int i = 0; i < optimizingStages.Count; i++)
            {
                sumTarget += optimizingStages[i].targetDv;
                sumTrue += optimizingStages[i].trueDv;
                Debug.Log($"Optimal x{i + 1}: target={optimizingStages[i].targetDv:F2} true={optimizingStages[i].trueDv:F2}");
            }
            Debug.Log(string.Format("[ShipEngineOp] alloc totalDv={0:F2} sumTarget={1:F2} sumTrue={2:F2}", totalDv, sumTarget, sumTrue));
        }

        private static void RefineIntegerAllocation(
            List<StageConfig> optimizingStages, List<StageConfig> mockStageList, float totalDv)
        {
            // Keep every stage above the optimizer's own floor; a 20 m/s stage is dead weight.
            const float MinStageDv = 30f;
            const int MaxRefinePasses = 8;

            if (optimizingStages.Count < 2) return;

            float currentWeight = GetTotalWeight(mockStageList);
            if (currentWeight >= float.MaxValue) return;

            for (int pass = 0; pass < MaxRefinePasses; pass++)
            {
                bool improved = false;

                for (int i = 0; i < optimizingStages.Count - 1; i++)
                {
                    StageConfig left = optimizingStages[i];
                    StageConfig right = optimizingStages[i + 1];
                    if (left.targetDv <= MinStageDv) continue;

                    float savedLeft = left.targetDv;
                    float savedRight = right.targetDv;

                    left.targetDv = savedLeft - 1f;
                    right.targetDv = savedRight + 1f;
                    float shifted = GetTotalWeight(mockStageList);

                    if (shifted < currentWeight)
                    {
                        currentWeight = shifted;
                        improved = true;
                        continue;
                    }

                    left.targetDv = savedLeft + 1f;
                    right.targetDv = savedRight - 1f;
                    if (right.targetDv >= MinStageDv)
                    {
                        shifted = GetTotalWeight(mockStageList);
                        if (shifted < currentWeight)
                        {
                            currentWeight = shifted;
                            improved = true;
                            continue;
                        }
                    }

                    left.targetDv = savedLeft;
                    right.targetDv = savedRight;
                    GetTotalWeight(mockStageList);
                }

                if (!improved) break;
            }

            // Make the total exact: any leftover goes to the last stage, any overshoot is taken
            // back from it first and then from the stages above.
            float sum = 0f;
            for (int i = 0; i < optimizingStages.Count - 1; i++) sum += optimizingStages[i].targetDv;

            float lastDv = totalDv - sum;
            if (lastDv >= MinStageDv)
            {
                optimizingStages[optimizingStages.Count - 1].targetDv = lastDv;
            }
            else
            {
                float excess = MinStageDv - lastDv;
                for (int i = optimizingStages.Count - 2; i >= 0 && excess > 0f; i--)
                {
                    float take = Mathf.Min(excess, optimizingStages[i].targetDv - MinStageDv);
                    if (take <= 0f) continue;
                    optimizingStages[i].targetDv -= take;
                    excess -= take;
                }
                optimizingStages[optimizingStages.Count - 1].targetDv = MinStageDv;
            }

            GetTotalWeight(mockStageList);
        }

        /// <summary>False once an input changed; the optimised column is stale until the next run.</summary>
        private static bool resultValid;

        private static int cachedSignature = int.MinValue;
        private static float cachedMass = float.MaxValue;
        private static float cachedDv;
        private static string cachedSelectedStages = string.Empty;

        /// <summary>
        /// OnGUI runs several times per frame; recomputing the vessel mass means a full engine
        /// re-selection for every stage, so only redo it when an input actually changed.
        /// </summary>
        private static int ComputeStageSignature()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + SEO_OptimizationCache.Version;
                hash = hash * 31 + stageList.Count;

                for (int i = 0; i < stageList.Count; i++)
                {
                    StageConfig stage = stageList[i];
                    hash = hash * 31 + stage.targetDv.GetHashCode();
                    hash = hash * 31 + stage.trueDv.GetHashCode();
                    hash = hash * 31 + stage.targetTwr.GetHashCode();
                    hash = hash * 31 + stage.payloadMass.GetHashCode();
                    hash = hash * 31 + stage.additionalMass.GetHashCode();
                    hash = hash * 31 + stage.altitude.GetHashCode();
                    hash = hash * 31 + stage.constrainOp;
                    hash = hash * 31 + stage.qtyFilter;
                    hash = hash * 31 + (stage.manualMode ? 1 : 0);
                    hash = hash * 31 + (stage.keepEngine ? 2 : 0);
                    hash = hash * 31 + (stage.includeInOp ? 4 : 0);
                    hash = hash * 31 + (stage.filterT ? 8 : 0);
                    hash = hash * 31 + (stage.planetNameStr != null ? stage.planetNameStr.GetHashCode() : 0);
                    hash = hash * 31 + (stage.engineName != null ? stage.engineName.GetHashCode() : 0);
                }

                return hash;
            }
        }

        private static void RefreshStageSummaryCache()
        {
            int signature = ComputeStageSignature();
            if (signature == cachedSignature) return;

            // Inputs moved: whatever the table was showing no longer describes this vessel.
            resultValid = false;
            cachedSignature = signature;
            cachedDv = stageList.Select(m => m.trueDv).Sum();
            cachedMass = GetTotalWeight(stageList);
            cachedSelectedStages = string.Join(
                Localizer.Format("#LOC_SHIPENGOP_joint"),
                stageList.Where(s => s.includeInOp).Select(s => stageList.IndexOf(s)));
        }

        private static float CachedTotalDv()
        {
            RefreshStageSummaryCache();
            return cachedDv;
        }

        private static float CachedTotalMass()
        {
            RefreshStageSummaryCache();
            return cachedMass;
        }

        private static string CachedSelectedStageList()
        {
            RefreshStageSummaryCache();
            return cachedSelectedStages;
        }

        public static float GetTotalWeight(List<StageConfig> mockStages)
        {
            // stageList index 0 is the stage that fires first (the bottom one), so the payload
            // chain has to run from the top down: each stage lifts the fully loaded stage above it.
            for (int idx = mockStages.Count - 1; idx >= 0; idx--)
            {
                if (mockStages[idx].keepEngine && idx < mockStages.Count - 1)
                {
                    mockStages[idx].nextStageEngineQty = mockStages[idx + 1].engineQty;
                    mockStages[idx].engineName = mockStages[idx + 1].engineName;
                }

                if (idx < mockStages.Count - 1)
                {
                    mockStages[idx].payloadMass = mockStages[idx + 1].stageWeight;
                }
                else if (mockStages[idx].ownPayloadMass > 0f)
                {
                    // The top stage is never fed by the chain, so make sure a previous run's
                    // numbers cannot leak into it.
                    mockStages[idx].payloadMass = mockStages[idx].ownPayloadMass;
                }

                mockStages[idx].GetBestEngine();

                if (mockStages[idx].stageWeight > 1e6f)
                {
                    return float.MaxValue;
                }

                if (SEO_ShipReader.debugLog)
                {
                    Debug.Log(string.Format("[ShipEngineOp] chain idx={0} payload={1:F3} dv={2:F1} qty={3} engine={4} weight={5:F3}",
                        idx, mockStages[idx].payloadMass, mockStages[idx].targetDv,
                        mockStages[idx].engineQty, mockStages[idx].engineName, mockStages[idx].stageWeight));
                }
            }
            //Debug.Log("stage 0 dv = " + stageList.First().targetDv.ToString());
            //Debug.Log("stage 0 mass = " + stageList.First().stageWeight.ToString());
            return mockStages[0].stageWeight;
        }
    }
}
