using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipEngineOptimization
{
    /// <summary>
    /// Deterministic replacement for the random particle-swarm allocation.
    /// It minimises a caller-supplied vessel-mass objective subject to a fixed total Δv.
    /// </summary>
    public static class AutoStageOptimizer
    {
        // A stage that only carries a few m/s is dead weight; keep every stage meaningful.
        internal const float MinStageDv = 30f;
        private const float IntervalTolerance = 0.001f;
        private const float WeightImprovement = 1e-4f;
        private const int MaxGoldenIterations = 80;
        private const int MaxPairSweeps = 60;

        /// <summary>Runs the search to completion. Kept for the fallback path and offline tests.</summary>
        public static float[] Optimize(
            List<StageConfig> optimizingStages,
            Func<List<StageConfig>, float> evaluate)
        {
            Run run = new Run(optimizingStages, evaluate);
            run.Step(int.MaxValue);
            return run.Best;
        }

        /// <summary>
        /// Incremental version of the same search, so the editor can spread the work over frames
        /// instead of freezing while it runs.
        /// </summary>
        public sealed class Run
        {
            private readonly List<StageConfig> optimizingStages;
            private readonly Func<List<StageConfig>, float> evaluate;
            private readonly float[] originalAllocation;
            private readonly float totalDv;
            private readonly int stageCount;
            private readonly int seedCount;
            private readonly List<float[]> seeds;
            private readonly Dictionary<ulong, float> objectiveCache = new Dictionary<ulong, float>();

            private float[] current;
            private float currentWeight;
            private float[] best;
            private float bestWeight = float.MaxValue;
            private bool hasValidResult;

            private int seedIndex;
            private int sweepIndex;
            private int pairIndex;
            private bool sweepImproved;

            public int Evaluations { get; private set; }
            public bool Finished { get; private set; }
            public bool Cancelled { get; private set; }

            public float[] Best
            {
                get
                {
                    if (best == null) return Clone(originalAllocation);
                    return Normalize(best, totalDv, stageCount);
                }
            }

            /// <summary>Rough progress in 0..1, only used to draw a progress bar.</summary>
            public float Progress
            {
                get
                {
                    if (Finished) return 1f;
                    if (stageCount < 2) return 1f;

                    float pairsPerSeed = Math.Max(1, stageCount - 1);
                    float expected = seedCount * pairsPerSeed * 8f;
                    if (expected <= 0f) return 0f;

                    float done = seedIndex * pairsPerSeed * 8f + sweepIndex * pairsPerSeed + pairIndex;
                    float value = done / expected;
                    return value < 0f ? 0f : (value > 0.99f ? 0.99f : value);
                }
            }

            public Run(List<StageConfig> optimizingStages, Func<List<StageConfig>, float> evaluate)
            {
                this.optimizingStages = optimizingStages;
                this.evaluate = evaluate;
                this.originalAllocation = ReadAllocations(optimizingStages);
                this.stageCount = optimizingStages.Count;

                float sum = 0f;
                for (int i = 0; i < originalAllocation.Length; i++) sum += originalAllocation[i];
                this.totalDv = sum;

                this.seeds = new List<float[]>(CreateInitialCandidates(originalAllocation, stageCount, totalDv));
                this.seedCount = seeds.Count;

                best = Clone(originalAllocation);

                if (stageCount <= 1 || totalDv <= 0f)
                {
                    Finished = true;
                    return;
                }

                BeginSeed();
            }

            public void Cancel()
            {
                Cancelled = true;
                Finished = true;
            }

            /// <summary>Advances the search until the evaluation budget is exhausted.</summary>
            public void Step(int evaluationBudget)
            {
                if (Finished) return;

                long budgetEnd = (long)Evaluations + Math.Max(1, evaluationBudget);

                while (!Finished && Evaluations < budgetEnd)
                {
                    if (pairIndex >= stageCount - 1)
                    {
                        if (!sweepImproved) { NextSeed(); continue; }

                        sweepIndex++;
                        pairIndex = 0;
                        sweepImproved = false;

                        if (sweepIndex >= MaxPairSweeps) { NextSeed(); }
                        continue;
                    }

                    RunPair();
                    pairIndex++;
                }
            }

            private void BeginSeed()
            {
                if (seedIndex >= seedCount)
                {
                    Finished = true;
                    if (!hasValidResult)
                    {
                        Apply(optimizingStages, originalAllocation);
                    }
                    return;
                }

                current = Normalize(seeds[seedIndex], totalDv, stageCount);
                currentWeight = EvaluateAllocation(current);
                sweepIndex = 0;
                pairIndex = 0;
                sweepImproved = false;

                if (currentWeight >= float.MaxValue)
                {
                    seedIndex++;
                    BeginSeed();
                }
            }

            private void NextSeed()
            {
                seedIndex++;
                BeginSeed();
            }

            private void RunPair()
            {
                int pair = pairIndex;

                float otherSum = 0f;
                for (int i = 0; i < stageCount; i++)
                {
                    if (i != pair && i != pair + 1) otherSum += current[i];
                }

                float pairTotal = totalDv - otherSum;
                if (pairTotal <= 2f * MinStageDv) return;

                float lower = MinStageDv;
                float upper = pairTotal - MinStageDv;
                const float phi = 0.6180339887498949f;

                float a = upper - phi * (upper - lower);
                float b = lower + phi * (upper - lower);
                float fa = EvaluatePairMember(pair, pairTotal, a);
                float fb = EvaluatePairMember(pair, pairTotal, b);

                float bestPair = fa <= fb ? a : b;
                float bestPairWeight = Math.Min(fa, fb);

                for (int iteration = 0; iteration < MaxGoldenIterations && upper - lower > IntervalTolerance; iteration++)
                {
                    if (fa <= fb)
                    {
                        upper = b;
                        b = a;
                        fb = fa;
                        a = upper - phi * (upper - lower);
                        fa = EvaluatePairMember(pair, pairTotal, a);
                    }
                    else
                    {
                        lower = a;
                        a = b;
                        fa = fb;
                        b = lower + phi * (upper - lower);
                        fb = EvaluatePairMember(pair, pairTotal, b);
                    }

                    if (fa < bestPairWeight) { bestPairWeight = fa; bestPair = a; }
                    if (fb < bestPairWeight) { bestPairWeight = fb; bestPair = b; }
                }

                if (bestPairWeight < currentWeight - WeightImprovement)
                {
                    current[pair] = bestPair;
                    current[pair + 1] = pairTotal - bestPair;
                    currentWeight = bestPairWeight;
                    sweepImproved = true;
                }
                else
                {
                    EvaluateAllocation(current);
                }

                if (currentWeight < bestWeight)
                {
                    bestWeight = currentWeight;
                    best = Clone(current);
                    hasValidResult = true;
                }
            }

            private float EvaluatePairMember(int pair, float pairTotal, float firstStageDv)
            {
                float savedLeft = current[pair];
                float savedRight = current[pair + 1];

                current[pair] = Clamp(firstStageDv, MinStageDv, pairTotal - MinStageDv);
                current[pair + 1] = pairTotal - current[pair];

                float weight = EvaluateAllocation(current);

                current[pair] = savedLeft;
                current[pair + 1] = savedRight;
                return weight;
            }

            private float EvaluateAllocation(float[] allocation)
            {
                ulong key = AllocationKey(allocation);
                float cached;
                if (objectiveCache.TryGetValue(key, out cached))
                {
                    Apply(optimizingStages, allocation);
                    return cached;
                }

                Apply(optimizingStages, allocation);
                Evaluations++;
                float weight = evaluate(optimizingStages);
                objectiveCache[key] = weight;
                return weight;
            }
        }

        private static IEnumerable<float[]> CreateInitialCandidates(
            float[] originalAllocation, int n, float totalDv)
        {
            yield return Clone(originalAllocation);

            float[] equal = new float[n];
            float[] ascending = new float[n];
            float[] descending = new float[n];
            float equalValue = totalDv / n;

            float triangularSum = 0f;
            for (int i = 1; i <= n; i++) triangularSum += i;

            for (int i = 0; i < n; i++)
            {
                equal[i] = equalValue;
                ascending[i] = totalDv * (i + 1) / triangularSum;
                descending[i] = totalDv * (n - i) / triangularSum;
            }

            yield return equal;
            yield return ascending;
            yield return descending;
        }

        private static ulong AllocationKey(float[] allocation)
        {
            // FNV-1a over the millimetre-quantized allocation: no allocations, no formatting costs.
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < allocation.Length; i++)
            {
                hash ^= (ulong)(int)(allocation[i] * 1000f + 0.5f);
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private static void Apply(List<StageConfig> optimizingStages, float[] allocation)
        {
            for (int i = 0; i < optimizingStages.Count; i++)
                optimizingStages[i].targetDv = allocation[i];
        }

        private static float[] ReadAllocations(List<StageConfig> optimizingStages)
        {
            float[] result = new float[optimizingStages.Count];
            for (int i = 0; i < result.Length; i++) result[i] = optimizingStages[i].targetDv;
            return result;
        }

        private static float[] Normalize(float[] input, float totalDv, int count)
        {
            float[] result = new float[count];
            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                result[i] = Clamp(input[i], MinStageDv, totalDv);
                sum += result[i];
            }

            if (sum <= 0f)
            {
                float equal = totalDv / count;
                for (int i = 0; i < count; i++) result[i] = equal;
                return result;
            }

            float scale = totalDv / sum;
            for (int i = 0; i < count; i++) result[i] *= scale;

            float adjusted = 0f;
            for (int i = 0; i < count - 1; i++) adjusted += result[i];
            result[count - 1] = Math.Max(MinStageDv, totalDv - adjusted);

            return result;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (max < min) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static float[] Clone(float[] input)
        {
            float[] result = new float[input.Length];
            Array.Copy(input, result, input.Length);
            return result;
        }
    }
}