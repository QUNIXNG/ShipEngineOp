using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShipEngineOptimization
{
    /// <summary>
    /// Deterministic replacement for the random particle-swarm allocation.
    /// It minimizes a caller-supplied vessel-mass objective subject to a fixed total Δv.
    /// </summary>
    public static class AutoStageOptimizer
    {
        private const float MinStageDv = 1e-4f;
        private const float IntervalTolerance = 0.001f;
        private const float WeightImprovement = 1e-4f;
        private const int MaxGoldenIterations = 80;
        private const int MaxPairSweeps = 60;

        public static float[] Optimize(
            List<StageConfig> optimizingStages,
            Func<List<StageConfig>, float> evaluate)
        {
            if (optimizingStages == null || optimizingStages.Count == 0 || evaluate == null)
                return new float[0];

            float[] originalAllocation = ReadAllocations(optimizingStages);
            float totalDv = 0f;
            foreach (float value in originalAllocation) totalDv += value;

            int n = optimizingStages.Count;
            if (totalDv <= 0f) return Clone(originalAllocation);
            if (n == 1) return new float[] { totalDv };

            Dictionary<string, float> objectiveCache = new Dictionary<string, float>();
            float[] bestAllocation = Clone(originalAllocation);
            float bestWeight = EvaluateAllocation(optimizingStages, bestAllocation, evaluate, objectiveCache);
            bool hasValidResult = bestWeight < float.MaxValue;

            foreach (float[] start in CreateInitialCandidates(optimizingStages, originalAllocation, n, totalDv))
            {
                float[] current = Normalize(start, totalDv, n);
                float currentWeight = EvaluateAllocation(optimizingStages, current, evaluate, objectiveCache);
                if (currentWeight >= float.MaxValue) continue;

                for (int sweep = 0; sweep < MaxPairSweeps; sweep++)
                {
                    bool improved = false;

                    for (int pair = 0; pair < n - 1; pair++)
                    {
                        float otherSum = 0f;
                        for (int i = 0; i < n; i++)
                            if (i != pair && i != pair + 1) otherSum += current[i];

                        float pairTotal = totalDv - otherSum;
                        if (pairTotal <= 2f * MinStageDv) continue;

                        float lower = MinStageDv;
                        float upper = pairTotal - MinStageDv;
                        const float phi = 0.6180339887498949f;

                        float a = upper - phi * (upper - lower);
                        float b = lower + phi * (upper - lower);
                        float fa = EvaluatePairMember(optimizingStages, current, pair, pairTotal, a, evaluate, objectiveCache);
                        float fb = EvaluatePairMember(optimizingStages, current, pair, pairTotal, b, evaluate, objectiveCache);

                        float bestPair = fa <= fb ? a : b;
                        float bestPairWeight = Math.Min(fa, fb);

                        for (int iteration = 0;
                             iteration < MaxGoldenIterations && upper - lower > IntervalTolerance;
                             iteration++)
                        {
                            if (fa <= fb)
                            {
                                upper = b;
                                b = a;
                                fb = fa;
                                a = upper - phi * (upper - lower);
                                fa = EvaluatePairMember(optimizingStages, current, pair, pairTotal, a, evaluate, objectiveCache);
                            }
                            else
                            {
                                lower = a;
                                a = b;
                                fa = fb;
                                b = lower + phi * (upper - lower);
                                fb = EvaluatePairMember(optimizingStages, current, pair, pairTotal, b, evaluate, objectiveCache);
                            }

                            if (fa < bestPairWeight)
                            {
                                bestPairWeight = fa;
                                bestPair = a;
                            }
                            if (fb < bestPairWeight)
                            {
                                bestPairWeight = fb;
                                bestPair = b;
                            }
                        }

                        if (bestPairWeight < currentWeight - WeightImprovement)
                        {
                            current[pair] = bestPair;
                            current[pair + 1] = pairTotal - bestPair;
                            currentWeight = bestPairWeight;
                            improved = true;
                        }
                        else
                        {
                            // Restore the stage used by the trial evaluations.
                            EvaluateAllocation(optimizingStages, current, evaluate, objectiveCache);
                        }
                    }

                    if (!improved) break;
                }

                if (currentWeight < bestWeight)
                {
                    bestWeight = currentWeight;
                    bestAllocation = Clone(current);
                }
            }

            if (!hasValidResult && bestWeight >= float.MaxValue)
            {
                Apply(optimizingStages, originalAllocation);
                return Clone(originalAllocation);
            }

            float[] normalizedBest = Normalize(bestAllocation, totalDv, n);
            Apply(optimizingStages, normalizedBest);
            return normalizedBest;
        }

        private static IEnumerable<float[]> CreateInitialCandidates(
            List<StageConfig> optimizingStages,
            float[] originalAllocation,
            int n,
            float totalDv)
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

        private static float EvaluateAllocation(
            List<StageConfig> optimizingStages,
            float[] allocation,
            Func<List<StageConfig>, float> evaluate,
            Dictionary<string, float> objectiveCache)
        {
            string key = AllocationKey(allocation);
            float cached;
            if (objectiveCache.TryGetValue(key, out cached))
            {
                Apply(optimizingStages, allocation);
                return cached;
            }

            Apply(optimizingStages, allocation);
            float weight = evaluate(optimizingStages);
            objectiveCache[key] = weight;
            return weight;
        }

        private static float EvaluatePairMember(
            List<StageConfig> optimizingStages,
            float[] current,
            int pair,
            float pairTotal,
            float firstStageDv,
            Func<List<StageConfig>, float> evaluate,
            Dictionary<string, float> objectiveCache)
        {
            float savedLeft = current[pair];
            float savedRight = current[pair + 1];
            current[pair] = Clamp(firstStageDv, MinStageDv, pairTotal - MinStageDv);
            current[pair + 1] = pairTotal - current[pair];

            float weight = EvaluateAllocation(optimizingStages, current, evaluate, objectiveCache);

            current[pair] = savedLeft;
            current[pair + 1] = savedRight;
            return weight;
        }

        private static string AllocationKey(float[] allocation)
        {
            return string.Join(",", allocation.Select(x => x.ToString("F3", CultureInfo.InvariantCulture)));
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
