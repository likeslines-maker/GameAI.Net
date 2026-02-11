using GameAI.Net.BehaviorTree;
using GameAI.Net.Core;
using GameAI.Net.Crowd;
using GameAI.Net.Emotion;
using GameAI.Net.FastCalc;
using GameAI.Net.Parallel;
using GameAI.Net.Utility;
using SlidingRank.FastOps;
using System;
using System.Diagnostics;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("GameAI.Net Sample");
        Console.WriteLine("=================");
        Console.WriteLine();

        // Scenario knobs
        const int npcs = 10_000;
        const int frames = 240; // 4 seconds at 60 FPS
        const int emotionEvery = 4; // update emotion once per 4 frames

        // Build a tiny Behavior Tree:
        // Selector( Sequence(Condition(aggro>0.5),Attack),Idle )
        var (treeJagged, treeSoA, conds) = BuildTree();

        // Allocate blackboards (Jagged + SoA)
        var (bbs, bbFloats, bbInts, bbSoA) = BuildBlackboards(npcs);

        // Utility scoring inputs (batched)
        int featureDim = 64;
        int utilityRows = 100_000;
        var (features, weights, scores) = BuildUtilityBatch(utilityRows, featureDim);

        // Emotion graph (ring) + active sources list
        int emotionNodes = 100_000;
        int activeSourcesCount = 10_000; // 10% active
        var (emotion, activeSources) = BuildEmotion(emotionNodes, activeSourcesCount);

        // Crowd candidates + quotas
        int crowdCandidates = 100_000;
        int[] quotas = new[] { 10, 30, 60 }; // total = 100
        var candidates = BuildCrowd(crowdCandidates);

        // Demonstrate individual modules once
        Console.WriteLine("Single-step demos:");
        DemoOnce(treeJagged, treeSoA, bbs, bbSoA, features, weights, scores, emotion, activeSources, candidates, quotas);
        Console.WriteLine();

        // Run a simplified "frame loop" and measure total time
        Console.WriteLine($"Running frame loop:NPCs={npcs},Frames={frames},UtilityRows={utilityRows}x{featureDim},EmotionNodes={emotionNodes},CrowdCandidates={crowdCandidates}");
        Console.WriteLine();

        RunFrameLoop_Serial_Jagged(treeJagged, bbs, bbInts, features, weights, scores, emotion, activeSources, candidates, quotas, frames, emotionEvery);
        RunFrameLoop_Serial_SoA(treeSoA, bbSoA, features, weights, scores, emotion, activeSources, candidates, quotas, frames, emotionEvery);
        RunFrameLoop_Parallel_Jagged(treeJagged, bbs, bbInts, features, weights, scores, emotion, activeSources, candidates, quotas, frames, emotionEvery);

        Console.WriteLine();
        Console.WriteLine("Done.");
    }

    private static (BehaviorTree treeJagged, BehaviorTreeSoA treeSoA, ConditionProgram[] conds) BuildTree()
    {
        var condProg = new ConditionProgram(new[]
        {
 new Instruction(OpCode.LoadFloat,argI:0),
 new Instruction(OpCode.PushConst,argF:0.5f),
 new Instruction(OpCode.Gt)
 });
        var conds = new[] { condProg };

        var nodes = new[]
        {
 new Node(NodeKind.Selector,0,0),// 0
 new Node(NodeKind.Sequence,0,0),// 1
 new Node(NodeKind.Condition,0,0),// 2 (condId=0)
 new Node(NodeKind.Action,1,0),// 3 Attack
 new Node(NodeKind.Action,0,0),// 4 Idle
 };

        var children = new[] { 1, 4, 2, 3 };
        var childStart = new[] { 0, 2, 4, 4, 4 };
        var childCount = new[] { 2, 2, 0, 0, 0 };

        var jaggedActions = new JaggedActions();
        var soaActions = new SoAActions();

        var treeJagged = new BehaviorTree(nodes, children, childStart, childCount, conds, jaggedActions);
        var treeSoA = new BehaviorTreeSoA(nodes, children, childStart, childCount, conds, soaActions);

        return (treeJagged, treeSoA, conds);
    }

    private static (Blackboard[] bbs, float[][] bbFloats, int[][] bbInts, BlackboardSoA bbSoA) BuildBlackboards(int npcs)
    {
        var rng = new Random(123);

        var bbFloats = new float[npcs][];
        var bbInts = new int[npcs][];
        var bbs = new Blackboard[npcs];

        var bbSoA = new BlackboardSoA(agentCount: npcs, floatSlots: 8, intSlots: 4);

        for (int i = 0; i < npcs; i++)
        {
            float aggro = (float)rng.NextDouble();

            bbFloats[i] = new float[8];
            bbInts[i] = new int[4];

            bbFloats[i][0] = aggro;
            bbInts[i][0] = 0;

            bbs[i] = new Blackboard(bbFloats[i], bbInts[i]);

            bbSoA.SetFloat(i, 0, aggro);
            bbSoA.SetInt(i, 0, 0);
        }

        return (bbs, bbFloats, bbInts, bbSoA);
    }

    private static (EmbeddingMatrix features, float[] weights, float[] scores) BuildUtilityBatch(int rows, int dim)
    {
        var rng = new Random(42);

        var data = new float[rows * dim];
        for (int i = 0; i < data.Length; i++)
            data[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        var features = new EmbeddingMatrix(data, rows, dim);

        var weights = new float[dim];
        for (int i = 0; i < dim; i++)
            weights[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        var scores = new float[rows];
        return (features, weights, scores);
    }

    private static (EmotionGraph graph, int[] activeSources) BuildEmotion(int nodes, int activeSourcesCount)
    {
        int edges = nodes;
        var start = new int[nodes + 1];
        var to = new int[edges];
        var w = new float[edges];

        for (int i = 0; i < nodes; i++)
        {
            start[i] = i;
            to[i] = (i + 1) % nodes;
            w[i] = 0.1f;
        }
        start[nodes] = edges;

        var graph = new EmotionGraph(nodes, start, to, w);

        var rng = new Random(7);
        for (int i = 0; i < nodes; i++)
            graph.Set(i, (float)rng.NextDouble());

        int act = Math.Min(activeSourcesCount, nodes);
        var activeSources = new int[act];
        for (int i = 0; i < act; i++) activeSources[i] = i;

        return (graph, activeSources);
    }

    private static CrowdDirector.Candidate[] BuildCrowd(int count)
    {
        var rng = new Random(99);
        var c = new CrowdDirector.Candidate[count];

        for (int i = 0; i < count; i++)
        {
            int group = i % 3;
            float score = (float)rng.NextDouble();
            c[i] = new CrowdDirector.Candidate(id: i, group: group, score: score);
        }

        return c;
    }

    private static void DemoOnce(
    BehaviorTree treeJagged,
    BehaviorTreeSoA treeSoA,
    Blackboard[] bbs,
    BlackboardSoA bbSoA,
    EmbeddingMatrix features,
    float[] weights,
    float[] scores,
    EmotionGraph emotion,
    int[] activeSources,
    CrowdDirector.Candidate[] candidates,
    int[] quotas)
    {
        // BT:tick first agent
        treeJagged.TickFast(new TickContext(1f / 60f, bbs[0]));
        treeSoA.TickFast(1f / 60f, bbSoA, 0);

        // Utility scoring
        UtilityScorer.ScoreAll(features, weights, scores);

        // Emotion active
        emotion.StepActive(activeSources, 1f / 60f, 0.98f);

        // Crowd select
        Span<int> outIds = stackalloc int[100];
        int chosen = CrowdDirector.SelectWithQuotasInto(candidates, quotas, outIds);

        Console.WriteLine($" BT lastAction(jagged)={bbs[0].GetInt(0)} lastAction(soa)={bbSoA.GetInt(0, 0)}");
        Console.WriteLine($" Utility scores[0]={scores[0]:0.000}");
        Console.WriteLine($" Emotion[0]={emotion.Get(0):0.000}");
        Console.WriteLine($" Crowd chosen={chosen} firstId={outIds[0]}");
    }

    private static void RunFrameLoop_Serial_Jagged(
    BehaviorTree tree,
    Blackboard[] bbs,
    int[][] bbInts,
    EmbeddingMatrix features,
    float[] weights,
    float[] scores,
    EmotionGraph emotion,
    int[] activeSources,
    CrowdDirector.Candidate[] candidates,
    int[] quotas,
    int frames,
    int emotionEvery)
    {
        var sw = Stopwatch.StartNew();
        float dt = 1f / 60f;
        int acc = 0;

        for (int f = 1; f <= frames; f++)
        {
            // BT tick
            for (int i = 0; i < bbs.Length; i++)
                tree.TickFast(new TickContext(dt, bbs[i]));

            // utility
            UtilityScorer.ScoreAll(features, weights, scores);

            // crowd
            Span<int> outIds = stackalloc int[100];
            int chosen = CrowdDirector.SelectWithQuotasInto(candidates, quotas, outIds);

            // emotion once per N frames
            if (emotionEvery > 0 && (f % emotionEvery) == 0)
                emotion.StepActive(activeSources, dt, 0.98f);

            acc ^= chosen ^ bbInts[0][0] ^ (int)(scores[0] * 1000f) ^(int)(emotion.Get(0) * 1000f);
    }

    sw.Stop();
 Console.WriteLine($"Serial/Jagged:total={sw.Elapsed.TotalMilliseconds:0.000} ms,perFrame={(sw.Elapsed.TotalMilliseconds/frames):0.000} ms,acc={acc}");
 }

private static void RunFrameLoop_Serial_SoA(
BehaviorTreeSoA tree,
BlackboardSoA bb,
EmbeddingMatrix features,
float[] weights,
float[] scores,
EmotionGraph emotion,
int[] activeSources,
CrowdDirector.Candidate[] candidates,
int[] quotas,
int frames,
int emotionEvery)
{
    var sw = Stopwatch.StartNew();
    float dt = 1f / 60f;
    int acc = 0;

    for (int f = 1; f <= frames; f++)
    {
        // BT tick SoA
        for (int i = 0; i < bb.AgentCount; i++)
            tree.TickFast(dt, bb, i);

        UtilityScorer.ScoreAll(features, weights, scores);

        Span<int> outIds = stackalloc int[100];
        int chosen = CrowdDirector.SelectWithQuotasInto(candidates, quotas, outIds);

        if (emotionEvery > 0 && (f % emotionEvery) == 0)
            emotion.StepActive(activeSources, dt, 0.98f);

        acc ^= chosen ^ bb.GetInt(0, 0) ^ (int)(scores[0] * 1000f) ^(int)(emotion.Get(0) * 1000f);
}

sw.Stop();
Console.WriteLine($"Serial/SoA:total={sw.Elapsed.TotalMilliseconds:0.000} ms,perFrame={(sw.Elapsed.TotalMilliseconds / frames):0.000} ms,acc={acc}");
 }

 private static void RunFrameLoop_Parallel_Jagged(
 BehaviorTree tree,
 Blackboard[] bbs,
 int[][] bbInts,
 EmbeddingMatrix features,
 float[] weights,
 float[] scores,
 EmotionGraph emotion,
 int[] activeSources,
 CrowdDirector.Candidate[] candidates,
 int[] quotas,
 int frames,
 int emotionEvery)
{
    var sw = Stopwatch.StartNew();
    float dt = 1f / 60f;
    int acc = 0;

    for (int f = 1; f <= frames; f++)
    {
        // Parallel BT
        BehaviorTreeBatchRunner.TickAllParallel(tree, bbs, dt, maxDegreeOfParallelism: 6, minBatchForParallel: 2048);

        // Parallel utility (thresholded)
        UtilityScorerParallel.ScoreAllParallel(features, weights, scores, maxDegreeOfParallelism: 6, minCountForParallel: 50_000);

        Span<int> outIds = stackalloc int[100];
        int chosen = CrowdDirector.SelectWithQuotasInto(candidates, quotas, outIds);

        if (emotionEvery > 0 && (f % emotionEvery) == 0)
            emotion.StepActive(activeSources, dt, 0.98f);

        acc ^= chosen ^ bbInts[0][0] ^ (int)(scores[0] * 1000f) ^(int)(emotion.Get(0) * 1000f);
}

sw.Stop();
Console.WriteLine($"Parallel/Jagged:total={sw.Elapsed.TotalMilliseconds:0.000} ms,perFrame={(sw.Elapsed.TotalMilliseconds / frames):0.000} ms,acc={acc}");
 }

 // Thread-safe action handlers:write only per-agent blackboard
 private sealed class JaggedActions : IActionHandler
{
    public NodeStatus Execute(int actionId, in TickContext ctx)
    {
        ctx.Blackboard.SetInt(0, actionId);
        return NodeStatus.Success;
    }
}

private sealed class SoAActions : IActionHandlerSoA
{
    public NodeStatus Execute(int actionId, in TickContextSoA ctx)
    {
        ctx.Blackboard.SetInt(ctx.Agent, 0, actionId);
        return NodeStatus.Success;
    }
}
}
