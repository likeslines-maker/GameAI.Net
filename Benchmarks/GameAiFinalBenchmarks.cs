using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using GameAI.Net.Core;
using GameAI.Net.Crowd;
using GameAI.Net.Emotion;
using GameAI.Net.FastCalc;
using GameAI.Net.Parallel;
using GameAI.Net.Utility;
using SlidingRank.FastOps;
using System;
using static GameAI.Net.Crowd.CrowdDirector;

[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 10)] // разумный баланс:не часы,но стабильнее
[MemoryDiagnoser]
public class GameAiFinalBenchmarks
{
    // ----------------------------
    // Scenario definition (no combinatorial explosion)
    // ----------------------------
    public readonly record struct Scenario(
    string Name,
    int Npcs,
    int UtilityRows,
    int FeatureDim,
    int CrowdCandidates,
    int EmotionNodes,
    int EmotionEvery,
    int EmotionActiveSources,
    int MaxDop)
    {
        public override string ToString() => Name;
    }

    private Scenario Case;

    [ParamsSource(nameof(Scenarios))]
    public Scenario ScenarioCase
    {
        get => Case;
        set => Case = value;
    }

    public static Scenario[] Scenarios => new[]
    {
 new Scenario(
 Name:"Small",
 Npcs:1_000,
 UtilityRows:10_000,
 FeatureDim:32,
 CrowdCandidates:10_000,
 EmotionNodes:10_000,
 EmotionEvery:4,
 EmotionActiveSources:1_000,// 10% active
 MaxDop:6
 ),
 new Scenario(
 Name:"Medium",
 Npcs:5_000,
 UtilityRows:50_000,
 FeatureDim:64,
 CrowdCandidates:50_000,
 EmotionNodes:50_000,
 EmotionEvery:4,
 EmotionActiveSources:5_000,// 10% active
 MaxDop:6
 ),
 new Scenario(
 Name:"Large",
 Npcs:10_000,
 UtilityRows:100_000,
 FeatureDim:64,
 CrowdCandidates:100_000,
 EmotionNodes:100_000,
 EmotionEvery:4,
 EmotionActiveSources:10_000,// 10% active
 MaxDop:6
 ),
 };

    // ----------------------------
    // Behavior tree runtimes
    // ----------------------------
    private GameAI.Net.BehaviorTree.BehaviorTree _treeJagged = default!;
    private GameAI.Net.BehaviorTree.BehaviorTreeSoA _treeSoA = default!;

    private JaggedActions _actionsJagged = default!;
    private SoAActions _actionsSoA = default!;

    // Jagged blackboards
    private Blackboard[] _bbs = default!;
    private float[][] _bbFloats = default!;
    private int[][] _bbInts = default!;

    // SoA blackboard
    private BlackboardSoA _bbSoA = default!;

    // ----------------------------
    // Utility scoring
    // ----------------------------
    private EmbeddingMatrix _features;
    private float[] _weights = default!;
    private float[] _scores = default!;

    // ----------------------------
    // Emotion
    // ----------------------------
    private EmotionGraph _emotion = default!;
    private int[] _activeSources = default!;
    private int _frameCounter;

    // ----------------------------
    // Crowd
    // ----------------------------
    private CrowdDirector.Candidate[] _candidates = default!;
    private int[] _quotas = new[] { 10, 30, 60 }; // total = 100

    [GlobalSetup]
    public void Setup()
    {
        SetupTrees();
        SetupUtility();
        SetupEmotion();
        SetupCrowd();
        _frameCounter = 0;
    }

    private void SetupTrees()
    {
        // Condition:LoadFloat(0) > 0.5
        var condProg = new ConditionProgram(new[]
        {
 new Instruction(OpCode.LoadFloat,argI:0),
 new Instruction(OpCode.PushConst,argF:0.5f),
 new Instruction(OpCode.Gt)
 });
        var conds = new[] { condProg };

        // Tree:
        // 0 Selector [1,4]
        // 1 Sequence [2,3]
        // 2 Condition(0)
        // 3 Action(Attack=1)
        // 4 Action(Idle=0)
        var nodes = new[]
        {
 new GameAI.Net.BehaviorTree.Node(GameAI.Net.BehaviorTree.NodeKind.Selector,0,0),
 new GameAI.Net.BehaviorTree.Node(GameAI.Net.BehaviorTree.NodeKind.Sequence,0,0),
 new GameAI.Net.BehaviorTree.Node(GameAI.Net.BehaviorTree.NodeKind.Condition,0,0),
 new GameAI.Net.BehaviorTree.Node(GameAI.Net.BehaviorTree.NodeKind.Action,1,0),
 new GameAI.Net.BehaviorTree.Node(GameAI.Net.BehaviorTree.NodeKind.Action,0,0),
 };

        var children = new[] { 1, 4, 2, 3 };
        var childStart = new[] { 0, 2, 4, 4, 4 };
        var childCount = new[] { 2, 2, 0, 0, 0 };

        _actionsJagged = new JaggedActions();
        _treeJagged = new GameAI.Net.BehaviorTree.BehaviorTree(
        nodes, children, childStart, childCount, conds, _actionsJagged);

        _actionsSoA = new SoAActions();
        _treeSoA = new GameAI.Net.BehaviorTree.BehaviorTreeSoA(
        nodes, children, childStart, childCount, conds, _actionsSoA);

        int n = Case.Npcs;

        // Jagged allocation
        _bbFloats = new float[n][];
        _bbInts = new int[n][];
        _bbs = new Blackboard[n];

        // SoA allocation
        _bbSoA = new BlackboardSoA(agentCount: n, floatSlots: 8, intSlots: 4);

        var rng = new Random(123);
        for (int i = 0; i < n; i++)
        {
            float aggro = (float)rng.NextDouble();

            _bbFloats[i] = new float[8];
            _bbInts[i] = new int[4];

            _bbFloats[i][0] = aggro;
            _bbInts[i][0] = 0;

            _bbs[i] = new Blackboard(_bbFloats[i], _bbInts[i]);

            _bbSoA.SetFloat(i, 0, aggro);
            _bbSoA.SetInt(i, 0, 0);
        }
    }

    private void SetupUtility()
    {
        int rows = Case.UtilityRows;
        int dim = Case.FeatureDim;

        var data = new float[rows * dim];
        var rng = new Random(42);
        for (int i = 0; i < data.Length; i++)
            data[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        _features = new EmbeddingMatrix(data, rows, dim);

        _weights = new float[dim];
        for (int i = 0; i < dim; i++)
            _weights[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        _scores = new float[rows];
    }

    private void SetupEmotion()
    {
        int n = Case.EmotionNodes;
        int edges = n; // ring

        var start = new int[n + 1];
        var to = new int[edges];
        var w = new float[edges];

        for (int i = 0; i < n; i++)
        {
            start[i] = i;
            to[i] = (i + 1) % n;
            w[i] = 0.1f;
        }
        start[n] = edges;

        _emotion = new EmotionGraph(n, start, to, w);

        var rng = new Random(7);
        for (int i = 0; i < n; i++)
            _emotion.Set(i, (float)rng.NextDouble());

        int act = Math.Min(Case.EmotionActiveSources, n);
        _activeSources = new int[act];
        for (int i = 0; i < act; i++) _activeSources[i] = i;
    }

    private void SetupCrowd()
    {
        int m = Case.CrowdCandidates;
        _candidates = new CrowdDirector.Candidate[m];

        var rng = new Random(99);
        for (int i = 0; i < m; i++)
        {
            int group = i % 3;
            float score = (float)rng.NextDouble();
            _candidates[i] = new CrowdDirector.Candidate(id: i, group: group, score: score);
        }
    }

    // =========================================================
    // MICRO BENCHMARKS (modules in isolation)
    // =========================================================

    [Benchmark(Description = "BT_TickFast_Serial_Jagged")]
    public int BT_TickFast_Serial_Jagged()
    {
        float dt = 1f / 60f;
        for (int i = 0; i < Case.Npcs; i++)
        {
            var ctx = new TickContext(dt, _bbs[i]);
            _treeJagged.TickFast(ctx);
        }
        return _bbInts[0][0];
    }

    [Benchmark(Description = "BT_TickFast_Serial_SoA")]
    public int BT_TickFast_Serial_SoA()
    {
        float dt = 1f / 60f;
        for (int i = 0; i < Case.Npcs; i++)
            _treeSoA.TickFast(dt, _bbSoA, i);

        return _bbSoA.GetInt(0, 0);
    }

    [Benchmark(Description = "Utility_ScoreAll_Serial")]
    public float Utility_ScoreAll_Serial()
    {
        UtilityScorer.ScoreAll(_features, _weights, _scores);
        return _scores[0];
    }

    [Benchmark(Description = "Utility_ScoreAll_Parallel")]
    public float Utility_ScoreAll_Parallel()
    {
        UtilityScorerParallel.ScoreAllParallel(
        _features, _weights, _scores,
        maxDegreeOfParallelism: Case.MaxDop,
        minCountForParallel: 50_000);
        return _scores[0];
    }

    [Benchmark(Description = "Crowd_SelectWithQuotas_ZeroAlloc")]
    public int Crowd_SelectWithQuotas_ZeroAlloc()
    {
        Span<int> outIds = stackalloc int[100];
        return CrowdDirector.SelectWithQuotasInto(_candidates, _quotas, outIds);
    }

    [Benchmark(Description = "Emotion_StepAll")]
    public float Emotion_StepAll()
    {
        _emotion.Step(dt: 1f / 60f, damping: 0.98f);
        return _emotion.Get(0);
    }

    [Benchmark(Description = "Emotion_StepActive")]
    public float Emotion_StepActive()
    {
        _emotion.StepActive(_activeSources, dt: 1f / 60f, damping: 0.98f);
        return _emotion.Get(0);
    }

    // =========================================================
    // FRAME BUDGET (composed per-frame workload)
    // We run exactly EmotionEvery frames per invocation to make cost stable.
    // =========================================================

    [Benchmark(Description = "FrameN_Serial_Jagged")]
    public int FrameN_Serial_Jagged()
    => RunFramesJagged(frames: Math.Max(1, Case.EmotionEvery), parallel: false);

    [Benchmark(Description = "FrameN_Serial_SoA")]
    public int FrameN_Serial_SoA()
    => RunFramesSoA(frames: Math.Max(1, Case.EmotionEvery));

    [Benchmark(Description = "FrameN_Parallel_Jagged")]
    public int FrameN_Parallel_Jagged()
    => RunFramesJagged(frames: Math.Max(1, Case.EmotionEvery), parallel: true);

    private int RunFramesJagged(int frames, bool parallel)
    {
        float dt = 1f / 60f;
        int acc = 0;

        for (int f = 0; f < frames; f++)
        {
            // 1) Behavior tick
            if (!parallel)
            {
                for (int i = 0; i < Case.Npcs; i++)
                {
                    var ctx = new TickContext(dt, _bbs[i]);
                    _treeJagged.TickFast(ctx);
                }
            }
            else
            {
                BehaviorTreeBatchRunner.TickAllParallel(
                _treeJagged, _bbs, dt,
                maxDegreeOfParallelism: Case.MaxDop,
                minBatchForParallel: 2048);
            }

            // 2) Utility scoring
            if (!parallel)
                UtilityScorer.ScoreAll(_features, _weights, _scores);
            else
                UtilityScorerParallel.ScoreAllParallel(
                _features, _weights, _scores,
                maxDegreeOfParallelism: Case.MaxDop,
                minCountForParallel: 50_000);

            // 3) Crowd selection
            Span<int> outIds = stackalloc int[100];
            int chosen = CrowdDirector.SelectWithQuotasInto(_candidates, _quotas, outIds);

            // 4) Emotion (active) once per EmotionEvery frames
            _frameCounter++;
            if (Case.EmotionEvery > 0 && (_frameCounter % Case.EmotionEvery) == 0)
                _emotion.StepActive(_activeSources, dt, damping: 0.98f);

            acc ^= chosen ^ _bbInts[0][0] ^ (int)(_scores[0] * 1000f) ^(int)(_emotion.Get(0) * 1000f);
    }

 return acc;
 }

private int RunFramesSoA(int frames)
{
    float dt = 1f / 60f;
    int acc = 0;

    for (int f = 0; f < frames; f++)
    {
        // 1) Behavior tick (SoA)
        for (int i = 0; i < Case.Npcs; i++)
            _treeSoA.TickFast(dt, _bbSoA, i);

        // 2) Utility scoring
        UtilityScorer.ScoreAll(_features, _weights, _scores);

        // 3) Crowd selection
        Span<int> outIds = stackalloc int[100];
        int chosen = CrowdDirector.SelectWithQuotasInto(_candidates, _quotas, outIds);

        // 4) Emotion (active) once per EmotionEvery frames
        _frameCounter++;
        if (Case.EmotionEvery > 0 && (_frameCounter % Case.EmotionEvery) == 0)
            _emotion.StepActive(_activeSources, dt, damping: 0.98f);

        acc ^= chosen ^ _bbSoA.GetInt(0, 0) ^ (int)(_scores[0] * 1000f) ^(int)(_emotion.Get(0) * 1000f);
}

return acc;
 }

 // =========================================================
 // Action handlers (thread-safe)
 // =========================================================

 private sealed class JaggedActions : GameAI.Net.BehaviorTree.IActionHandler
{
    public NodeStatus Execute(int actionId, in TickContext ctx)
    {
        ctx.Blackboard.SetInt(0, actionId);
        return NodeStatus.Success;
    }
}

private sealed class SoAActions : GameAI.Net.BehaviorTree.IActionHandlerSoA
{
    public NodeStatus Execute(int actionId, in TickContextSoA ctx)
    {
        ctx.Blackboard.SetInt(ctx.Agent, 0, actionId);
        return NodeStatus.Success;
    }
}
}
